const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { resolve, verify, checkMain } = require('./verify-build.cjs');
const sha = letter => letter.repeat(40);

test('promotion callers and reusable verifier can read the merged PR provenance', () => {
  // Mocked REST tests cannot detect a missing real GITHUB_TOKEN permission.
  // A caller must allow the permission requested by its reusable workflow.
  for (const workflow of ['reuse-build.yml', 'kufr-backend-deploy.yml',
                         'kufr-memory-provision.yml',
                         'kufr-ios-sign.yml', 'kufr-ios-deploy.yml']) {
    const source = fs.readFileSync(path.resolve(__dirname, '../../.github/workflows', workflow), 'utf8');
    const permissions = source.match(/^permissions:\r?\n((?:  [^\n]*\n)+)/m)?.[1];
    assert.match(permissions ?? '', /^  pull-requests: read\s*$/m, workflow);
    assert.doesNotMatch(permissions ?? '', /pull-requests: write/, workflow);
  }
});

test('promotion transfers the original immutable artifact without a second upload', () => {
  const readWorkflow = name => fs.readFileSync(path.resolve(__dirname, '../../.github/workflows', name), 'utf8');
  const verifier = readWorkflow('reuse-build.yml');
  assert.doesNotMatch(verifier, /actions\/upload-artifact/);
  for (const output of ['artifact_id', 'run_id']) {
    assert.match(verifier, new RegExp(`value: \\$\\{\\{ jobs\\.verify\\.outputs\\.${output} \\}\\}`));
    assert.match(verifier, new RegExp(`${output}: \\$\\{\\{ steps\\.resolve\\.outputs\\.${output} \\}\\}`));
  }
  for (const workflow of ['kufr-backend-deploy.yml', 'kufr-memory-provision.yml',
                         'kufr-firmware-release.yml', 'kufr-ios-sign.yml', 'kufr-ios-deploy.yml']) {
    const source = readWorkflow(workflow);
    assert.match(source, /artifact-ids: \$\{\{ needs\.verify\.outputs\.artifact_id \}\}/, workflow);
    assert.match(source, /run-id: \$\{\{ needs\.verify\.outputs\.run_id \}\}/, workflow);
    assert.match(source, /github-token: \$\{\{ github\.token \}\}/, workflow);
    assert.match(source, /merge-multiple: true/, workflow);
    assert.doesNotMatch(source, /name: verified-/, workflow);
    assert.match(source, /scripts\/ci\/package\.py unpack/, workflow);
    assert.match(source, /verify-build\.cjs'\)\.checkMain/, workflow);
  }
});

test('routine PR builds validate packages without consuming artifact storage', () => {
  for (const workflow of ['kufr-backend.yml', 'kufr-firmware.yml', 'kufr-web.yml']) {
    const source = fs.readFileSync(path.resolve(__dirname, '../../.github/workflows', workflow), 'utf8');
    const upload = source.slice(source.indexOf('      - uses: actions/upload-artifact'));
    assert.match(upload, /if: github\.event_name == 'workflow_dispatch' && github\.ref == 'refs\/heads\/main' && !github\.event\.inputs\.pr_number/, workflow);
    assert.match(upload, /retention-days: 1\s/, workflow);
    assert.match(source, /scripts\/ci\/package\.py pack/, workflow);
  }
});

function fixture() {
  const run = { id: 123, workflow_id: 7, repository: { full_name: 'owner/repo' },
    status: 'completed', conclusion: 'success', event: 'pull_request', run_attempt: 2, head_sha: sha('a') };
  const manifest = { schema: 1, repository: 'owner/repo', run_id: 123, run_attempt: 2,
    event: 'pull_request', head_sha: sha('a'), source_sha: sha('b'), source_tree: sha('c'),
    base_sha: sha('d'), pr_number: 42, payload_sha256: 'e'.repeat(64) };
  const pr = { merged: true, base: { ref: 'main', repo: { full_name: 'owner/repo' } },
    head: { sha: sha('a'), repo: { full_name: 'owner/repo' } } };
  const source = { sha: sha('b'), tree: { sha: sha('c') }, parents: [{sha: sha('d')}, {sha: sha('a')}] };
  const main = { sha: sha('f'), tree: { sha: sha('c') } }; // Squash merge has a different SHA, identical tree.
  const artifacts = [{id: 999, name: 'backend-publish', expired: false}];
  const outputs = {};
  const github = { rest: {
    actions: { getWorkflowRun: async () => ({data: run}), getWorkflow: async () => ({data: {id: 7}}),
      listWorkflowRunArtifacts: () => {} },
    pulls: { get: async () => ({data: pr}) },
    git: { getRef: async () => ({data: {object: {sha: main.sha}}}),
      getCommit: async ({commit_sha}) => ({data: commit_sha === main.sha ? main : source}) },
  }, paginate: async () => artifacts };
  const core = { setOutput: (key, value) => { outputs[key] = value; },
    summary: { addHeading() {return this;}, addTable() {return this;}, async write() {} } };
  return {run, manifest, pr, source, main, artifacts, outputs,
    api: {github, context: {repo: {owner: 'owner', repo: 'repo'}}, core} };
}
async function verifyFixture(f, options = {}) {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'build-provenance-'));
  try {
    const file = path.join(folder, 'build-manifest.json');
    fs.writeFileSync(file, JSON.stringify(f.manifest));
    await verify(f.api, {manifestPath: file, runId: 123, workflow: 'ci.yml', ...options});
  } finally { fs.rmSync(folder, {recursive: true, force: true}); }
}

