const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { resolve, verify, checkMain } = require('./verify-build.cjs');
const sha = letter => letter.repeat(40);

function fixture() {
  const repository = { full_name: 'owner/repo' };
  const run = { id: 123, workflow_id: 7, repository, head_repository: repository,
    status: 'completed', conclusion: 'success', event: 'workflow_dispatch',
    run_attempt: 2, head_branch: 'main', head_sha: sha('a') };
  const manifest = { schema: 1, repository: 'owner/repo', run_id: 123, run_attempt: 2,
    event: 'workflow_dispatch', head_sha: sha('a'), source_sha: sha('a'), source_tree: sha('c'),
    base_sha: null, pr_number: null, payload_sha256: 'e'.repeat(64) };
  const source = { sha: sha('a'), tree: { sha: sha('c') } };
  const main = { sha: sha('a'), tree: { sha: sha('c') } };
  const artifacts = [{ id: 999, name: 'backend-publish', expired: false, digest: 'sha256:' + 'd'.repeat(64) }];
  const outputs = {};
  const requests = [];
  const github = { rest: {
    actions: { getWorkflowRun: async request => { requests.push(request); return { data: run }; },
      getWorkflow: async () => ({ data: { id: 7 } }), listWorkflowRunArtifacts: () => {} },
    git: { getRef: async request => { requests.push(request); return { data: { object: { sha: main.sha } } }; },
      getCommit: async ({ commit_sha }) => ({ data: commit_sha === main.sha ? main : source }) },
  }, paginate: async () => artifacts };
  const core = { setOutput: (key, value) => { outputs[key] = value; },
    summary: { addHeading() { return this; }, addTable() { return this; }, async write() {} } };
  return { run, manifest, source, main, artifacts, outputs, requests,
    api: { github, context: { repo: { owner: 'owner', repo: 'repo' } }, core } };
}

async function verifyFixture(f, options = {}) {
  const folder = fs.mkdtempSync(path.join(os.tmpdir(), 'build-provenance-'));
  try {
    const file = path.join(folder, 'build-manifest.json');
    fs.writeFileSync(file, JSON.stringify(f.manifest));
    await verify(f.api, { manifestPath: file, runId: '123', workflow: 'ci.yml', ...options });
  } finally { fs.rmSync(folder, { recursive: true, force: true }); }
}

test('successful direct main build exposes immutable artifact ID and digest', async () => {
  const f = fixture();
  await resolve(f.api, { runId: '123', workflow: 'ci.yml', artifact: 'backend-publish' });
  await verifyFixture(f);
  assert.equal(f.outputs.artifact_id, 999);
  assert.equal(f.outputs.artifact_digest, f.artifacts[0].digest);
  assert.equal(f.outputs.main_sha, sha('a'));
});

for (const [description, mutate] of [
  ['failed run', f => { f.run.conclusion = 'failure'; }],
  ['different workflow', f => { f.run.workflow_id = 8; }],
  ['different repository', f => { f.run.repository = { full_name: 'attacker/repo' }; }],
  ['fork head repository', f => { f.run.head_repository = { full_name: 'attacker/repo' }; }],
  ['PR run even after merge', f => { f.run.event = 'pull_request'; }],
  ['push event', f => { f.run.event = 'push'; }],
  ['feature branch dispatch', f => { f.run.head_branch = 'feature'; }],
  ['expired artifact', f => { f.artifacts[0].expired = true; }],
  ['ambiguous artifact', f => { f.artifacts.push({ ...f.artifacts[0], id: 1000 }); }],
  ['wrong artifact name', f => { f.artifacts[0].name = 'unexpected'; }],
  ['missing GitHub digest', f => { delete f.artifacts[0].digest; }],
  ['invalid immutable ID', f => { f.artifacts[0].id = '999'; }],
]) test(`resolver rejects ${description}`, async () => {
  const f = fixture(); mutate(f);
  await assert.rejects(resolve(f.api, { runId: '123', workflow: 'ci.yml', artifact: 'backend-publish' }));
});

for (const invalid of ['-1', '1;echo pwn', '', '1e3', '9007199254740993']) {
  test(`resolver rejects invalid run ID ${JSON.stringify(invalid)}`, async () => {
    await assert.rejects(resolve(fixture().api, { runId: invalid, workflow: 'ci.yml', artifact: 'backend-publish' }));
  });
}

for (const [description, mutate] of [
  ['PR manifest on a main dispatch', f => { f.manifest.pr_number = 42; }],
  ['PR base metadata', f => { f.manifest.base_sha = sha('d'); }],
  ['manifest PR event', f => { f.manifest.event = 'pull_request'; }],
  ['changed main tree', f => { f.main.tree.sha = sha('d'); }],
  ['changed main commit with identical tree', f => { f.main.sha = sha('f'); }],
  ['forged source tree', f => { f.manifest.source_tree = sha('d'); }],
  ['wrong source commit', f => { f.manifest.source_sha = sha('b'); }],
  ['wrong head commit', f => { f.manifest.head_sha = sha('b'); }],
  ['wrong repository', f => { f.manifest.repository = 'attacker/repo'; }],
  ['wrong run metadata', f => { f.manifest.run_id = 124; }],
  ['future run attempt', f => { f.manifest.run_attempt = 3; }],
  ['invalid digest', f => { f.manifest.payload_sha256 = 'invalid'; }],
]) test(`promotion rejects ${description}`, async () => {
  const f = fixture(); mutate(f); await assert.rejects(verifyFixture(f));
});

test('private promotion reads public source and never compares private commit with public commit', async () => {
  const f = fixture();
  f.api.context.repo = { owner: 'owner', repo: 'deployment' };
  f.api.context.sha = sha('f');
  await verifyFixture(f, { sourceRepo: 'owner/repo' });
  await checkMain(f.api, f.main.sha, 'owner/repo');
  assert.ok(f.requests.every(request => request.repo === 'repo'));
});

test('development IPA cannot be published as Ad Hoc', async () => {
  const f = fixture(); f.manifest.signing_kind = 'development';
  await assert.rejects(verifyFixture(f, { signingKind: 'ad_hoc' }));
});

test('Ad Hoc IPA on current main is accepted', async () => {
  const f = fixture(); f.manifest.signing_kind = 'ad_hoc';
  await verifyFixture(f, { workflow: 'kufr-ios-sign.yml', signingKind: 'ad_hoc' });
});

test('main is checked again after a queue delay', async () => {
  const f = fixture();
  await checkMain(f.api, f.main.sha);
  await assert.rejects(checkMain(f.api, sha('d')));
});

test('earlier successful job attempt in a successful retry remains valid', async () => {
  const f = fixture(); f.manifest.run_attempt = 1; await verifyFixture(f);
});

test('public dispatch superseded by main is rejected', async () => {
  const f = fixture(); f.api.context.sha = sha('d'); await assert.rejects(verifyFixture(f));
});
