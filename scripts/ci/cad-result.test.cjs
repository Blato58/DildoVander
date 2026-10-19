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
    repository: { full_name: 'Blato58/DildoVander' },
    pull_requests: [{ number: 123 }], head_sha: 'b'.repeat(40),
    referenced_workflows: [{
      path: `Blato58/DildoVander/.github/workflows/kufr-cad.yml@${'c'.repeat(40)}`,
      ref: 'refs/pull/123/merge', sha: 'c'.repeat(40),
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
    'docs/technical-note.md',
    'docs/examples/check.cjs',
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

test('same PR source-identical merge tree reuses immutable result', async () => {
  const f = prFixture();
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
    'docs/examples/check.cjs', 'docs/technical-note.md', 'kufr/mechanika/final/exports/ignored.stl',
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
  const executableDocumentation = prFixture();
  executableDocumentation.currentTree.find(entry => entry.path === 'docs/technical-note.md').mode = '100755';
  await resolve(executableDocumentation.options, executableDocumentation.settings);
  assert.deepEqual(executableDocumentation.outputs, {});
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
  for (const name of Object.keys(f.outputs)) delete f.outputs[name];
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
    f => { f.run.pull_requests = null; },
    f => { delete f.run.repository; },
    f => { f.run.repository.full_name = 'fork/DildoVander'; },
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

function promotionFixture() {
  const f = prFixture();
  f.options.context.eventName = 'push';
  f.options.context.ref = 'refs/heads/main';
  f.run.repository = { full_name: 'Blato58/DildoVander' };
  f.mergedPr = {
    number: 123, merged: true, merge_commit_sha: 'f'.repeat(40),
    base: { ref: 'main', repo: { full_name: 'Blato58/DildoVander' } },
    head: { repo: { full_name: 'Blato58/DildoVander' } },
  };
  f.options.github.rest.pulls = {
    get: async args => {
      assert.equal(args.pull_number, 123);
      return { data: f.mergedPr };
    },
  };
  f.settings.isAncestor = async sha => {
    assert.equal(sha, f.mergedPr.merge_commit_sha);
    return true;
  };
  return f;
}

test('main promotes content-identical merged PR with server provenance', async () => {
  for (const eventName of ['push', 'workflow_dispatch']) {
    const f = promotionFixture();
    f.options.context.eventName = eventName;
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {
      'artifact-id': '8', 'run-id': '7', exact: 'true', scope: 'merged-pr',
      'source-pr': '123', 'source-merge-sha': 'f'.repeat(40), 'source-built-sha': 'c'.repeat(40),
      'source-basis': 'exact',
    });
  }
});

test('main promotion rejects unmerged, unrelated, malformed and non-ancestor server PRs', async () => {
  for (const change of [
    f => { f.mergedPr.merged = false; },
    f => { delete f.mergedPr.merged; },
    f => { f.mergedPr.number = 456; },
    f => { f.mergedPr.base.ref = 'release'; },
    f => { f.mergedPr.base.repo.full_name = 'fork/DildoVander'; },
    f => { f.mergedPr.head.repo.full_name = 'fork/DildoVander'; },
    f => { f.mergedPr.merge_commit_sha = 'invalid'; },
    f => { delete f.mergedPr.merge_commit_sha; },
    f => { delete f.run.repository; },
    f => { f.run.repository.full_name = 'fork/DildoVander'; },
    f => { f.run.pull_requests = [{ number: 456 }]; },
    f => { f.settings.isAncestor = () => false; },
    f => { f.settings.isAncestor = () => { throw Error('missing history'); }; },
    f => { f.options.github.rest.pulls.get = () => { throw Error('unavailable'); }; },
  ]) {
    const f = promotionFixture();
    change(f);
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {});
  }
});

test('main promotion rejects wrong current context, missing actual tree, key and different code or modes', async () => {
  for (const change of [
    f => { f.options.context.ref = 'refs/heads/feature'; },
    f => { f.options.context.eventName = 'merge_group'; },
    f => { f.options.context.repo.owner = 'fork'; },
    f => { f.artifact.name = `cad-pr-result-v1-${'b'.repeat(64)}`; },
    f => { delete f.run.referenced_workflows; },
    f => { delete f.tree.tree; },
    f => { f.tree.truncated = true; },
    f => { f.currentTree.find(entry => entry.path.endsWith('generate.py')).sha = 'f'.repeat(40); },
    f => { f.currentTree.find(entry => entry.path === 'kufr/backend-tests/Tests.cs').sha = 'f'.repeat(40); },
    f => { f.currentTree.find(entry => entry.path.endsWith('generate.py')).mode = '100755'; },
    f => { f.currentTree.find(entry => entry.path === 'docs/technical-note.md').mode = '100755'; },
  ]) {
    const f = promotionFixture();
    change(f);
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {});
  }
});

test('canonical exact main result takes priority over a newer merged-PR artifact', async () => {
  const f = promotionFixture();
  const main = fixture();
  main.artifact.id = 1;
  main.artifact.workflow_run.id = 2;
  f.options.github.rest.actions.listArtifactsForRepo = async () => ({ data: { artifacts: [f.artifact, main.artifact] } });
  f.options.github.rest.actions.getWorkflowRun = async args => ({ data: args.run_id === 2 ? main.run : f.run });
  f.options.github.rest.pulls.get = () => { throw Error('main hit must not query PR'); };
  await resolve(f.options, f.settings);
  assert.deepEqual(f.outputs, { 'artifact-id': '1', 'run-id': '2', exact: 'true', scope: 'main' });
});

test('force bypasses merged-PR discovery, server queries, tree reads and ancestry', async () => {
  const f = promotionFixture();
  f.options.github.rest.actions.listArtifactsForRepo = () => { throw Error('unexpected lookup'); };
  f.options.github.rest.pulls.get = () => { throw Error('unexpected PR lookup'); };
  f.settings.readTree = () => { throw Error('unexpected git'); };
  f.settings.isAncestor = () => { throw Error('unexpected ancestry'); };
  await resolve(f.options, { ...f.settings, force: true });
  assert.deepEqual(f.outputs, {});
});

test('server workflow merge ref binds same-PR and main results when pull_requests is empty or absent', async () => {
  for (const createFixture of [prFixture, promotionFixture]) {
    for (const missing of [false, true]) {
      const f = createFixture();
      if (missing) delete f.run.pull_requests;
      else f.run.pull_requests = [];
      await resolve(f.options, f.settings);
      assert.equal(f.outputs['artifact-id'], '8');
      assert.equal(f.outputs['source-pr'], '123');
      assert.equal(f.outputs.scope, createFixture === prFixture ? 'same-pr' : 'merged-pr');
    }
  }
});

test('missing, malformed, mismatched or ambiguous canonical workflow refs reject both PR trust paths', async () => {
  for (const createFixture of [prFixture, promotionFixture]) {
    for (const change of [
      f => { delete f.run.referenced_workflows[0].ref; },
      f => { f.run.referenced_workflows[0].ref = 'refs/heads/main'; },
      f => { f.run.referenced_workflows[0].ref = 'refs/pull/123/head'; },
      f => { f.run.referenced_workflows[0].ref = 'refs/pull/0/merge'; },
      f => { f.run.referenced_workflows[0].ref = 'refs/pull/0123/merge'; },
      f => { f.run.referenced_workflows[0].ref = 'refs/pull/9007199254740992/merge'; },
      f => { f.run.referenced_workflows[0].ref = 'refs/pull/456/merge'; },
      f => { f.run.referenced_workflows.push({ ...f.run.referenced_workflows[0] }); },
      f => { f.run.pull_requests = [{ number: 456 }]; },
      f => { delete f.run.repository; },
      f => { f.run.repository.full_name = 'fork/DildoVander'; },
    ]) {
      const f = createFixture();
      change(f);
      await resolve(f.options, f.settings);
      assert.deepEqual(f.outputs, {});
    }
  }
  const f = prFixture();
  f.run.pull_requests = [];
  f.run.referenced_workflows[0].ref = 'refs/pull/456/merge';
  await resolve(f.options, f.settings);
  assert.deepEqual(f.outputs, {}, 'same-PR payload must match reference even without pull_requests');
});

function mainAdvanceFixture() {
  const f = prFixture();
  const baseSha = '1'.repeat(40);
  const mainSha = '2'.repeat(40);
  f.currentTree.find(entry => entry.path === 'kufr/mobilni-aplikace/package.json').sha = 'f'.repeat(40);
  f.baseTree = { sha: '3'.repeat(40), truncated: false, tree: structuredClone(f.tree.tree) };
  f.mainTree = { sha: '4'.repeat(40), truncated: false, tree: structuredClone(f.currentTree) };
  // The PR adds its CAD producer files; original/current main need not have them.
  for (const tree of [f.baseTree, f.mainTree]) {
    tree.tree = tree.tree.filter(entry => !entry.path.startsWith('scripts/ci/'));
  }
  f.builtCommit = { sha: 'c'.repeat(40), tree: { sha: f.tree.sha },
    parents: [{ sha: baseSha }, { sha: f.run.head_sha }] };
  f.mainRef = { ref: 'refs/heads/main', object: { type: 'commit', sha: mainSha } };
  f.options.github.rest.git = {
    getRef: async args => {
      assert.equal(args.ref, 'heads/main');
      return { data: f.mainRef };
    },
    getCommit: async args => {
      if (args.commit_sha === f.builtCommit.sha) return { data: f.builtCommit };
      if (args.commit_sha === baseSha) return { data: { sha: baseSha, tree: { sha: f.baseTree.sha } } };
      if (args.commit_sha === mainSha) return { data: { sha: mainSha, tree: { sha: f.mainTree.sha } } };
      throw Error('unexpected commit');
    },
    getTree: async args => {
      assert.equal(args.recursive, 'true');
      const tree = [f.tree, f.baseTree, f.mainTree].find(value => value.sha === args.tree_sha);
      if (!tree) throw Error('unexpected tree');
      return { data: tree };
    },
  };
  f.settings.isAncestor = async (sha, descendant) =>
    (sha === baseSha && descendant === mainSha) || (sha === mainSha && descendant === undefined);
  return f;
}

test('exact source proof avoids main ref, historical tree and ancestry lookups', async () => {
  const f = prFixture();
  f.options.github.rest.git.getRef = () => { throw Error('unexpected main ref lookup'); };
  f.settings.isAncestor = () => { throw Error('unexpected ancestry lookup'); };
  await resolve(f.options, f.settings);
  assert.equal(f.outputs['artifact-id'], '8');
  assert.equal(f.outputs['source-basis'], 'exact');
  assert.equal(f.outputs['source-built-sha'], 'c'.repeat(40));
  assert.equal(f.outputs['source-base-sha'], undefined);
  assert.equal(f.outputs['source-main-sha'], undefined);
});

test('source fallback accepts only authenticated inherited main changes including additions and deletions', async () => {
  for (const change of [
    () => {},
    f => {
      for (const tree of [f.currentTree, f.mainTree.tree]) {
        tree.splice(tree.findIndex(entry => entry.path === 'kufr/mobilni-aplikace/package.json'), 1);
      }
    },
    f => {
      const added = { path: 'kufr/mobilni-aplikace/new-ui.js', mode: '100644', type: 'blob', sha: 'f'.repeat(40) };
      f.currentTree.push(added); f.mainTree.tree.push({ ...added });
    },
  ]) {
    const f = mainAdvanceFixture();
    change(f);
    await resolve(f.options, f.settings);
    assert.equal(f.outputs['artifact-id'], '8');
    assert.equal(f.outputs.scope, 'same-pr');
    assert.equal(f.outputs['source-basis'], 'inherited-main');
    assert.equal(f.outputs['source-built-sha'], 'c'.repeat(40));
    assert.equal(f.outputs['source-base-sha'], '1'.repeat(40));
    assert.equal(f.outputs['source-main-sha'], '2'.repeat(40));
  }
});

test('merged-PR promotion retains its merge gates when source proof uses inherited main changes', async () => {
  const f = mainAdvanceFixture();
  f.options.context.eventName = 'push';
  f.options.context.ref = 'refs/heads/main';
  const mergeSha = '5'.repeat(40);
  f.options.github.rest.pulls = { get: async () => ({ data: {
    number: 123, merged: true, merge_commit_sha: mergeSha,
    base: { ref: 'main', repo: { full_name: 'Blato58/DildoVander' } },
    head: { repo: { full_name: 'Blato58/DildoVander' } },
  } }) };
  const ancestry = f.settings.isAncestor;
  f.settings.isAncestor = async (sha, descendant) =>
    (sha === mergeSha && descendant === undefined) || await ancestry(sha, descendant);
  await resolve(f.options, f.settings);
  assert.equal(f.outputs.scope, 'merged-pr');
  assert.equal(f.outputs['source-merge-sha'], mergeSha);
  assert.equal(f.outputs['source-basis'], 'inherited-main');
  assert.equal(f.outputs['source-built-sha'], 'c'.repeat(40));
  assert.equal(f.outputs['source-base-sha'], '1'.repeat(40));
  assert.equal(f.outputs['source-main-sha'], '2'.repeat(40));
});

test('source fallback rejects PR-owned or overlapping changes and missing type or mode proof', async () => {
  const path = 'kufr/mobilni-aplikace/package.json';
  for (const change of [
    f => { f.baseTree.tree.find(entry => entry.path === path).sha = 'a'.repeat(40); },
    f => { f.baseTree.tree = f.baseTree.tree.filter(entry => entry.path !== path); },
    f => { f.baseTree.tree.find(entry => entry.path === path).mode = '100755'; },
    f => { f.mainTree.tree.find(entry => entry.path === path).sha = 'a'.repeat(40); },
    f => { f.mainTree.tree = f.mainTree.tree.filter(entry => entry.path !== path); },
    f => { f.mainTree.tree.find(entry => entry.path === path).mode = '100755'; },
    f => { f.currentTree.find(entry => entry.path === path).mode = '120000'; },
    f => { f.currentTree.find(entry => entry.path === 'scripts/ci/cad-result.cjs').sha = 'f'.repeat(40); },
    f => { f.currentTree.find(entry => entry.path === path).sha = 'a'.repeat(40); },
  ]) {
    const f = mainAdvanceFixture();
    change(f);
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {});
  }
});

