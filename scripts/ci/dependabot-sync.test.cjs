const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const { createHash } = require('node:crypto');
const { plan, publish, producerPlan } = require('./dependabot-sync.cjs');

const sha = letter => letter.repeat(40);
const BACKEND = 'backend/packages.lock.json';
const TESTS = 'backend-tests/packages.lock.json';
const PROJECT = 'backend/backend.csproj';
const MOBILE = 'mobilni-aplikace';
const SWIFT = `${MOBILE}/ios/App/CapApp-SPM/Package.swift`;
const RESOLVED = `${MOBILE}/ios/App/App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved`;
const encode = value => `${JSON.stringify(value, null, 2)}\n`;
const hash = value => createHash('sha1').update(value).digest('hex');
const project = version => `<Project><ItemGroup><PackageReference Include="Example" Version="${version}" /></ItemGroup></Project>\n`;
const manifest = (version, vite = '^8.0.0') => encode({ name: 'mobile', scripts: { test: 'node --test' },
  dependencies: { '@capacitor/core': version, '@capacitor/ios': version },
  devDependencies: { '@capacitor/cli': version, vite } });
const swift = version => `import PackageDescription\nlet package = Package(dependencies: [\n.package(url: "https://github.com/ionic-team/capacitor-swift-pm.git", exact: "${version}")\n])\n`;
const resolved = version => encode({ version: 3, pins: [{ identity: 'capacitor-swift-pm', kind: 'remoteSourceControl',
  location: 'https://github.com/ionic-team/capacitor-swift-pm.git', state: { revision: version === '1.0.0' ? sha('a') : sha('b'), version } },
{ identity: 'other', kind: 'remoteSourceControl', location: 'https://example.com/other.git',
  state: { revision: sha('d'), version: '1.0.0' } }] });
function lock(version, testProject = false) {
  const dependencies = { Example: { type: testProject ? 'Transitive' : 'Direct', resolved: version,
    contentHash: `${'a'.repeat(86)}==`, ...(testProject ? {} : { requested: `[${version}, )` }) } };
  if (testProject) dependencies.backend = { type: 'Project', dependencies: { Example: `[${version}, )` } };
  return encode({ version: 1, dependencies: { 'net10.0': dependencies } });
}

