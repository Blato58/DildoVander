const { test } = require('node:test');
const assert = require('node:assert/strict');
const { check, synchronize, run } = require('./pr-freshness.cjs');
const sha = letter => letter.repeat(40);

function fixture() {
  const pr = { number: 7, state: 'open', draft: false,
    head: { sha: sha('a'), ref: 'feature', repo: { full_name: 'owner/repo' } },
    base: { ref: 'main' } };
  const calls = [];
  let behind = 1;
  const api = {
    context: { repo: { owner: 'owner', repo: 'repo' }, payload: { pull_request: pr } },
    core: { info() {}, error() {}, setFailed(message) { calls.push(['failed', message]); } },
    github: { rest: {
      pulls: {
        list: () => {},
        get: async () => ({ data: pr }),
        updateBranch: async args => {
          calls.push(['update', args]);
          pr.head.sha = sha('c'); behind = 0;
        },
      },
      repos: {
        getBranch: async () => ({ data: { commit: { sha: sha('b') } } }),
        compareCommits: async () => ({ data: { behind_by: behind } }),
      },
      actions: { createWorkflowDispatch: async args => { calls.push(['dispatch', args]); } },
    }, paginate: async () => [{ number: 7 }] },
  };
  return { api, pr, calls, fresh() { behind = 0; } };
}

test('update uses expected head and dispatches settled head/base', async () => {
  const { api, calls } = fixture();
  await synchronize(api, 7);
  assert.equal(calls[0][1].expected_head_sha, sha('a'));
  assert.deepEqual(calls[1][1].inputs,
    { pr_number: '7', expected_head_sha: sha('c'), expected_base_sha: sha('b') });
  assert.equal(calls[1][1].ref, 'feature');
});

test('fresh branches do not get duplicate CI on normal events', async () => {
  const { api, calls, fresh } = fixture(); fresh();
  await synchronize(api, 7);
  assert.deepEqual(calls, []);
});

test('accepted update waits for new head and dispatches the latest target SHA', async () => {
  const { api, pr, calls } = fixture();
  let reads = 0;
  const get = api.github.rest.pulls.get;
  api.github.rest.pulls.get = async args => {
    reads++;
    if (reads >= 3) pr.head.sha = sha('c');
    return get(args);
  };
  api.github.rest.pulls.updateBranch = async args => { calls.push(['update', args]); };
  api.github.rest.repos.getBranch = async () => ({
    data: { commit: { sha: reads >= 3 ? sha('d') : sha('b') } },
  });
  api.github.rest.repos.compareCommits = async () => ({ data: { behind_by: reads >= 3 ? 0 : 1 } });
  await synchronize(api, 7);
  assert.equal(calls[1][1].inputs.expected_head_sha, sha('c'));
  assert.equal(calls[1][1].inputs.expected_base_sha, sha('d'));
});

test('rerun after update/dispatch failure recovers fresh PR CI', async () => {
  const { api, calls, fresh } = fixture(); fresh(); api.context.runAttempt = 2;
  await synchronize(api, 7);
  assert.equal(calls[0][0], 'dispatch');
});

test('draft, closed and fork PRs are never modified', async () => {
  for (const change of [pr => { pr.draft = true; }, pr => { pr.state = 'closed'; },
    pr => { pr.head.repo.full_name = 'fork/repo'; }]) {
    const { api, pr, calls } = fixture(); change(pr);
    await synchronize(api, 7);
    assert.deepEqual(calls, []);
  }
});

test('conflicts fail synchronization without dispatch', async () => {
  const { api, calls } = fixture();
  api.github.rest.pulls.updateBranch = async () => { throw new Error('merge conflict'); };
  await run(api);
  assert.equal(calls.length, 1);
  assert.equal(calls[0][0], 'failed');
});

test('freshness rejects stale head, moved base, behind branch and malformed inputs', async () => {
  const { api, fresh } = fixture(); fresh();
  await check(api, { number: '7', head: sha('a'), base: sha('b') });
  for (const args of [
    { number: '7', head: sha('c'), base: sha('b') },
    { number: '7', head: sha('a'), base: sha('c') },
    { number: 'invalid', head: sha('a'), base: sha('b') },
    { number: '7', head: '', base: sha('b') },
  ]) await assert.rejects(check(api, args));
  const stale = fixture();
  await assert.rejects(check(stale.api, { number: '7', head: sha('a'), base: sha('b') }));
});

test('push inspects all open main PRs and continues after one conflict', async () => {
  const { api, calls } = fixture(); api.context.payload = {};
  api.github.rest.pulls.list = () => {};
  api.github.paginate = async (method, args) => {
    assert.equal(args.base, 'main'); return [{ number: 8 }, { number: 7 }];
  };
  const get = api.github.rest.pulls.get;
  api.github.rest.pulls.get = async args => {
    if (args.pull_number === 8) throw new Error('conflict');
    return get(args);
  };
  await run(api);
  assert.deepEqual(calls.map(call => call[0]), ['update', 'dispatch', 'failed']);
});

