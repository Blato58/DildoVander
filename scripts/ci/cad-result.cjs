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

function relevantSource(entry) {
  // Artifacts identify a run, not its uploading job. A different job's changed
  // application/test code can upload a counterfeit result with the same name.
  // Compare the whole executable/dependency surface across the run, permitting
  // only the passive tracking data that the tracking CLI parses without exec.
  const trackingData = /^docs\/project\/(?:(?:work-items|evidence|targets|releases)\/[^/]+\.json|STATUS\.md|project\.json)$/;
  return !(entry.type === 'blob' && entry.mode === '100644' && trackingData.test(entry.path));
}

function sourceInventory(tree) {
  if (!Array.isArray(tree)) throw new Error('Missing source tree');
  const inventory = new Map();
  for (const entry of tree) {
    if (typeof entry.path !== 'string' || !entry.path || entry.path.includes('\0')
      || entry.path.split('/').some(part => !part || part === '.' || part === '..')) {
      throw new Error('Invalid source path');
    }
    if (!relevantSource(entry) || entry.type === 'tree') continue;
    if (!/^[a-f0-9]{40}$/.test(entry.sha)
      || !((entry.type === 'blob' && ['100644', '100755', '120000'].includes(entry.mode))
        || (entry.type === 'commit' && entry.mode === '160000'))
      || inventory.has(entry.path)) throw new Error('Incomplete source metadata');
    inventory.set(entry.path, `${entry.mode} ${entry.type} ${entry.sha}`);
  }
  if (!requiredSources.every(path => inventory.has(path))
    || ![...inventory.keys()].some(path => path.startsWith('kufr/mechanika/final/'))) {
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

async function authenticatedBuiltSha(github, repo, reference, currentInventory) {
  const sha = reference.sha;
  const { data: commit } = await github.rest.git.getCommit({ ...repo, commit_sha: sha });
  if (commit.sha !== sha || !/^[a-f0-9]{40}$/.test(commit.tree?.sha)) return;
  const { data: tree } = await github.rest.git.getTree({
    ...repo, tree_sha: commit.tree.sha, recursive: 'true',
  });
  if (tree.truncated !== false || tree.sha !== commit.tree.sha) return;
  if (JSON.stringify(sourceInventory(tree.tree)) === JSON.stringify(await currentInventory())) return sha;
}

function isAncestorOfHead(sha) {
  try {
    execFileSync('git', ['merge-base', '--is-ancestor', sha, 'HEAD'], { stdio: 'pipe' });
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
    const builtSha = await authenticatedBuiltSha(github, repo, reference, currentInventory);
    if (builtSha) return { scope: 'merged-pr', number: pr.number, mergeSha: pr.merge_commit_sha, builtSha };
    return;
  }
  const pr = context.payload?.pull_request;
  if (context.eventName !== 'pull_request' || !Number.isSafeInteger(pr?.number) || pr.number <= 0
    || typeof pr.head?.repo?.full_name !== 'string' || !pr.head.repo.full_name
    || run.head_repository?.full_name !== pr.head.repo.full_name
    || reference.number !== pr.number) return;
  const builtSha = await authenticatedBuiltSha(github, repo, reference, currentInventory);
  if (builtSha) return { scope: 'same-pr', number: pr.number, builtSha };
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
    core.info(`CAD trusted result: ${provenance.scope} run ${id}, artifact ${artifact.id}`);
    return;
  }
  core.info('CAD trusted result: no eligible successful artifact; safe miss');
}

module.exports = { resolve };
