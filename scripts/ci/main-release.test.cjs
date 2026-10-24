'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { run, releaseRequest, requestTitle } = require('./main-release.cjs');
const repository = 'Duloc-Dungeon/DildoVander';
const sha = 'a'.repeat(40);
const workflowIds = { 'main-release.yml': 1, 'ci.yml': 2, 'kufr-ios-sign.yml': 3, 'kufr-ios-app-store.yml': 4 };

function fixture() {
  const calls = [];
  const outputs = {};
  const children = [];
  let clock = 0;
  let main = sha;
  let nextId = 100;
  const context = {
    repo: { owner: 'Duloc-Dungeon', repo: 'DildoVander' }, ref: 'refs/heads/main',
    sha, eventName: 'push', runId: 50, runAttempt: 1,
  };
  function makeRun(workflow, changes = {}) {
    return {
      id: nextId++, run_attempt: 1, repository: { full_name: repository },
      head_repository: { full_name: repository }, head_sha: sha, head_branch: 'main',
      workflow_id: workflowIds[workflow], event: 'workflow_dispatch',
      display_title: requestTitle(releaseRequest(context.runId)), status: 'completed', conclusion: 'success',
      ...changes,
    };
  }
  const own = makeRun('main-release.yml', { id: context.runId, event: context.eventName });
  const core = {
    info(message) { calls.push(['info', message]); },
    setOutput(name, value) { outputs[name] = value; },
    summary: { addHeading() { return this; }, addTable() { return this; }, async write() {} },
  };
  const actions = {
    async getWorkflow({ workflow_id }) { return { data: { id: workflowIds[workflow_id] } }; },
    async getWorkflowRun({ run_id }) { return { data: run_id === own.id ? own : children.find(item => item.id === run_id) }; },
    async listWorkflowRuns(args) {
      calls.push(['list', args]);
      return { data: { workflow_runs: children.filter(item => item.workflow_id === workflowIds[args.workflow_id]) } };
    },
    async createWorkflowDispatch(args) {
      calls.push(['dispatch', args]);
      children.push(makeRun(args.workflow_id));
    },
  };
  const api = { github: { rest: { actions, git: { async getRef() { return { data: { object: { sha: main } } }; } } } }, context, core };
  const options = { now: () => clock, sleep: async ms => { clock += ms; }, timeoutMs: 100, pollMs: 10, discoveryTimeoutMs: 30 };
  return {
    api, actions, options, calls, outputs, children, own, makeRun,
    supersede() { main = 'b'.repeat(40); },
    dispatches() { return calls.filter(item => item[0] === 'dispatch').map(item => item[1]); },
  };
}

test('direct current main release dispatches genuine all-project CI then both signing workflows', async () => {
  const f = fixture();
  const result = await run(f.api, f.options);
  assert.equal(result.outcome, 'success');
  const dispatches = f.dispatches();
  assert.deepEqual(dispatches.map(item => item.workflow_id), ['ci.yml', 'kufr-ios-sign.yml', 'kufr-ios-app-store.yml']);
  assert.deepEqual(dispatches[0].inputs, { release_request: releaseRequest(50), project: 'all' });
  for (const dispatch of dispatches) {
    assert.equal(dispatch.ref, 'main');
    assert.equal(`${dispatch.owner}/${dispatch.repo}`, repository);
  }
  for (const dispatch of dispatches.slice(1)) assert.equal(dispatch.inputs.build_run_id, String(result.ci_run_id));
  assert.equal(result.adhoc_run_id, f.children[1].id);
  assert.equal(result.app_store_run_id, f.children[2].id);
  assert.equal(f.outputs.controller_run_attempt, 1);
});

test('manual recovery uses the same guards', async () => {
  const f = fixture();
  f.api.context.eventName = f.own.event = 'workflow_dispatch';
  assert.equal((await run(f.api, f.options)).outcome, 'success');
});