function dispatchedPrFixture() {
  const f = fixture();
  f.run.event = f.manifest.event = 'workflow_dispatch';
  f.run.head_branch = f.pr.head.ref = 'codex/feature';
  f.manifest.source_sha = f.source.sha = f.run.head_sha;
  f.source.parents = [{sha: sha('d')}];
  return f;
}
test('successful merged PR can promote its tested merge tree after a squash merge', async () => {
  const f = fixture();
  await resolve(f.api, {runId: '123', workflow: 'ci.yml', artifact: 'backend-publish'});
  await verifyFixture(f);
  assert.equal(f.outputs.artifact_id, 999);
  assert.equal(f.outputs.main_sha, sha('f'));
});
for (const [description, mutate] of [
  ['failed run', f => {f.run.conclusion = 'failure';}],
  ['different workflow', f => {f.run.workflow_id = 8;}],
  ['different repository', f => {f.run.repository.full_name = 'attacker/repo';}],
  ['push event', f => {f.run.event = 'push';}],
  ['expired artifact', f => {f.artifacts[0].expired = true;}],
  ['wrong artifact name', f => {f.artifacts[0].name = 'unexpected';}],
]) test(`resolver rejects ${description}`, async () => {
  const f = fixture(); mutate(f);
  await assert.rejects(resolve(f.api, {runId: '123', workflow: 'ci.yml', artifact: 'backend-publish'}));
});
for (const invalid of ['-1', '1;echo pwn', '', '1e3', '9007199254740993']) {
  test(`resolver rejects invalid run ID ${JSON.stringify(invalid)}`, async () => {
    await assert.rejects(resolve(fixture().api, {runId: invalid, workflow: 'ci.yml', artifact: 'backend-publish'}));
  });
}
for (const [description, mutate] of [
  ['unmerged PR', f => {f.pr.merged = false;}],
  ['fork PR', f => {f.pr.head.repo.full_name = 'attacker/repo';}],
  ['wrong base branch', f => {f.pr.base.ref = 'dev';}],
  ['outdated PR head', f => {f.pr.head.sha = sha('d');}],
  ['changed main tree', f => {f.main.tree.sha = sha('d');}],
  ['forged source tree', f => {f.manifest.source_tree = sha('d');}],
  ['unrelated source commit', f => {f.source.parents[1].sha = sha('d');}],
  ['wrong run metadata', f => {f.manifest.run_id = 124;}],
  ['future run attempt', f => {f.manifest.run_attempt = 3;}],
  ['changed run conclusion', f => {f.run.conclusion = 'failure';}],
]) test(`promotion rejects ${description}`, async () => {
  const f = fixture(); mutate(f); await assert.rejects(verifyFixture(f));
});
test('recovery build on main can be promoted', async () => {
  const f = fixture();
  f.run.event = f.manifest.event = 'workflow_dispatch'; f.run.head_branch = 'main';
  f.run.head_sha = f.manifest.head_sha = f.manifest.source_sha;
  f.manifest.pr_number = f.manifest.base_sha = null;
  await verifyFixture(f);
});

