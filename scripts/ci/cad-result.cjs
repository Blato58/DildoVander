'use strict';

const { execFileSync } = require('node:child_process');
const mainPrefix = 'cad-result-v1-';
const prPrefix = 'cad-pr-result-v1-';
const cadWorkflow = '.github/workflows/kufr-cad.yml';
const requiredSources = [
  cadWorkflow, '.github/actions/verified-cad/action.yml',
  'scripts/ci/cad-result.cjs', 'scripts/ci/cad_result.py',
  'kufr/mobilni-aplikace/.nvmrc',
  'kufr/mechanika/rozmery-doruceneho-kufru.md',
  'kufr/mechanika/rozmery-hulek.md',
  'kufr/mechanika/rozmery-nakoupenych-soucastek.md',
  'kufr/docs/zapojeni-potvrzeneho-hardwaru.md',
];

function sourceInventory(tree, requireCadSources = true) {
  if (!Array.isArray(tree)) throw new Error('Missing source tree');
  const inventory = new Map();
  for (const entry of tree) {
    if (typeof entry.path !== 'string' || !entry.path || entry.path.includes('\0')
      || entry.path.split('/').some(part => !part || part === '.' || part === '..')) {
      throw new Error('Invalid source path');
    }
    // Artifacts identify a run, not its uploading job. Compare the entire
    // source surface so another job cannot upload a counterfeit result.
    if (entry.type === 'tree') continue;
    if (!/^[a-f0-9]{40}$/.test(entry.sha)
      || !((entry.type === 'blob' && ['100644', '100755', '120000'].includes(entry.mode))
        || (entry.type === 'commit' && entry.mode === '160000'))
      || inventory.has(entry.path)) throw new Error('Incomplete source metadata');
    inventory.set(entry.path, `${entry.mode} ${entry.type} ${entry.sha}`);
  }
  if (requireCadSources && (!requiredSources.every(path => inventory.has(path))
    || ![...inventory.keys()].some(path => path.startsWith('kufr/mechanika/final/')))) {
    throw new Error('Incomplete CAD source tree');
  }
  return [...inventory].sort(([a], [b]) => a.localeCompare(b));
}

function readCurrentTree() {
  const output = execFileSync('git', ['ls-tree', '-r', '-z', 'HEAD'], {
    encoding: 'utf8', maxBuffer: 32 * 1024 * 1024,
  });
  return output.split('\0').filter(Boolean).map(line => {
    const match = /^(\d{6}) (blob|commit) ([a-f0-9]{40})\t([\s\S]+)$/.exec(line);
    if (!match) throw new Error('Invalid checkout tree');
    return { mode: match[1], type: match[2], sha: match[3], path: match[4] };
  });
}

function cadReference(repo, run) {
  // For a reusable workflow, head_sha is the PR source head, not the executed
  // merge tree. Only GitHub's referenced workflow SHA authenticates that tree.
  const canonicalPath = `${repo.owner}/${repo.repo}/${cadWorkflow}@`;
  const references = run.referenced_workflows?.filter(workflow =>
    typeof workflow.path === 'string' && workflow.path.startsWith(canonicalPath));
  if (references?.length !== 1 || !/^[a-f0-9]{40}$/.test(references[0].sha)) return;
  const match = /^refs\/pull\/([1-9][0-9]*)\/merge$/.exec(references[0].ref);
  const number = match && Number(match[1]);
  if (!Number.isSafeInteger(number)) return;
  return { sha: references[0].sha, number };
}

async function serverCommitInventory(github, repo, sha, requireCadSources = true) {
  const { data: commit } = await github.rest.git.getCommit({ ...repo, commit_sha: sha });
  if (commit.sha !== sha || !/^[a-f0-9]{40}$/.test(commit.tree?.sha)) throw new Error('Missing commit tree');
  const { data: tree } = await github.rest.git.getTree({
    ...repo, tree_sha: commit.tree.sha, recursive: 'true',
  });
  if (tree.truncated !== false || tree.sha !== commit.tree.sha) throw new Error('Incomplete commit tree');
  return { commit, inventory: sourceInventory(tree.tree, requireCadSources) };
}

