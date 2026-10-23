// Trusted, API-only half of dependency regeneration. Never executes PR content.
const fs = require('node:fs/promises');
const path = require('node:path');
const { isDeepStrictEqual } = require('node:util');

const BACKEND = ['backend/packages.lock.json', 'backend-tests/packages.lock.json'];
const PROJECTS = ['backend/backend.csproj', 'backend-tests/BackendTests.csproj'];
const MOBILE = 'mobilni-aplikace';
const SWIFT = `${MOBILE}/ios/App/CapApp-SPM/Package.swift`;
const RESOLVED = `${MOBILE}/ios/App/App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved`;
const NATIVE = [SWIFT, RESOLVED];
const NPM_ROOTS = [MOBILE, 'contracts', 'firmware/simulator'];
const ALLOWED = new Set([...BACKEND, ...PROJECTS, ...NATIVE,
  ...NPM_ROOTS.flatMap(root => [`${root}/package.json`, `${root}/package-lock.json`])]);
const SHA = /^[a-f0-9]{40}$/;
const SEMVER = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
const NPM_VERSION = /^[~^]?\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;
const MAX_BYTES = 2 * 1024 * 1024;
const MARKER = 'chore(deps): synchronize generated dependency artifacts';

class Invalid extends Error {}
function requireValid(condition, message) {
  if (!condition) throw new Invalid(message);
}
function record(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }
function keys(value, allowed, required = allowed) {
  requireValid(record(value) && Object.keys(value).every(key => allowed.includes(key))
    && required.every(key => Object.hasOwn(value, key)), 'Unexpected object fields');
}
function json(content) {
  try { return JSON.parse(content); } catch { throw new Invalid('Invalid JSON'); }
}
function lf(value) { return value.replace(/\r\n/g, '\n'); }

async function read(api, ref, filename) {
  const { data } = await api.github.rest.repos.getContent({ ...api.context.repo, ref, path: filename });
  requireValid(data.type === 'file' && data.encoding === 'base64' && data.size <= MAX_BYTES,
    `Expected bounded regular file: ${filename}`);
  const bytes = Buffer.from(data.content, 'base64');
  requireValid(bytes.length <= MAX_BYTES && bytes.toString('utf8').indexOf('\ufffd') < 0,
    `Invalid UTF-8: ${filename}`);
  return bytes.toString('utf8');
}

async function tree(api, ref) {
  const { data: commit } = await api.github.rest.git.getCommit({ ...api.context.repo, commit_sha: ref });
  const { data } = await api.github.rest.git.getTree({
    ...api.context.repo, tree_sha: commit.tree.sha, recursive: 'true',
  });
  requireValid(!data.truncated && Array.isArray(data.tree), 'Incomplete repository tree');
  return new Map(data.tree.filter(entry => entry.type !== 'tree').map(entry => [entry.path, entry]));
}

function projectShape(content) {
  let count = 0;
  const shape = lf(content).replace(/(<PackageReference\b[^>]*\bVersion\s*=\s*")([^"]+)(")/g,
    (_, prefix, version, suffix) => {
      requireValid(SEMVER.test(version), 'Unsupported NuGet version');
      count++;
      return `${prefix}VERSION${suffix}`;
    });
  requireValid(count > 0, 'No supported PackageReference versions');
  return shape;
}

function manifestShape(content) {
  const value = json(content);
  requireValid(record(value), 'Expected npm manifest');
  for (const group of ['dependencies', 'devDependencies', 'optionalDependencies']) {
    if (!Object.hasOwn(value, group)) continue;
    requireValid(record(value[group]), 'Expected npm dependency map');
    for (const name of Object.keys(value[group])) {
      requireValid(typeof value[group][name] === 'string' && NPM_VERSION.test(value[group][name]),
        'Expected ordinary npm semver version or range');
      value[group][name] = 'VERSION';
    }
  }
  return value;
}

function capacitorVersion(content) {
  const value = json(content);
  const versions = ['@capacitor/core', '@capacitor/ios', '@capacitor/cli'].map(name => {
    const matches = ['dependencies', 'devDependencies', 'optionalDependencies']
      .filter(group => Object.hasOwn(value[group] || {}, name));
    requireValid(matches.length === 1, 'Capacitor trio must occur exactly once');
    return value[matches[0]][name];
  });
  requireValid(versions.every(version => typeof version === 'string' && SEMVER.test(version)
    && version === versions[0]), 'Capacitor trio must use unified exact versions');
  return versions[0];
}