function fixture({ backend = true, native = false } = {}) {
  const baseFiles = new Map([[PROJECT, project('1.0.0')], [BACKEND, lock('1.0.0')], [TESTS, lock('1.0.0', true)],
    [`${MOBILE}/package.json`, manifest('1.0.0')], [`${MOBILE}/package-lock.json`, '{}\n'],
    [SWIFT, swift('1.0.0')], [RESOLVED, resolved('1.0.0')]]);
  const headFiles = new Map(baseFiles);
  if (backend) { headFiles.set(PROJECT, project('2.0.0')); headFiles.set(BACKEND, lock('2.0.0')); }
  if (native) headFiles.set(`${MOBILE}/package.json`, manifest('2.0.0'));
  const pr = { number: 7, state: 'open', draft: false, user: { login: 'dependabot[bot]' },
    head: { sha: sha('a'), ref: 'dependabot/group', repo: { full_name: 'owner/repo' } },
    base: { sha: sha('b'), ref: 'main', repo: { full_name: 'owner/repo' } } };
  const files = new Map([[sha('b'), baseFiles], [sha('a'), headFiles]]);
  const messages = new Map();
  const parents = new Map();
  const blobs = new Map();
  const modes = new Map();
  const calls = [];
  let base = sha('b');
  const changedPaths = () => [...new Set([...baseFiles.keys(), ...files.get(pr.head.sha).keys()])]
    .filter(filename => baseFiles.get(filename) !== files.get(pr.head.sha).get(filename));
  Object.defineProperty(pr, 'changed_files', { get: () => changedPaths().length });
  const api = {
    context: { repo: { owner: 'owner', repo: 'repo' }, payload: { pull_request: pr }, eventName: 'pull_request' },
    core: { info(message) { calls.push(['info', message]); } },
    github: { rest: {
      pulls: {
        get: async () => ({ data: pr }), listFiles() {}, listCommits() {},
      },
      repos: {
        getBranch: async () => ({ data: { commit: { sha: base } } }),
        compareCommits: async () => ({ data: { merge_base_commit: { sha: sha('b') }, behind_by: 0 } }),
        getContent: async args => {
          const content = files.get(args.ref)?.get(args.path);
          assert.notEqual(content, undefined, `Unknown file ${args.ref}:${args.path}`);
          return { data: { type: 'file', encoding: 'base64', size: Buffer.byteLength(content),
            content: Buffer.from(content).toString('base64') } };
        },
      },
      git: {
        getCommit: async args => ({ data: { tree: { sha: args.commit_sha }, message: messages.get(args.commit_sha) || 'dependency bump',
          parents: parents.get(args.commit_sha) || [{ sha: sha('b') }] } }),
        getTree: async args => ({ data: { truncated: false,
          tree: [...files.get(args.tree_sha)].map(([filename, content]) => ({ path: filename,
            mode: modes.get(filename) || '100644', type: 'blob', sha: hash(content) })) } }),
        createBlob: async args => {
          calls.push(['blob', args]); blobs.set(hash(args.content), Buffer.from(args.content, 'base64').toString('utf8'));
          return { data: { sha: hash(args.content) } };
        },
        createTree: async args => {
          calls.push(['tree', args]); const generated = new Map(files.get(args.base_tree));
          for (const entry of args.tree) generated.set(entry.path, blobs.get(entry.sha));
          files.set(sha('d'), generated); return { data: { sha: sha('d') } };
        },
        createCommit: async args => {
          calls.push(['commit', args]); messages.set(sha('c'), args.message);
          parents.set(sha('c'), args.parents.map(value => ({ sha: value }))); files.set(sha('c'), files.get(args.tree));
          return { data: { sha: sha('c') } };
        },
        updateRef: async args => { calls.push(['ref', args]); pr.head.sha = args.sha; },
      },
      actions: { createWorkflowDispatch: async args => { calls.push(['dispatch', args]); } },
    }, paginate: async method => method === api.github.rest.pulls.listCommits ? [{ sha: sha('a') }]
      : changedPaths().map(filename => ({ filename, status: 'modified' })) },
  };
  return { api, pr, files, baseFiles, headFiles, calls, modes, messages, parents, setBase(value) { base = value; },
    writes() { return calls.filter(([kind]) => !['info'].includes(kind)); } };
}

async function artifact(t, current, change) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'dependabot-sync-'));
  t.after(() => fs.rm(directory, { recursive: true, force: true }));
  for (const kind of ['backend', 'native']) {
    if (!current[kind]) continue;
    const payload = { version: 1, number: current.number, head: current.head, base: current.base, kind,
      files: kind === 'backend' ? [{ path: BACKEND, content: lock('2.0.0') }, { path: TESTS, content: lock('2.0.0', true) }]
        : [{ path: SWIFT, content: swift('2.0.0') }, { path: RESOLVED, content: resolved('2.0.0') }] };
    if (change) change(payload, kind);
    await fs.mkdir(path.join(directory, kind));
    await fs.writeFile(path.join(directory, kind, 'dependencies.json'), encode(payload));
  }
  return directory;
}

test('plans backend, native and mixed dependency groups', async () => {
  for (const [backend, native] of [[true, false], [false, true], [true, true]]) {
    const f = fixture({ backend, native });
    assert.deepEqual(await plan(f.api), { number: 7, head: sha('a'), base: sha('b'), branch: 'dependabot/group', backend, native, dryRun: false });
    assert.deepEqual(f.writes(), []);
  }
});

test('Vite-only dependency changes do not request native runner', async () => {
  const f = fixture({ backend: false });
  f.headFiles.set(`${MOBILE}/package.json`, manifest('1.0.0', '^9.0.0'));
  assert.equal(await plan(f.api), null);
  f.headFiles.set(PROJECT, project('2.0.0'));
  assert.equal((await plan(f.api)).native, false);
});