async function inheritedMainChanges(github, repo, run, built, current, isAncestor) {
  if (`${repo.owner}/${repo.repo}` !== 'Blato58/DildoVander') return false;
  const parents = built.commit.parents;
  if (!Array.isArray(parents) || parents.length !== 2
    || !parents.every(parent => /^[a-f0-9]{40}$/.test(parent.sha))
    || parents[1].sha !== run.head_sha || parents[0].sha === parents[1].sha) return false;
  const { data: mainRef } = await github.rest.git.getRef({ ...repo, ref: 'heads/main' });
  if (mainRef.ref !== 'refs/heads/main' || mainRef.object?.type !== 'commit'
    || !/^[a-f0-9]{40}$/.test(mainRef.object.sha)) return false;
  const mainSha = mainRef.object.sha;
  const baseSha = parents[0].sha;
  if (await isAncestor(baseSha, mainSha) !== true || await isAncestor(mainSha) !== true) return false;
  // Main may not yet contain this PR's new CAD tooling. Missing paths are part
  // of the proof, so these two complete historical trees need no CAD manifest.
  const base = await serverCommitInventory(github, repo, baseSha, false);
  const main = await serverCommitInventory(github, repo, mainSha, false);
  const builtEntries = new Map(built.inventory);
  const currentEntries = new Map(current);
  const baseEntries = new Map(base.inventory);
  const mainEntries = new Map(main.inventory);
  for (const path of new Set([...builtEntries.keys(), ...currentEntries.keys()])) {
    if (builtEntries.get(path) === currentEntries.get(path)) continue;
    // A differing path must have been untouched by the original PR and now
    // match trusted main exactly, including absence, file type and mode.
    if (builtEntries.get(path) !== baseEntries.get(path)
      || currentEntries.get(path) !== mainEntries.get(path)) return false;
  }
  return { baseSha, mainSha };
}

async function authenticatedSource(github, repo, run, reference, currentInventory, isAncestor) {
  const built = await serverCommitInventory(github, repo, reference.sha);
  const current = await currentInventory();
  if (JSON.stringify(built.inventory) === JSON.stringify(current)) {
    return { builtSha: reference.sha, basis: 'exact' };
  }
  const inherited = await inheritedMainChanges(github, repo, run, built, current, isAncestor);
  if (inherited) return { builtSha: reference.sha, basis: 'inherited-main', ...inherited };
}

function isAncestorOfHead(sha, descendant = 'HEAD') {
  try {
    execFileSync('git', ['merge-base', '--is-ancestor', sha, descendant], { stdio: 'pipe' });
    return true;
  } catch (error) {
    if (error.status === 1) return false;
    throw error;
  }
}

async function prProvenance({ github, context }, repo, run, currentInventory, isAncestor, promote) {
  const canonical = `${repo.owner}/${repo.repo}`;
  if (run.event !== 'pull_request' || run.path !== '.github/workflows/ci.yml'
    || run.repository?.full_name !== canonical) return;
  const reference = cadReference(repo, run);
  if (!reference) return;
  // GitHub can clear pull_requests after merge. The executed reusable workflow's
  // server-supplied merge ref still binds the result to its original PR.
  if (run.pull_requests !== undefined && (!Array.isArray(run.pull_requests)
    || (run.pull_requests.length > 0 && !run.pull_requests.some(pr => pr.number === reference.number)))) return;
  if (promote) {
    const { data: pr } = await github.rest.pulls.get({ ...repo, pull_number: reference.number });
    if (pr.number !== reference.number || pr.merged !== true || pr.base?.ref !== 'main'
      || pr.base.repo?.full_name !== canonical || !/^[a-f0-9]{40}$/.test(pr.merge_commit_sha)
      || typeof pr.head?.repo?.full_name !== 'string' || !pr.head.repo.full_name
      || pr.head.repo.full_name !== run.head_repository?.full_name
      || await isAncestor(pr.merge_commit_sha) !== true) return;
    const source = await authenticatedSource(github, repo, run, reference, currentInventory, isAncestor);
    if (source) return { scope: 'merged-pr', number: pr.number, mergeSha: pr.merge_commit_sha, ...source };
    return;
  }
  const pr = context.payload?.pull_request;
  if (context.eventName !== 'pull_request' || !Number.isSafeInteger(pr?.number) || pr.number <= 0
    || typeof pr.head?.repo?.full_name !== 'string' || !pr.head.repo.full_name
    || run.head_repository?.full_name !== pr.head.repo.full_name
    || reference.number !== pr.number) return;
  const source = await authenticatedSource(github, repo, run, reference, currentInventory, isAncestor);
  if (source) return { scope: 'same-pr', number: pr.number, ...source };
}