test('merged PR dispatch can promote its exact head tree after a squash merge', async () => {
  const f = dispatchedPrFixture();
  await resolve(f.api, {runId: '123', workflow: 'ci.yml', artifact: 'backend-publish'});
  await verifyFixture(f);
  assert.equal(f.outputs.source_sha, f.run.head_sha);
  assert.equal(f.outputs.main_sha, sha('f'));
});

for (const [description, mutate] of [
  ['unmerged PR', f => {f.pr.merged = false;}],
  ['fork PR', f => {f.pr.head.repo.full_name = 'attacker/repo';}],
  ['wrong base branch', f => {f.pr.base.ref = 'dev';}],
  ['wrong base repository', f => {f.pr.base.repo.full_name = 'attacker/repo';}],
  ['wrong PR head', f => {f.pr.head.sha = sha('d');}],
  ['wrong PR branch', f => {f.pr.head.ref = 'codex/different';}],
  ['missing branch metadata', f => {delete f.run.head_branch; delete f.pr.head.ref;}],
  ['source different from run head', f => {f.manifest.source_sha = sha('b');}],
  ['manifest head different from run head', f => {f.manifest.head_sha = sha('b');}],
  ['invalid PR number', f => {f.manifest.pr_number = '42';}],
  ['missing PR number', f => {f.manifest.pr_number = null;}],
  ['invalid base SHA', f => {f.manifest.base_sha = 'not-a-sha';}],
  ['missing base SHA', f => {f.manifest.base_sha = null;}],
  ['different repository', f => {f.run.repository.full_name = 'attacker/repo';}],
  ['changed main tree', f => {f.main.tree.sha = sha('d');}],
  ['forged source tree', f => {f.source.tree.sha = sha('d');}],
]) test(`PR dispatch promotion rejects ${description}`, async () => {
  const f = dispatchedPrFixture(); mutate(f); await assert.rejects(verifyFixture(f));
});
test('manual build from a feature branch is rejected', async () => {
  const f = fixture();
  f.run.event = f.manifest.event = 'workflow_dispatch'; f.run.head_branch = 'feature';
  f.run.head_sha = f.manifest.head_sha = f.manifest.source_sha;
  f.manifest.pr_number = f.manifest.base_sha = null;
  await assert.rejects(verifyFixture(f));
});
test('development IPA cannot be published as Ad Hoc', async () => {
  const f = fixture(); f.manifest.signing_kind = 'development';
  await assert.rejects(verifyFixture(f, {signingKind: 'ad_hoc'}));
});
test('main is checked again after a deployment queue delay', async () => {
  const f = fixture();
  await checkMain(f.api, f.main.sha);
  await assert.rejects(checkMain(f.api, sha('d')));
});

test('retrying only failed jobs can reuse an artifact from an earlier successful job attempt', async () => {
  const f = fixture(); f.manifest.run_attempt = 1;
  await verifyFixture(f);
});
test('dispatch from a commit superseded by main is rejected', async () => {
  const f = fixture(); f.api.context.sha = sha('d');
  await assert.rejects(verifyFixture(f));
});
