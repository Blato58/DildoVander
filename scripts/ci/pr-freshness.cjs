// API-only controller. The write-token workflow loads this from trusted main.
async function snapshot({ github, context }, number) {
  const repo = context.repo;
  const { data: pr } = await github.rest.pulls.get({ ...repo, pull_number: number,
    headers: { 'X-GitHub-Api-Version': '2026-03-10' } });
  const { data: base } = await github.rest.repos.getBranch({ ...repo, branch: pr.base.ref });
  const { data: comparison } = await github.rest.repos.compareCommits({
    ...repo, base: base.commit.sha, head: pr.head.sha,
  });
  return { pr, base: base.commit.sha, behind: comparison.behind_by };
}

async function check(api, { number, head, base }) {
  if (!/^[1-9]\d*$/.test(String(number)) || !Number.isSafeInteger(Number(number))
      || ![head, base].every(sha => /^[a-f0-9]{40}$/.test(sha || ''))) {
    throw new Error('Expected a PR number and full head/base SHA');
  }
  const current = await snapshot(api, Number(number));
  if (current.pr.state !== 'open' || current.pr.head.sha !== head
      || current.behind !== 0) {
    throw new Error('PR changed or is behind its target; wait for branch synchronization and current CI');
  }
  if (current.base !== base) await checkNativeBaseRace(api, current, { number: Number(number), head, base });
  return current;
}

function nativeContext(pr) {
  const stack = pr?.stack;
  if (!stack || !['id', 'number', 'position', 'size'].every(key =>
    Number.isSafeInteger(stack[key]) && stack[key] > 0)
      || stack.position > stack.size || !stack.base?.ref
      || !/^[a-f0-9]{40}$/.test(stack.base.sha || '')) return null;
  return { id: stack.id, number: stack.number, position: stack.position, size: stack.size,
    base: { ref: stack.base.ref, sha: stack.base.sha } };
}

async function nativePrefix(api, current, native) {
  const { github, context } = api;
  const { data: stacks } = await github.request('GET /repos/{owner}/{repo}/stacks', {
    ...context.repo, pull_request: current.pr.number,
    headers: { 'X-GitHub-Api-Version': '2026-03-10' },
  });
  const stack = Array.isArray(stacks) && stacks.length === 1 && stacks[0];
  const entries = stack?.pull_requests;
  if (!stack || stack.id !== native.id || stack.number !== native.number
      || stack.base?.ref !== native.base.ref || !Array.isArray(entries) || entries.length !== native.size
      || new Set(entries.map(entry => entry.number)).size !== entries.length) {
    throw new Error('Native stack membership changed during branch synchronization');
  }
  const prefix = entries.slice(0, native.position).map(entry => {
    if (!Number.isSafeInteger(entry.number) || entry.number <= 0 || !entry.head?.ref
        || !/^[a-f0-9]{40}$/.test(entry.head.sha || '')) throw new Error('Incomplete native stack prefix');
    return { number: entry.number, ref: entry.head.ref, sha: entry.head.sha };
  });
  const selected = prefix.at(-1);
  const { data: trunk } = await github.rest.repos.getBranch({ ...context.repo, branch: native.base.ref });
  if (selected.number !== current.pr.number || selected.ref !== current.pr.head.ref
      || selected.sha !== current.pr.head.sha || trunk.commit.sha !== native.base.sha
      || (native.position > 1 && (prefix.at(-2).ref !== current.pr.base.ref
        || prefix.at(-2).sha !== current.base))
      || (native.position === 1 && (native.base.ref !== current.pr.base.ref
        || native.base.sha !== current.base))) throw new Error('Native prefix or trunk is no longer current');
  return prefix;
}