// PR results require independently authenticated source bytes. Promotion into
// main additionally requires a server-confirmed merge already in this history.
async function resolve({ github, context, core }, {
  key, force = false, readTree = readCurrentTree, isAncestor = isAncestorOfHead,
}) {
  if (!/^[a-f0-9]{64}$/.test(key)) throw new Error('Invalid CAD result key');
  if (force) return;
  const repo = { owner: context.repo.owner, repo: context.repo.repo };
  const canonical = `${repo.owner}/${repo.repo}`;
  // Bounded discovery: expiration/old results are safe misses, never acceptance.
  let artifacts;
  try {
    const { data } = await github.rest.actions.listArtifactsForRepo({ ...repo, per_page: 100 });
    artifacts = data.artifacts;
  } catch {
    core.info('CAD trusted result: artifact lookup unavailable; safe miss');
    return;
  }
  const isPr = context.eventName === 'pull_request';
  const promote = canonical === 'Blato58/DildoVander' && context.ref === 'refs/heads/main'
    && ['push', 'workflow_dispatch'].includes(context.eventName);
  const candidates = (Array.isArray(artifacts) ? artifacts : []).filter(artifact =>
    artifact.expired === false && Number.isSafeInteger(artifact.id) && artifact.id > 0
    && typeof artifact.name === 'string' && (/^cad-result-v1-[a-f0-9]{64}$/.test(artifact.name)
      || ((isPr || promote) && artifact.name === `${prPrefix}${key}`)));
  const exact = artifact => [mainPrefix, prPrefix].some(prefix => artifact.name === `${prefix}${key}`);
  const priority = artifact => exact(artifact) ? (artifact.name.startsWith(mainPrefix) ? 2 : 1) : 0;
  candidates.sort((a, b) => priority(b) - priority(a) || b.id - a.id);
  let inventory;
  const currentInventory = async () => inventory ??= sourceInventory(await readTree());
  for (const artifact of candidates.slice(0, 20)) {
    const id = artifact.workflow_run?.id;
    if (!Number.isSafeInteger(id) || id <= 0) continue;
    let trusted = false;
    let provenance = { scope: 'main' };
    try {
      const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: id });
      if (run.status !== 'completed' || run.conclusion !== 'success') continue;
      if (artifact.name.startsWith(mainPrefix)) {
        trusted = artifact.workflow_run.head_branch === 'main' && run.head_branch === 'main'
          && run.head_repository?.full_name === canonical
          && ['push', 'workflow_dispatch'].includes(run.event)
          && [cadWorkflow, '.github/workflows/ci.yml'].includes(run.path);
      } else {
        provenance = await prProvenance({ github, context }, repo, run, currentInventory, isAncestor, promote);
        trusted = Boolean(provenance);
      }
    } catch {
      // Missing metadata, inaccessible merge commits and truncated trees are misses.
      core.info(`CAD trusted result: provenance unavailable for run ${id}; safe miss`);
    }
    if (!trusted) continue;
    core.setOutput('artifact-id', String(artifact.id));
    core.setOutput('run-id', String(id));
    core.setOutput('exact', String(exact(artifact)));
    core.setOutput('scope', provenance.scope);
    if (provenance.number) core.setOutput('source-pr', String(provenance.number));
    if (provenance.mergeSha) core.setOutput('source-merge-sha', provenance.mergeSha);
    if (provenance.builtSha) core.setOutput('source-built-sha', provenance.builtSha);
    if (provenance.basis) core.setOutput('source-basis', provenance.basis);
    if (provenance.baseSha) core.setOutput('source-base-sha', provenance.baseSha);
    if (provenance.mainSha) core.setOutput('source-main-sha', provenance.mainSha);
    core.info(`CAD trusted result: ${provenance.scope} run ${id}, artifact ${artifact.id}`);
    return;
  }
  core.info('CAD trusted result: no eligible successful artifact; safe miss');
}

module.exports = { resolve };
