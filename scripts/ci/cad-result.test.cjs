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

test('different manufacturing key can seed part exports but never exact acceptance', async () => {
  const f = fixture();
  f.artifact.name = `cad-result-v1-${'b'.repeat(64)}`;
  await resolve(f.options, { key });
  assert.equal(f.outputs.exact, 'false');
});