test('rejects actor, repository, branch, lifecycle and arbitrary source changes', async () => {
  const changes = [f => { f.pr.user.login = 'human'; }, f => { f.pr.head.repo.full_name = 'fork/repo'; },
    f => { f.pr.base.repo.full_name = 'fork/repo'; }, f => { f.pr.head.ref = 'feature'; },
    f => { f.pr.base.ref = 'release'; }, f => { f.pr.state = 'closed'; }, f => { f.pr.draft = true; },
    f => { f.headFiles.set('.github/workflows/ci.yml', 'run: bad'); },
    f => { f.headFiles.delete(PROJECT); }, f => { f.modes.set(PROJECT, '120000'); },
    f => { f.headFiles.set(PROJECT, project('2.0.0').replace('</Project>', '<Target Name="Run" /></Project>')); },
    f => { const value = JSON.parse(manifest('2.0.0')); value.scripts.test = 'bad'; f.headFiles.set(`${MOBILE}/package.json`, encode(value)); },
    f => { const value = JSON.parse(manifest('1.0.0')); value.devDependencies.vite = 'file:../../evil'; f.headFiles.set(`${MOBILE}/package.json`, encode(value)); },
    f => { const value = JSON.parse(manifest('2.0.0')); value.dependencies['@capacitor/ios'] = '^2.0.0'; f.headFiles.set(`${MOBILE}/package.json`, encode(value)); },
    f => { f.api.github.paginate = async () => [{ filename: PROJECT, status: 'renamed', previous_filename: 'other' }]; },
    f => { f.api.github.rest.git.getTree = async () => ({ data: { truncated: true, tree: [] } }); },
    f => { f.api.github.rest.repos.compareCommits = async () => ({ data: { merge_base_commit: { sha: sha('b') }, behind_by: 1 } }); }];
  for (const change of changes) {
    const f = fixture(); change(f);
    assert.equal(await plan(f.api), null, String(change));
    assert.deepEqual(f.writes(), []);
  }
});

test('compares semantic changes to merge base instead of unrelated current main content', async () => {
  const f = fixture();
  f.setBase(sha('e')); f.files.set(sha('e'), new Map([...f.baseFiles, ['unrelated', 'main update']]));
  f.api.github.rest.repos.compareCommits = async () => ({ data: { merge_base_commit: { sha: sha('b') }, behind_by: 0 } });
  assert.equal((await plan(f.api)).base, sha('e'));
});

test('historical manual dry run accepts only a known PR commit and remains strictly read only', async t => {
  const f = fixture({ backend: true, native: true }); f.pr.state = 'closed'; f.api.context.eventName = 'workflow_dispatch';
  const current = await plan(f.api, { number: 7, dryRun: true, head: sha('a') });
  assert.ok(current);
  const directory = await artifact(t, current);
  assert.deepEqual(await publish(f.api, current, directory), { changed: [TESTS, SWIFT, RESOLVED], dryRun: true });
  assert.deepEqual(f.writes(), []);
  assert.equal(await plan(f.api, { number: 7, dryRun: true, head: sha('f') }), null);
  assert.equal(await plan(f.api, { number: 7, dryRun: false, head: sha('a') }), null);
  f.api.context.eventName = 'pull_request';
  assert.equal(await plan(f.api, { number: 7, dryRun: true, head: sha('a') }), null);
});

test('publisher commits fixed generated paths with pinned parent and dispatches exact CI identity', async t => {
  const f = fixture({ backend: true, native: true });
  const current = await plan(f.api);
  const directory = await artifact(t, current);
  assert.deepEqual(await publish(f.api, current, directory), { changed: [TESTS, SWIFT, RESOLVED], head: sha('c') });
  const writes = f.writes();
  assert.equal(writes.filter(([kind]) => kind === 'blob').length, 3);
  assert.deepEqual(writes.find(([kind]) => kind === 'tree')[1].tree.map(entry => entry.path), [TESTS, SWIFT, RESOLVED]);
  assert.deepEqual(writes.find(([kind]) => kind === 'commit')[1].parents, [sha('a')]);
  assert.deepEqual(writes.find(([kind]) => kind === 'ref')[1], { owner: 'owner', repo: 'repo', ref: 'heads/dependabot/group', sha: sha('c'), force: false });
  assert.deepEqual(writes.find(([kind]) => kind === 'dispatch')[1].inputs,
    { project: 'all', pr_number: '7', expected_head_sha: sha('c'), expected_base_sha: sha('b') });
});