test('runner environment supplies the run attempt when toolkit context omits it', async () => {
  const f = fixture();
  delete f.api.context.runAttempt;
  const previous = process.env.GITHUB_RUN_ATTEMPT;
  try {
    process.env.GITHUB_RUN_ATTEMPT = '1';
    assert.equal((await run(f.api, f.options)).controller_run_attempt, 1);
    process.env.GITHUB_RUN_ATTEMPT = '2';
    await assert.rejects(run(f.api, f.options), /Controller attempt changed/);
  } finally {
    if (previous == null) delete process.env.GITHUB_RUN_ATTEMPT;
    else process.env.GITHUB_RUN_ATTEMPT = previous;
  }
});

test('rejects wrong repository, ref, event and immutable controller identity before dispatch', async () => {
  for (const mutate of [
    f => { f.api.context.repo.owner = 'fork'; },
    f => { f.api.context.ref = 'refs/heads/feature'; },
    f => { f.api.context.eventName = 'pull_request'; },
    f => { f.own.head_sha = 'b'.repeat(40); },
    f => { f.own.workflow_id = 99; },
    f => { f.own.head_repository.full_name = 'fork/source'; },
    f => { f.own.run_attempt = 2; },
  ]) {
    const f = fixture(); mutate(f);
    await assert.rejects(run(f.api, f.options));
    assert.equal(f.dispatches().length, 0);
  }
});

test('superseded before dispatch exits cleanly without producing promotable success', async () => {
  const f = fixture(); f.supersede();
  assert.equal((await run(f.api, f.options)).outcome, 'superseded');
  assert.equal(f.dispatches().length, 0);
});

test('main changing during a running child stops without cancellation or later dispatch', async () => {
  const f = fixture();
  f.options.sleep = async () => { f.supersede(); };
  f.actions.createWorkflowDispatch = async args => {
    f.calls.push(['dispatch', args]);
    f.children.push(f.makeRun(args.workflow_id, { status: 'in_progress', conclusion: null }));
  };
  assert.equal((await run(f.api, f.options)).outcome, 'superseded');
  assert.equal(f.dispatches().length, 1);
  assert.equal(f.children[0].status, 'in_progress');
});

test('main changes at child completion stop promotion', async () => {
  const f = fixture();
  const get = f.actions.getWorkflowRun;
  f.actions.getWorkflowRun = async args => {
    const result = await get(args);
    if (args.run_id !== f.own.id) f.supersede();
    return result;
  };
  assert.equal((await run(f.api, f.options)).outcome, 'superseded');
  assert.equal(f.dispatches().length, 1);
});

test('failed, cancelled, skipped and timed-out children block later stages', async () => {
  for (const conclusion of ['failure', 'cancelled', 'skipped', 'timed_out', 'neutral']) {
    const f = fixture();
    f.children.push(f.makeRun('ci.yml', { conclusion }));
    await assert.rejects(run(f.api, f.options), /completed with/);
    assert.equal(f.outputs.outcome, 'failure');
    assert.equal(f.dispatches().length, 0);
  }
});

test('reruns reuse one exact correlated child per stage without duplicate dispatches', async () => {
  const f = fixture();
  await run(f.api, f.options);
  f.api.context.runAttempt = f.own.run_attempt = 2;
  assert.equal((await run(f.api, f.options)).outcome, 'success');
  assert.equal(f.dispatches().length, 3);
});

test('correlation never selects an unrelated same-SHA run', async () => {
  const f = fixture();
  f.children.push(f.makeRun('ci.yml', { display_title: 'Manual main build' }));
  const receipt = await run(f.api, f.options);
  assert.notEqual(receipt.ci_run_id, f.children[0].id);
  assert.equal(f.dispatches().length, 3);
});

