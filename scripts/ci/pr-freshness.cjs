// API-only controller. The write-token workflow loads this from trusted main.
async function snapshot({ github, context }, number) {
  const repo = context.repo;
  const { data: pr } = await github.rest.pulls.get({ ...repo, pull_number: number });
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
      || current.base !== base || current.behind !== 0) {
    throw new Error('PR changed or is behind its target; wait for branch synchronization and current CI');
  }
  return current;
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