test('rejects forged, oversized and unexpected payloads before creating any Git objects', async t => {
  const changes = [payload => { payload.number = 99; }, payload => { payload.head = sha('f'); },
    payload => { payload.base = sha('f'); }, payload => { payload.kind = 'other'; },
    payload => { payload.extra = 'surprise'; }, payload => { payload.files[0].extra = true; },
    payload => { payload.files[0].path = '../evil'; }, payload => { payload.files[0].path = TESTS; },
    payload => { payload.files[0].content = 'x'.repeat(2 * 1024 * 1024 + 1); },
    payload => { payload.files[0].content = '{}'; }, payload => { payload.files[0].content = lock('1.0.0'); },
    payload => { payload.files[0].content = payload.files[0].content.replace(/\n/g, '\r\n'); }];
  for (const change of changes) {
    const f = fixture(); const current = await plan(f.api); const directory = await artifact(t, current, change);
    await assert.rejects(publish(f.api, current, directory));
    assert.deepEqual(f.writes(), []);
  }
});

test('native payload rejects changed code, wrong Capacitor pin, or unrelated Swift resolution changes', async t => {
  const changes = [payload => { payload.files[0].content += 'import Evil\n'; },
    payload => { payload.files[0].content = swift('3.0.0'); },
    payload => { payload.files[1].content = resolved('3.0.0'); },
    payload => { const value = JSON.parse(payload.files[1].content); value.pins[1].state.revision = sha('f'); payload.files[1].content = encode(value); }];
  for (const change of changes) {
    const f = fixture({ backend: false, native: true }); const current = await plan(f.api);
    await assert.rejects(publish(f.api, current, await artifact(t, current, change)));
    assert.deepEqual(f.writes(), []);
  }
});

test('NuGet payload preserves framework coverage and shared transitive identity', async t => {
  for (const change of [value => { value.dependencies['net10.0/linux-x64'] = {}; },
    value => { value.dependencies['net10.0'].Example.contentHash = `${'b'.repeat(86)}==`; }]) {
    const f = fixture(); const current = await plan(f.api);
    const directory = await artifact(t, current, payload => {
      const value = JSON.parse(payload.files[1].content); change(value); payload.files[1].content = encode(value);
    });
    await assert.rejects(publish(f.api, current, directory)); assert.deepEqual(f.writes(), []);
  }
});

test('native resolver may update originHash while retaining unrelated package pins', async t => {
  const f = fixture({ backend: false, native: true }); const current = await plan(f.api);
  const directory = await artifact(t, current, payload => {
    const value = JSON.parse(payload.files[1].content); value.originHash = 'a'.repeat(64); payload.files[1].content = encode(value);
  });
  assert.equal((await publish(f.api, current, directory)).head, sha('c'));
});

test('closed, draft, changed head and changed base all stop publication before writes', async t => {
  for (const change of [f => { f.pr.state = 'closed'; }, f => { f.pr.draft = true; },
    f => { f.pr.head.sha = sha('f'); }, f => { f.setBase(sha('f')); }]) {
    const f = fixture(); const current = await plan(f.api); const directory = await artifact(t, current); change(f);
    await assert.rejects(publish(f.api, current, directory)); assert.deepEqual(f.writes(), []);
  }
});

test('final eligibility recheck detects a race after object creation and does not update branch', async t => {
  const f = fixture(); const current = await plan(f.api); const directory = await artifact(t, current);
  const createCommit = f.api.github.rest.git.createCommit;
  f.api.github.rest.git.createCommit = async args => { const result = await createCommit(args); f.pr.state = 'closed'; return result; };
  await assert.rejects(publish(f.api, current, directory), /changed/);
  assert.equal(f.writes().filter(([kind]) => kind === 'ref' || kind === 'dispatch').length, 0);
});

test('nonforce update rejects a concurrent commit after the final recheck without dispatch', async t => {
  const f = fixture(); const current = await plan(f.api); const directory = await artifact(t, current);
  f.api.github.rest.git.updateRef = async args => {
    assert.equal(args.force, false); throw new Error('422 update is not a fast forward');
  };
  await assert.rejects(publish(f.api, current, directory), /fast forward/);
  assert.equal(f.writes().filter(([kind]) => kind === 'dispatch').length, 0);
});

test('no-op never commits or dispatches normal dependency heads', async t => {
  const f = fixture(); f.headFiles.set(TESTS, lock('2.0.0', true));
  const current = await plan(f.api); const directory = await artifact(t, current);
  assert.deepEqual(await publish(f.api, current, directory), { changed: [] });
  assert.deepEqual(f.writes(), []);
});