test('source fallback rejects unrelated or stale main, unauthenticated parents and incomplete APIs', async () => {
  for (const change of [
    f => { f.settings.isAncestor = async (sha, descendant) => descendant === undefined; },
    f => { f.settings.isAncestor = async (sha, descendant) => descendant !== undefined; },
    f => { f.settings.isAncestor = () => { throw Error('missing local history'); }; },
    f => { delete f.builtCommit.parents; },
    f => { f.builtCommit.parents = f.builtCommit.parents.slice(0, 1); },
    f => { f.builtCommit.parents[1].sha = 'a'.repeat(40); },
    f => { f.builtCommit.parents[0].sha = 'invalid'; },
    f => { f.mainRef.ref = 'refs/heads/feature'; },
    f => { f.mainRef.object.type = 'tag'; },
    f => { f.mainRef.object.sha = 'invalid'; },
    f => { f.options.github.rest.git.getRef = () => { throw Error('unavailable'); }; },
    f => { f.options.github.rest.git.getCommit = () => { throw Error('unavailable'); }; },
    f => { f.baseTree.truncated = true; },
    f => { delete f.baseTree.tree; },
    f => { f.mainTree.truncated = true; },
    f => { delete f.mainTree.tree; },
    f => { f.options.github.rest.git.getTree = () => { throw Error('unavailable'); }; },
  ]) {
    const f = mainAdvanceFixture();
    change(f);
    await resolve(f.options, f.settings);
    assert.deepEqual(f.outputs, {});
  }
});
