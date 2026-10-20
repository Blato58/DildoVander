// Launch only the isolated account fixture. It clears inherited service
// configuration, has no provider registrations, and owns a temporary database.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { access } from 'node:fs/promises';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const appRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const repository = resolve(appRoot, '../..');
const assembly = process.env.KUFR_TESTS_DLL;
assert(assembly, 'Set KUFR_TESTS_DLL to the previously built BackendTests.dll.');
await access(resolve(repository, assembly));
await access(resolve(appRoot, 'dist/index.html'));

const fixture = spawn('dotnet', [resolve(repository, assembly), '--identity-fixture'], {
  cwd: repository, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
});
let fixtureExit;
const exited = new Promise(resolveExit => {
  fixture.once('exit', code => { fixtureExit = code ?? 1; resolveExit(); });
  fixture.once('error', () => { fixtureExit = 1; resolveExit(); });
});
let output = '';
let lineBuffer = '';
let origin;
let delivery;
let readyResolve;
let readyReject;
const ready = new Promise((resolveReady, rejectReady) => { readyResolve = resolveReady; readyReject = rejectReady; });
fixture.once('error', readyReject);
fixture.once('exit', () => readyReject(new Error(`Isolated account fixture exited before readiness (${fixtureExit}).`)));
fixture.stderr.on('data', data => { output = (output + data).slice(-12000); });
fixture.stdout.on('data', data => {
  output = (output + data).slice(-12000);
  lineBuffer += data;
  const lines = lineBuffer.split(/\r?\n/); lineBuffer = lines.pop();
  for (const line of lines) {
    if (line.startsWith('ACCOUNT_BROWSER_FIXTURE ')) origin = line.slice('ACCOUNT_BROWSER_FIXTURE '.length).trim();
    if (line.startsWith('ACCOUNT_BROWSER_DELIVERY ')) delivery = line.slice('ACCOUNT_BROWSER_DELIVERY '.length).trim();
    if (origin && delivery) readyResolve();
  }
});
const startupTimeout = setTimeout(() => readyReject(new Error('Isolated account fixture did not become ready within 60 seconds.')), 60000);
try {
  await ready;
  clearTimeout(startupTimeout);
  const browser = spawn(process.execPath, ['tests/accounts.browser.mjs'], {
    cwd: appRoot, windowsHide: true, stdio: 'inherit',
    env: { ...process.env, ACCOUNT_BROWSER_URL: origin, ACCOUNT_BOOTSTRAP_FILE: delivery },
  });
  const result = await new Promise((resolveResult, rejectResult) => {
    browser.once('error', rejectResult);
    browser.once('exit', code => resolveResult(code ?? 1));
  });
  assert.equal(result, 0, `Real account browser journey failed (${result}).`);
} catch (error) {
  // The fixture never prints enrollment links. Still redact capability-shaped
  // fragments before showing bounded diagnostics from a failed local service.
  console.error(output.replace(/#(?:invite|recovery)=[A-Za-z0-9_-]+/g, '#redacted'));
  throw error;
} finally {
  clearTimeout(startupTimeout);
  if (fixtureExit === undefined) {
    fixture.stdin.write('stop\n'); fixture.stdin.end();
    const forcedStop = setTimeout(() => fixture.kill(), 10000);
    await exited; clearTimeout(forcedStop);
  }
  if (delivery) {
    const privateRoot = resolve(dirname(delivery), '../..');
    await assert.rejects(access(privateRoot), error => error.code === 'ENOENT',
      'The isolated fixture must remove its private database, keys and delivery directory.');
  }
}