test('no-op retries dispatch after a prior generated commit succeeded', async t => {
  const f = fixture(); f.headFiles.set(TESTS, lock('2.0.0', true));
  f.messages.set(sha('a'), `chore(deps): synchronize generated dependency artifacts\n\nPR #7; base ${sha('b')}`);
  const current = await plan(f.api); const directory = await artifact(t, current);
  assert.deepEqual(await publish(f.api, current, directory), { changed: [] });
  assert.deepEqual(f.writes().map(([kind]) => kind), ['dispatch']);
});

test('a failed dispatch propagates so workflow failure is visible', async t => {
  const f = fixture(); const current = await plan(f.api); const directory = await artifact(t, current);
  f.api.github.rest.actions.createWorkflowDispatch = async () => { throw new Error('Dispatch unavailable'); };
  await assert.rejects(publish(f.api, current, directory), /Dispatch unavailable/);
  assert.equal(f.writes().filter(([kind]) => kind === 'ref').length, 1);
});

function producer(f) {
  f.api.context.eventName = 'workflow_run';
  f.api.context.payload.workflow_run = { status: 'completed', conclusion: 'success', event: 'pull_request',
    path: '.github/workflows/dependabot-generate.yml', repository: { full_name: 'owner/repo' },
    head_repository: { full_name: 'owner/repo' }, pull_requests: [{ number: 7 }],
    head_sha: sha('a'), head_branch: 'dependabot/group' };
  return f.api.context.payload.workflow_run;
}

test('producerPlan binds successful same-repository producer to the exact current PR head', async () => {
  const f = fixture(); producer(f);
  assert.deepEqual(await producerPlan(f.api), await plan(f.api)); assert.deepEqual(f.writes(), []);
});

test('producerPlan refuses other workflow identities, forks, manual runs and unsafe newer commits', async () => {
  for (const change of [(run) => { run.path = '.github/workflows/evil.yml'; }, (run) => { run.event = 'workflow_dispatch'; },
    (run) => { run.conclusion = 'failure'; }, (run) => { run.status = 'in_progress'; },
    (run) => { run.repository.full_name = 'fork/repo'; }, (run) => { run.head_repository.full_name = 'fork/repo'; },
    (run) => { run.pull_requests = []; }, (run) => { run.pull_requests.push({ number: 8 }); },
    (run) => { run.head_branch = 'other'; }, (run) => { run.head_sha = sha('f'); }]) {
    const f = fixture(); const run = producer(f); change(run);
    assert.equal(await producerPlan(f.api), null, String(change)); assert.deepEqual(f.writes(), []);
  }
});

test('producerPlan recovers a failed dispatch after branch update without republishing stale artifacts', async t => {
  const f = fixture(); const current = await plan(f.api); const directory = await artifact(t, current);
  const createWorkflowDispatch = f.api.github.rest.actions.createWorkflowDispatch;
  f.api.github.rest.actions.createWorkflowDispatch = async () => { throw new Error('Dispatch failed'); };
  await assert.rejects(publish(f.api, current, directory), /Dispatch failed/);
  assert.equal(f.pr.head.sha, sha('c'));
  f.api.github.rest.actions.createWorkflowDispatch = createWorkflowDispatch;
  producer(f); f.calls.length = 0;
  assert.equal(await producerPlan(f.api), null);
  assert.deepEqual(f.writes().map(([kind]) => kind), ['dispatch']);
  assert.equal(f.writes()[0][1].inputs.expected_head_sha, sha('c'));
});

test('producer recovery requires exact marker and sole parent matching the producer', async () => {
  for (const parents of [[], [{ sha: sha('b') }], [{ sha: sha('a') }, { sha: sha('b') }]]) {
    const f = fixture(); producer(f); f.api.context.payload.workflow_run.head_sha = sha('f');
    f.messages.set(sha('a'), `chore(deps): synchronize generated dependency artifacts\n\nPR #7; base ${sha('b')}`);
    f.parents.set(sha('a'), parents);
    assert.equal(await producerPlan(f.api), null); assert.deepEqual(f.writes(), []);
  }
});