test('PR event reconciles every PR for its target when pending events are replaced', async () => {
  const { api, calls } = fixture();
  api.context.payload.pull_request.base.ref = 'integration';
  api.github.paginate = async (method, args) => {
    assert.equal(args.base, 'integration'); return [{ number: 7 }, { number: 8 }];
  };
  const visited = [];
  const get = api.github.rest.pulls.get;
  api.github.rest.pulls.get = async args => { visited.push(args.pull_number); return get(args); };
  await run(api);
  assert.ok(visited.includes(7));
  assert.ok(visited.includes(8));
  assert.ok(calls.some(call => call[0] === 'dispatch'));
});

function nativeFixture() {
  const setup = fixture(); setup.fresh();
  const { api, pr } = setup;
  pr.base = { ref: 'lower', sha: sha('b') };
  pr.stack = { id: 1, number: 9, position: 2, size: 2, base: { ref: 'main', sha: sha('d') } };
  api.context.eventName = 'pull_request';
  api.context.payload.pull_request = structuredClone(pr);
  api.context.payload.pull_request.base.sha = sha('c');
  const stack = { id: 1, number: 9, base: { ref: 'main' }, pull_requests: [
    { number: 6, head: { ref: 'lower', sha: sha('b') } },
    { number: 7, head: structuredClone(pr.head) },
  ] };
  api.github.rest.pulls.get = async () => ({ data: structuredClone(pr) });
  api.github.rest.repos.getBranch = async ({ branch }) => ({ data: {
    commit: { sha: branch === 'main' ? sha('d') : sha('b') },
  } });
  api.github.request = async () => ({ data: [structuredClone(stack)] });
  return { ...setup, stack, args: { number: '7', head: sha('a'), base: sha('c') } };
}

test('native push accepts historical immediate base only with unchanged exact prefix and both bases in head', async () => {
  const { api, calls, args } = nativeFixture();
  const comparisons = [];
  api.github.rest.repos.compareCommits = async input => {
    comparisons.push(input); return { data: { behind_by: 0 } };
  };
  const result = await check(api, args);
  assert.equal(result.base, sha('b'));
  assert.deepEqual(comparisons.map(item => item.base), [sha('b'), sha('c'), sha('b')]);
  assert.deepEqual(calls, []);
});

test('native historical base rejects unrelated contexts, cohort changes and non-ancestor event base', async () => {
  const changes = [
    f => { f.api.context.eventName = 'workflow_dispatch'; },
    f => { f.api.context.payload.pull_request.stack.id = 2; },
    f => { f.api.context.payload.pull_request.stack.position = 1; },
    f => { f.api.context.payload.pull_request.stack.size = 3; },
    f => { f.api.context.payload.pull_request.stack.base.sha = sha('e'); },
    f => { f.api.context.payload.pull_request.base.ref = 'other'; },
    f => { f.api.context.payload.pull_request.head.ref = 'other'; },
    f => { f.api.context.payload.pull_request.head.repo.full_name = 'fork/repo'; },
    f => { f.pr.head.repo.full_name = 'fork/repo'; },
    f => { f.stack.pull_requests.reverse(); },
    f => { f.stack.pull_requests[0].head.sha = sha('e'); },
    f => { f.api.github.rest.repos.compareCommits = async ({ base }) =>
      ({ data: { behind_by: base === sha('c') ? 1 : 0 } }); },
    f => { f.api.github.rest.repos.getBranch = async () => ({ data: { commit: { sha: sha('b') } } }); },
  ];
  for (const change of changes) {
    const setup = nativeFixture(); change(setup);
    await assert.rejects(check(setup.api, setup.args));
    assert.deepEqual(setup.calls, []);
  }
});

test('native historical base rejects lower prefix, head and branch races on stable reread', async () => {
  for (const change of [
    f => { f.stack.pull_requests[0].head.sha = sha('e'); },
    f => { f.stack.pull_requests[0].number = 5; },
    f => { f.pr.head.sha = sha('e'); },
    f => { f.pr.base.ref = 'other'; },
    f => { f.pr.stack.base.sha = sha('e'); },
  ]) {
    const setup = nativeFixture(); let reads = 0;
    setup.api.github.rest.pulls.get = async () => {
      if (++reads === 2) change(setup);
      return { data: structuredClone(setup.pr) };
    };
    await assert.rejects(check(setup.api, setup.args));
    assert.deepEqual(setup.calls, []);
  }
});
