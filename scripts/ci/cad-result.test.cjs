const test = require('node:test');
const assert = require('node:assert/strict');
const { resolve } = require('./cad-result.cjs');
const key = 'a'.repeat(64);

function fixture(overrides = {}) {
  const outputs = {};
  const artifact = { id: 8, name: `cad-result-v1-${key}`, expired: false,
    workflow_run: { id: 7, head_branch: 'main' } };
  const run = { status: 'completed', conclusion: 'success', head_branch: 'main',
    head_repository: { full_name: 'Blato58/DildoVander' }, event: 'push',
    path: '.github/workflows/kufr-cad.yml', ...overrides };
  return { outputs, artifact, run, options: {
    context: { repo: { owner: 'Blato58', repo: 'DildoVander' } },
    core: { setOutput: (name, value) => { outputs[name] = value; }, info: () => {} },
    github: { rest: { actions: {
      listArtifactsForRepo: async () => ({ data: { artifacts: [artifact] } }),
      getWorkflowRun: async () => ({ data: run }),
    } } },
  } };
}

test('trusted immutable successful main artifact selected', async () => {
  const f = fixture();
  await resolve(f.options, { key });
  assert.equal(f.outputs['artifact-id'], '8');
  assert.equal(f.outputs.exact, 'true');
});

test('PR, fork, failed, running, other workflow and expired results rejected', async () => {
  for (const overrides of [{ event: 'pull_request' }, { event: 'pull_request_target' },
    { head_repository: { full_name: 'fork/DildoVander' } }, { conclusion: 'failure' },
    { status: 'in_progress' }, { head_branch: 'feature' }, { path: '.github/workflows/unrelated.yml' }]) {
    const f = fixture(overrides);
    await resolve(f.options, { key });
    assert.deepEqual(f.outputs, {});
  }
  const f = fixture();
  f.artifact.expired = true;
  await resolve(f.options, { key });
  assert.deepEqual(f.outputs, {});
});

test('force performs no lookup and malformed identity fails', async () => {
  const f = fixture();
  f.options.github.rest.actions.listArtifactsForRepo = () => { throw Error('unexpected lookup'); };
  await resolve(f.options, { key, force: true });
  assert.deepEqual(f.outputs, {});
  await assert.rejects(resolve(f.options, { key: 'wrong' }), /Invalid/);
});

test('missing artifact identity and unavailable discovery are safe misses', async () => {
  for (const change of [
    f => { delete f.artifact.id; },
    f => { delete f.artifact.expired; },
    f => { delete f.artifact.workflow_run.id; },
    f => { f.artifact.name = 'cad-result-v1-invalid'; },
    f => { f.options.github.rest.actions.listArtifactsForRepo = async () => ({ data: {} }); },
    f => { f.options.github.rest.actions.listArtifactsForRepo = async () => { throw Error('unavailable'); }; },
  ]) {
    const f = fixture();
    change(f);
    await resolve(f.options, { key });
    assert.deepEqual(f.outputs, {});
  }
});

test('different manufacturing key can seed part exports but never exact acceptance', async () => {
  const f = fixture();
  f.artifact.name = `cad-result-v1-${'b'.repeat(64)}`;
  await resolve(f.options, { key });
  assert.equal(f.outputs.exact, 'false');
});

function prFixture() {
  const f = fixture({
    event: 'pull_request', head_branch: 'feature', path: '.github/workflows/ci.yml',
    pull_requests: [{ number: 123 }], head_sha: 'b'.repeat(40),
    referenced_workflows: [{
      path: 'Blato58/DildoVander/.github/workflows/kufr-cad.yml@refs/pull/123/merge',
      sha: 'c'.repeat(40),
    }],
  });
  f.artifact.name = `cad-pr-result-v1-${key}`;
  f.artifact.workflow_run.head_branch = 'feature';
  Object.assign(f.options.context, {
    eventName: 'pull_request',
    payload: { pull_request: { number: 123, head: { repo: { full_name: 'Blato58/DildoVander' } } } },
  });
  const paths = [
    '.github/workflows/kufr-cad.yml', '.github/workflows/ci.yml',
    '.github/actions/verified-cad/action.yml',
    'scripts/ci/cad-result.cjs', 'scripts/ci/cad_result.py',
    'scripts/ci/test_cad_impact.py', 'kufr/mobilni-aplikace/.nvmrc',
    'kufr/mechanika/rozmery-doruceneho-kufru.md',
    'kufr/mechanika/rozmery-hulek.md',
    'kufr/mechanika/rozmery-nakoupenych-soucastek.md',
    'kufr/docs/zapojeni-potvrzeneho-hardwaru.md',
    '.gitattributes', '.gitmodules',
    'kufr/mechanika/final/generate.py',
    'kufr/mechanika/final/presentation/viewer.js',
    'kufr/mechanika/final/tests/test_geometry.py',
    'kufr/mechanika/final/README.md',
    'kufr/mechanika/final/exports/ignored.stl',
    'docs/project/STATUS.md',
    'docs/project/artifacts/check.cjs',
    'kufr/mobilni-aplikace/package.json',
    'kufr/backend-tests/Tests.cs',
  ];
  f.tree = { sha: 'd'.repeat(40), truncated: false,
    tree: paths.map(path => ({ path, mode: '100644', type: 'blob', sha: 'e'.repeat(40) })) };
  f.currentTree = structuredClone(f.tree.tree);
  f.settings = { key, readTree: () => f.currentTree };
  f.options.github.rest.git = {
    getCommit: async args => {
      assert.equal(args.commit_sha, 'c'.repeat(40), 'must use referenced executed merge SHA, not run.head_sha');
      return { data: { sha: args.commit_sha, tree: { sha: 'd'.repeat(40) } } };
    },
    getTree: async args => {
      assert.equal(args.tree_sha, 'd'.repeat(40));
      assert.equal(args.recursive, 'true');
      return { data: f.tree };
    },
  };
  return f;
}