function swiftShape(content, version) {
  let count = 0;
  const shape = lf(content).replace(/(\.package\(url: "https:\/\/github\.com\/ionic-team\/capacitor-swift-pm\.git", exact: ")([^"]+)("\))/g,
    (_, prefix, actual, suffix) => {
      requireValid(SEMVER.test(actual) && (!version || actual === version), 'Unexpected native Capacitor version');
      count++;
      return `${prefix}VERSION${suffix}`;
    });
  requireValid(count === 1, 'Expected exactly one Capacitor Swift package');
  return shape;
}

function resolvedShape(content, version) {
  const value = json(content);
  keys(value, ['pins', 'version', 'originHash'], ['pins', 'version']);
  requireValid([2, 3].includes(value.version) && Array.isArray(value.pins) && value.pins.length < 100,
    'Unsupported Swift lock format');
  if (Object.hasOwn(value, 'originHash')) {
    requireValid(typeof value.originHash === 'string' && /^[a-f0-9]{64}$/.test(value.originHash),
      'Invalid Swift origin hash');
    delete value.originHash;
  }
  const identities = new Set();
  let count = 0;
  for (const pin of value.pins) {
    keys(pin, ['identity', 'kind', 'location', 'state']);
    requireValid(typeof pin.identity === 'string' && !identities.has(pin.identity), 'Duplicate Swift pin');
    identities.add(pin.identity);
    if (pin.identity !== 'capacitor-swift-pm') continue;
    requireValid(pin.kind === 'remoteSourceControl'
      && pin.location === 'https://github.com/ionic-team/capacitor-swift-pm.git', 'Unexpected Capacitor repository');
    keys(pin.state, ['revision', 'version']);
    requireValid(SHA.test(pin.state.revision) && SEMVER.test(pin.state.version)
      && (!version || pin.state.version === version), 'Invalid Capacitor Swift pin');
    pin.state = { revision: 'REVISION', version: 'VERSION' };
    count++;
  }
  requireValid(count === 1, 'Expected exactly one resolved Capacitor pin');
  return value;
}

function nugetLock(content) {
  const value = json(content);
  keys(value, ['version', 'dependencies']);
  requireValid(value.version === 1 && record(value.dependencies)
    && Object.keys(value.dependencies).length > 0, 'Unsupported NuGet lock format');
  for (const dependencies of Object.values(value.dependencies)) {
    requireValid(record(dependencies), 'Invalid NuGet framework dependencies');
    for (const dependency of Object.values(dependencies)) {
      keys(dependency, ['type', 'requested', 'resolved', 'contentHash', 'dependencies'], ['type']);
      requireValid(['Direct', 'Transitive', 'Project'].includes(dependency.type), 'Invalid NuGet dependency type');
      if (dependency.type !== 'Project') {
        requireValid(typeof dependency.resolved === 'string' && SEMVER.test(dependency.resolved)
          && typeof dependency.contentHash === 'string'
          && /^[A-Za-z0-9+/]{86}==$/.test(dependency.contentHash), 'Invalid NuGet package identity');
      }
      if (dependency.requested !== undefined) requireValid(typeof dependency.requested === 'string', 'Invalid NuGet request');
      if (dependency.dependencies !== undefined) requireValid(record(dependency.dependencies)
        && Object.values(dependency.dependencies).every(version => typeof version === 'string'), 'Invalid NuGet edge');
    }
  }
  return value;
}

async function validateBackend(api, current, files) {
  const locks = BACKEND.map(filename => nugetLock(files.find(file => file.path === filename).content));
  for (const [index, filename] of BACKEND.entries()) {
    const previous = nugetLock(await read(api, current.head, filename));
    requireValid(isDeepStrictEqual(Object.keys(previous.dependencies).sort(), Object.keys(locks[index].dependencies).sort()),
      'Generated NuGet lock changed framework or runtime coverage');
  }
  for (const [framework, dependencies] of Object.entries(locks[0].dependencies)) {
    const testDependencies = locks[1].dependencies[framework];
    requireValid(record(testDependencies), 'Test lock is missing a backend framework');
    for (const [name, dependency] of Object.entries(dependencies)) {
      const testDependency = testDependencies[name];
      if (!framework.includes('/')) requireValid(testDependency, 'Test lock omitted a backend package');
      if (testDependency && dependency.type !== 'Project') {
        requireValid(dependency.resolved === testDependency.resolved && dependency.contentHash === testDependency.contentHash,
          'Backend and test locks disagree on shared package identity');
      }
    }
  }
  const project = await read(api, current.head, PROJECTS[0]);
  const references = [...project.matchAll(/<PackageReference\s+Include="([^"]+)"\s+Version="([^"]+)"\s*\/>/g)]
    .map(match => [match[1], match[2]]);
  requireValid(references.length > 0, 'Expected backend package references');
  const primary = Object.entries(locks[0].dependencies).filter(([framework]) => !framework.includes('/'));
  requireValid(primary.length > 0, 'Expected primary NuGet framework');
  for (const [framework, dependencies] of primary) {
    const testDependencies = locks[1].dependencies[framework];
    requireValid(record(testDependencies), 'Test lock is missing the backend framework');
    for (const [name, version] of references) {
      requireValid(dependencies[name]?.type === 'Direct' && dependencies[name].resolved === version
        && testDependencies[name]?.resolved === version
        && testDependencies[name].contentHash === dependencies[name].contentHash
        && testDependencies.backend?.type === 'Project'
        && testDependencies.backend.dependencies?.[name] === `[${version}, )`,
      'Backend and test NuGet locks do not agree with project references');
    }
  }
}

async function plan(api, { number = api.context.payload.pull_request?.number, dryRun = false, head } = {}) {
  const { github, context, core } = api;
  try {
    requireValid(Number.isSafeInteger(Number(number)) && Number(number) > 0 && typeof dryRun === 'boolean',
      'Expected PR number and dryRun boolean');
    requireValid(head === undefined || (dryRun && context.eventName === 'workflow_dispatch' && SHA.test(head)),
      'Revision override is only supported for manual dry runs');
    const { data: pr } = await github.rest.pulls.get({ ...context.repo, pull_number: Number(number) });
    const fullName = `${context.repo.owner}/${context.repo.repo}`;
    requireValid(pr.user?.login === 'dependabot[bot]' && pr.head.repo?.full_name === fullName
      && pr.base.repo?.full_name === fullName && pr.head.ref.startsWith('dependabot/')
      && pr.base.ref === 'main' && !pr.draft && (pr.state === 'open' || dryRun),
    'Only same-repository Dependabot dependency PRs are supported');
    if (head !== undefined) {
      const commits = await github.paginate(github.rest.pulls.listCommits, {
        ...context.repo, pull_number: Number(number), per_page: 100,
      });
      requireValid(commits.length < 250 && commits.some(commit => commit.sha === head),
        'Revision is not a complete, known PR commit');
    }
    const revision = head || pr.head.sha;
    const { data: branch } = await github.rest.repos.getBranch({ ...context.repo, branch: 'main' });
    const base = branch.commit.sha;
    requireValid(SHA.test(revision) && SHA.test(base), 'Expected immutable revisions');
    const { data: comparison } = await github.rest.repos.compareCommits({ ...context.repo, base, head: revision });
    requireValid(SHA.test(comparison.merge_base_commit?.sha), 'Missing merge base');
    requireValid(dryRun || comparison.behind_by === 0, 'PR must be synchronized with main first');
    const [before, after] = await Promise.all([
      tree(api, comparison.merge_base_commit.sha), tree(api, revision),
    ]);
    const changed = [...new Set([...before.keys(), ...after.keys()])]
      .filter(filename => before.get(filename)?.sha !== after.get(filename)?.sha
        || before.get(filename)?.mode !== after.get(filename)?.mode);
    requireValid(changed.length > 0 && changed.every(filename => ALLOWED.has(filename)
      && before.get(filename)?.mode === '100644' && after.get(filename)?.mode === '100644'
      && before.get(filename)?.type === 'blob' && after.get(filename)?.type === 'blob'),
    'PR contains source changes, additions, deletions or nonregular dependency files');
    if (!head) {
      const files = await github.paginate(github.rest.pulls.listFiles, {
        ...context.repo, pull_number: Number(number), per_page: 100,
      });
      requireValid(files.length === pr.changed_files && files.length === changed.length
        && files.every(file => file.status === 'modified' && !file.previous_filename && changed.includes(file.filename)),
      'Incomplete or unsupported PR file diff');
    }
    for (const filename of changed) {
      if (filename.endsWith('.csproj') || filename.endsWith('/package.json') || NATIVE.includes(filename)) {
        const [oldContent, newContent] = await Promise.all([
          read(api, comparison.merge_base_commit.sha, filename), read(api, revision, filename),
        ]);
        const shape = filename.endsWith('.csproj') ? projectShape
          : filename.endsWith('/package.json') ? manifestShape : filename === SWIFT ? swiftShape : resolvedShape;
        requireValid(isDeepStrictEqual(shape(oldContent), shape(newContent)), `Non-version dependency change: ${filename}`);
      }
    }
    const backend = changed.some(filename => [...BACKEND, ...PROJECTS].includes(filename));
    let native = false;
    if (changed.some(filename => filename.startsWith(`${MOBILE}/`))) {
      const [oldManifest, newManifest] = await Promise.all([
        read(api, comparison.merge_base_commit.sha, `${MOBILE}/package.json`), read(api, revision, `${MOBILE}/package.json`),
      ]);
      native = capacitorVersion(oldManifest) !== capacitorVersion(newManifest);
      requireValid(native || !changed.some(filename => NATIVE.includes(filename)), 'Native pins changed without Capacitor bump');
    }
    if (!backend && !native) { core.info(`PR #${number}: no generated dependency artifacts requested`); return null; }
    return { number: Number(number), head: revision, base, branch: pr.head.ref, backend, native, dryRun };
  } catch (error) {
    if (!(error instanceof Invalid)) throw error;
    core.info(`PR #${number}: skipped: ${error.message}`);
    return null;
  }
}

async function payloadFiles(directory, current) {
  const result = [];
  for (const [kind, allowed] of [['backend', BACKEND], ['native', NATIVE]]) {
    if (!current[kind]) continue;
    const kindDirectory = path.join(directory, kind);
    const directoryStat = await fs.lstat(kindDirectory);
    requireValid(directoryStat.isDirectory() && !directoryStat.isSymbolicLink(), 'Invalid artifact directory');
    const filename = path.join(kindDirectory, 'dependencies.json');
    const stat = await fs.lstat(filename);
    requireValid(stat.isFile() && !stat.isSymbolicLink() && stat.size <= MAX_BYTES * 3, 'Invalid dependency artifact');
    const payload = json(await fs.readFile(filename, 'utf8'));
    keys(payload, ['version', 'number', 'head', 'base', 'kind', 'files']);
    requireValid(payload.version === 1 && payload.number === current.number && payload.head === current.head
      && payload.base === current.base && payload.kind === kind && Array.isArray(payload.files)
      && payload.files.length === allowed.length, 'Artifact identity mismatch');
    const seen = new Set();
    for (const file of payload.files) {
      keys(file, ['path', 'content']);
      requireValid(allowed.includes(file.path) && !seen.has(file.path) && typeof file.content === 'string'
        && Buffer.byteLength(file.content) <= MAX_BYTES && !file.content.includes('\r')
        && !file.content.includes('\0') && !file.content.includes('\ufffd'), 'Invalid artifact path or content');
      seen.add(file.path);
      result.push(file);
    }
  }
  return result;
}

async function dispatch(api, current, head) {
  await api.github.rest.actions.createWorkflowDispatch({ ...api.context.repo, workflow_id: 'ci.yml', ref: current.branch,
    inputs: { project: 'all', pr_number: String(current.number), expected_head_sha: head,
      expected_base_sha: current.base } });
}

async function verifyCurrent(api, expected) {
  const current = await plan(api, { number: expected.number, dryRun: expected.dryRun,
    ...(expected.dryRun && api.context.eventName === 'workflow_dispatch' ? { head: expected.head } : {}) });
  requireValid(current && isDeepStrictEqual(current, expected), 'PR eligibility, head or base changed; regenerate');
  return current;
}

async function producerPlan(api) {
  try {
    const run = api.context.payload.workflow_run;
    const fullName = `${api.context.repo.owner}/${api.context.repo.repo}`;
    requireValid(api.context.eventName === 'workflow_run' && run?.status === 'completed'
      && run.conclusion === 'success' && run.event === 'pull_request'
      && run.path === '.github/workflows/dependabot-generate.yml'
      && run.repository?.full_name === fullName && run.head_repository?.full_name === fullName
      && Array.isArray(run.pull_requests) && run.pull_requests.length === 1 && SHA.test(run.head_sha),
    'Unsupported dependency artifact producer');
    const current = await plan(api, { number: run.pull_requests[0].number });
    if (!current) return null;
    requireValid(run.head_branch === current.branch, 'Producer branch does not match the PR');
    if (run.head_sha === current.head) return current;
    const { data: commit } = await api.github.rest.git.getCommit({ ...api.context.repo, commit_sha: current.head });
    requireValid(commit.message === `${MARKER}\n\nPR #${current.number}; base ${current.base}`
      && commit.parents?.length === 1 && commit.parents[0].sha === run.head_sha,
    'Producer is stale and current head is not its generated commit');
    // Ref publication can succeed before dispatch fails; retry only CI, never old artifacts.
    await verifyCurrent(api, current);
    await dispatch(api, current, current.head);
    return null;
  } catch (error) {
    if (!(error instanceof Invalid)) throw error;
    api.core.info(`Dependency producer skipped: ${error.message}`);
    return null;
  }
}

async function publish(api, expected, artifactDirectory) {
  keys(expected, ['number', 'head', 'base', 'branch', 'backend', 'native', 'dryRun']);
  const current = await verifyCurrent(api, expected);
  const files = await payloadFiles(artifactDirectory, current);
  if (current.backend) await validateBackend(api, current, files);
  const mobileVersion = current.native ? capacitorVersion(await read(api, current.head, `${MOBILE}/package.json`)) : null;
  const changed = [];
  for (const file of files) {
    const previous = await read(api, current.head, file.path);
    if (BACKEND.includes(file.path)) nugetLock(file.content);
    else if (file.path === SWIFT) requireValid(swiftShape(file.content, mobileVersion) === swiftShape(previous), 'Unexpected Swift manifest change');
    else requireValid(isDeepStrictEqual(resolvedShape(file.content, mobileVersion), resolvedShape(previous)), 'Unexpected Swift pins change');
    if (previous !== file.content) changed.push(file);
  }
  if (current.dryRun) {
    api.core.info(`PR #${current.number}: dry run; ${changed.length} generated files differ; no writes`);
    return { changed: changed.map(file => file.path), dryRun: true };
  }
  if (!changed.length) {
    const { data: commit } = await api.github.rest.git.getCommit({ ...api.context.repo, commit_sha: current.head });
    // A prior publication may have succeeded before dispatch failed. Retrying is safe.
    if (commit.message === `${MARKER}\n\nPR #${current.number}; base ${current.base}`) {
      await verifyCurrent(api, current);
      await dispatch(api, current, current.head);
    }
    return { changed: [] };
  }
  const { github, context } = api;
  const { data: parent } = await github.rest.git.getCommit({ ...context.repo, commit_sha: current.head });
  const entries = [];
  for (const file of changed) {
    const { data: blob } = await github.rest.git.createBlob({ ...context.repo,
      content: Buffer.from(file.content, 'utf8').toString('base64'), encoding: 'base64' });
    entries.push({ path: file.path, mode: '100644', type: 'blob', sha: blob.sha });
  }
  const { data: generatedTree } = await github.rest.git.createTree({ ...context.repo, base_tree: parent.tree.sha, tree: entries });
  const { data: commit } = await github.rest.git.createCommit({ ...context.repo, tree: generatedTree.sha,
    parents: [current.head], message: `${MARKER}\n\nPR #${current.number}; base ${current.base}` });
  await verifyCurrent(api, current);
  // Nonforce fast-forward rejects a concurrent commit: the generated commit has only the pinned old head as parent.
  await github.rest.git.updateRef({ ...context.repo, ref: `heads/${current.branch}`, sha: commit.sha, force: false });
  await dispatch(api, current, commit.sha);
  return { changed: changed.map(file => file.path), head: commit.sha };
}

module.exports = { plan, publish, producerPlan };