test('ambiguous correlation fails before dispatch and when duplicates appear later', async () => {
  const f = fixture();
  f.children.push(f.makeRun('ci.yml'), f.makeRun('ci.yml'));
  await assert.rejects(run(f.api, f.options), /Ambiguous/);
  assert.equal(f.dispatches().length, 0);
  const late = fixture();
  let reads = 0;
  const list = late.actions.listWorkflowRuns;
  late.actions.listWorkflowRuns = async args => {
    if (++reads === 3) late.children.push(late.makeRun('ci.yml'));
    return list(args);
  };
  await assert.rejects(run(late.api, late.options), /Ambiguous/);
  assert.equal(late.dispatches().length, 1);
});

test('rejects lookalike titles with wrong dispatch provenance or repository', async () => {
  for (const changes of [
    { event: 'push' }, { event: 'pull_request' }, { head_sha: 'b'.repeat(40) },
    { head_branch: 'feature' }, { repository: { full_name: 'fork/repo' } },
    { head_repository: { full_name: 'fork/repo' } },
  ]) {
    const f = fixture(); f.children.push(f.makeRun('ci.yml', changes));
    await assert.rejects(run(f.api, f.options), /identity or dispatch provenance/);
    assert.equal(f.dispatches().length, 0);
  }
});

test('missing child and a running child have bounded deadlines', async () => {
  const missing = fixture();
  missing.actions.createWorkflowDispatch = async args => { missing.calls.push(['dispatch', args]); };
  await assert.rejects(run(missing.api, missing.options), /Timed out correlating/);
  assert.equal(missing.dispatches().length, 1);
  const running = fixture();
  running.children.push(running.makeRun('ci.yml', { status: 'in_progress', conclusion: null }));
  await assert.rejects(run(running.api, running.options), /deadline exceeded/);
  assert.equal(running.dispatches().length, 0);
});

test('an uncertain dispatch response is never automatically retried', async () => {
  const f = fixture();
  const dispatch = f.actions.createWorkflowDispatch;
  f.actions.createWorkflowDispatch = async args => { await dispatch(args); throw new Error('network response lost'); };
  await assert.rejects(run(f.api, f.options), /network response lost/);
  assert.equal(f.dispatches().length, 1);
  f.actions.createWorkflowDispatch = dispatch;
  assert.equal((await run(f.api, f.options)).outcome, 'success');
  assert.equal(f.dispatches().length, 3);
});

test('release request is a stable bounded UUID and rejects arbitrary input', () => {
  assert.equal(releaseRequest(50), releaseRequest(50));
  assert.notEqual(releaseRequest(50), releaseRequest(51));
  assert.equal(releaseRequest(50).length, 36);
  for (const value of ['', 'x'.repeat(500), '${{ secrets.TOKEN }}', '$(touch /tmp/file)', null]) {
    assert.throws(() => requestTitle(value), /Invalid release request/);
  }
  for (const value of [0, -1, NaN, '1;echo', Number.MAX_SAFE_INTEGER + 1]) assert.throws(() => releaseRequest(value));
});

test('workflow grants only public token dispatch and preserves direct child contracts', () => {
  const root = path.join(__dirname, '../..');
  const controller = fs.readFileSync(path.join(root, '.github/workflows/main-release.yml'), 'utf8');
  assert.match(controller, /push:\s+branches: \[main\]/);
  assert.match(controller, /workflow_dispatch:/);
  assert.match(controller, /cancel-in-progress: false/);
  assert.match(controller, /actions: write/);
  assert.match(controller, /ref: \$\{\{ github.sha \}\}/);
  assert.doesNotMatch(controller, /secrets\.|self-hosted|environment:|repository_dispatch|pull_request_target/);
  for (const workflow of ['ci.yml', 'kufr-ios-sign.yml', 'kufr-ios-app-store.yml']) {
    const source = fs.readFileSync(path.join(root, '.github/workflows', workflow), 'utf8');
    assert.match(source, /release_request:\s+description:.*\s+type: string/);
    assert.match(source, /format\('Main release \| \{0\}', inputs.release_request\)/);
  }
});