test('same PR source-identical merge tree reuses immutable result across tracking changes', async () => {
  const f = prFixture();
  f.currentTree.find(entry => entry.path === 'docs/project/STATUS.md').sha = 'f'.repeat(40);
  // Sibling merge commits need no shared merge-base assumption; compare tree bytes.
  await resolve(f.options, f.settings);
  assert.equal(f.outputs['artifact-id'], '8');
  assert.equal(f.outputs.exact, 'true');
});

test('forged matching PR key cannot hide changed code, tests, docs, workflow or modes', async () => {
  for (const path of [
    'kufr/mechanika/final/generate.py',
    'kufr/mechanika/final/presentation/viewer.js',
    'kufr/mechanika/final/tests/test_geometry.py',
    'kufr/mechanika/final/README.md',
    '.github/workflows/ci.yml', 'scripts/ci/test_cad_impact.py',
    '.gitattributes', '.gitmodules', 'kufr/mobilni-aplikace/.nvmrc',
    'kufr/docs/zapojeni-potvrzeneho-hardwaru.md',
    'kufr/mobilni-aplikace/package.json', 'kufr/backend-tests/Tests.cs',
    'docs/project/artifacts/check.cjs', 'kufr/mechanika/final/exports/ignored.stl',
  ]) {
    const f = prFixture();
    f.currentTree.find(entry => entry.path === path).sha = 'f'.repeat(40);
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {}, path);
  }
  const f = prFixture();
  f.currentTree.find(entry => entry.path.endsWith('generate.py')).mode = '100755';
  await resolve(f.options, f.settings);
  assert.deepEqual(f.outputs, {});
  const executableTracking = prFixture();
  executableTracking.currentTree.find(entry => entry.path === 'docs/project/STATUS.md').mode = '100755';
  await resolve(executableTracking.options, executableTracking.settings);
  assert.deepEqual(executableTracking.outputs, {});
});

test('same PR provenance rejects other PR, fork source, events and workflows', async () => {
  for (const change of [
    f => { f.run.pull_requests = [{ number: 456 }]; },
    f => { f.run.head_repository.full_name = 'fork/DildoVander'; },
    f => { f.run.event = 'pull_request_target'; },
    f => { f.run.path = '.github/workflows/unrelated.yml'; },
    f => { f.run.conclusion = 'failure'; },
    f => { f.run.status = 'in_progress'; },
    f => { f.run.referenced_workflows[0].path = 'fork/DildoVander/.github/workflows/kufr-cad.yml@refs/pull/123/merge'; },
    f => { f.artifact.name = `cad-pr-result-v1-${'b'.repeat(64)}`; },
    f => { f.options.context.eventName = 'push'; },
    f => { f.options.context.eventName = 'workflow_dispatch'; },
  ]) {
    const f = prFixture();
    change(f);
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {});
  }
});

test('fork PR may reuse its own verified source but its result never enters main trust', async () => {
  const f = prFixture();
  f.run.head_repository.full_name = 'fork/DildoVander';
  f.options.context.payload.pull_request.head.repo.full_name = 'fork/DildoVander';
  await resolve(f.options, f.settings);
  assert.equal(f.outputs['artifact-id'], '8');
  delete f.outputs['artifact-id']; delete f.outputs['run-id']; delete f.outputs.exact;
  f.options.context.eventName = 'push';
  f.options.context.ref = 'refs/heads/main';
  await resolve(f.options, f.settings);
  assert.deepEqual(f.outputs, {});
  f.artifact.name = `cad-result-v1-${key}`;
  f.run.event = 'push'; f.run.head_branch = 'main';
  f.artifact.workflow_run.head_branch = 'main';
  await resolve(f.options, f.settings);
  assert.deepEqual(f.outputs, {}, 'fork run cannot impersonate canonical main');
});

test('missing, ambiguous, malformed or truncated source metadata is a safe miss', async () => {
  for (const change of [
    f => { delete f.run.pull_requests; },
    f => { delete f.options.context.payload.pull_request.head; },
    f => { delete f.run.referenced_workflows; },
    f => { f.run.referenced_workflows.push(f.run.referenced_workflows[0]); },
    f => { f.run.referenced_workflows[0].sha = 'invalid'; },
    f => { f.tree.truncated = true; },
    f => { delete f.tree.truncated; },
    f => { delete f.tree.tree; },
    f => { delete f.tree.sha; },
    f => { f.tree.tree = []; },
    f => { f.tree.tree = f.tree.tree.filter(entry => !entry.path.endsWith('cad_result.py')); },
    f => { delete f.tree.tree[0].mode; },
    f => { delete f.tree.tree[0].sha; },
    f => { f.tree.tree[0].type = 'unknown'; },
    f => { f.currentTree.push({ path: 'scripts/ci/extra.sh', mode: '100755', type: 'blob', sha: 'f'.repeat(40) }); },
    f => { f.options.github.rest.git.getCommit = async () => { throw new Error('not found'); }; },
    f => { f.options.github.rest.git.getTree = async () => { throw new Error('not found'); }; },
    f => { f.options.github.rest.actions.getWorkflowRun = async () => { throw new Error('not found'); }; },
  ]) {
    const f = prFixture();
    change(f);
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {});
  }
});

test('force bypasses PR provenance and tree reads', async () => {
  const f = prFixture();
  f.options.github.rest.actions.listArtifactsForRepo = () => { throw Error('unexpected lookup'); };
  f.settings.readTree = () => { throw Error('unexpected git'); };
  await resolve(f.options, { ...f.settings, force: true });
  assert.deepEqual(f.outputs, {});
});