async function checkNativeBaseRace(api, current, { number, head, base }) {
  const { github, context } = api;
  const event = context.payload.pull_request;
  const native = nativeContext(event);
  const fullName = `${context.repo.owner}/${context.repo.repo}`;
  if (context.eventName !== 'pull_request' || !native
      || JSON.stringify(nativeContext(current.pr)) !== JSON.stringify(native)
      || event.number !== number || event.head?.sha !== head || event.base?.sha !== base
      || event.head.ref !== current.pr.head.ref || event.base.ref !== current.pr.base.ref
      || event.head.repo?.full_name !== fullName || current.pr.head.repo?.full_name !== fullName) {
    throw new Error('PR target changed outside a matching native stack event');
  }
  const prefix = await nativePrefix(api, current, native);
  // Both merge-checkout inputs must already be in head. Project selection still
  // uses the pinned native trunk, never the historical immediate event base.
  const { data: comparison } = await github.rest.repos.compareCommits({ ...context.repo, base, head });
  const refreshed = await snapshot(api, number);
  if (comparison.behind_by !== 0 || refreshed.pr.state !== 'open' || refreshed.pr.head.sha !== head
      || refreshed.pr.head.ref !== current.pr.head.ref || refreshed.pr.base.ref !== current.pr.base.ref
      || refreshed.pr.head.repo?.full_name !== fullName || refreshed.base !== current.base
      || refreshed.behind !== 0 || JSON.stringify(nativeContext(refreshed.pr)) !== JSON.stringify(native)
      || JSON.stringify(await nativePrefix(api, refreshed, native)) !== JSON.stringify(prefix)) {
    throw new Error('Native stack changed or event base is not contained in the current head');
  }
}

async function dispatch(api, current) {
  const { github, context, core } = api;
  await github.rest.actions.createWorkflowDispatch({
    ...context.repo, workflow_id: 'ci.yml', ref: current.pr.head.ref,
    inputs: { pr_number: String(current.pr.number), expected_head_sha: current.pr.head.sha,
      expected_base_sha: current.base },
  });
  core.info(`PR #${current.pr.number}: dispatched CI at ${current.pr.head.sha}`);
}

async function synchronize(api, number) {
  const { github, context, core } = api;
  const repo = context.repo;
  let current = await snapshot(api, number);
  const fullName = `${repo.owner}/${repo.repo}`;
  if (current.pr.state !== 'open' || current.pr.draft
      || current.pr.head.repo?.full_name !== fullName) {
    core.info(`PR #${number}: closed, draft or fork; no automatic update`);
    return;
  }
  if (!current.behind) {
    if (context.eventName === 'workflow_dispatch' || context.runAttempt > 1) await dispatch(api, current);
    return;
  }
  const previousHead = current.pr.head.sha;
  // expected_head_sha prevents overwriting a concurrent author update.
  await github.rest.pulls.updateBranch({ ...repo, pull_number: number, expected_head_sha: previousHead });
  for (let attempt = 0; attempt < 15; attempt++) {
    current = await snapshot(api, number);
    if (current.pr.state !== 'open') return;
    if (current.pr.head.sha !== previousHead && !current.behind) {
      // Explicit dispatch runs automatically; token-generated PR events need approval.
      await dispatch(api, current);
      return;
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(`PR #${number}: branch update did not settle; rerun synchronization`);
}

async function run(api) {
  const { github, context, core } = api;
  // Actions replaces older pending runs even with cancel-in-progress:false.
  // Reconcile the whole target so a replaced PR event cannot lose its update.
  const base = context.payload.pull_request?.base.ref || 'main';
  const numbers = (await github.paginate(github.rest.pulls.list, {
    ...context.repo, state: 'open', base, per_page: 100,
  })).map(pr => pr.number);
  let failed = false;
  for (const number of numbers) {
    try { await synchronize(api, number); }
    catch (error) {
      core.error(`PR #${number}: ${error.message}`);
      failed = true;
    }
  }
  if (failed) core.setFailed('Some PR branches could not be updated; resolve conflicts or rerun synchronization');
}

module.exports = { snapshot, check, synchronize, run };
