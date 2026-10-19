const { test } = require('node:test');
const assert = require('node:assert/strict');
const { generateKeyPairSync, createVerify } = require('node:crypto');
const { mkdtemp, writeFile, rm, rmdir } = require('node:fs/promises');
const { spawnSync } = require('node:child_process');
const os = require('node:os');
const path = require('node:path');

const library = import('../coordination-token.mjs');

test('App JWT is signed with bounded lifetime and the declared App identity', async () => {
  const { signJwt } = await library;
  const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
  const jwt = signJwt('123', privateKey, 1000);
  const [header, payload, signature] = jwt.split('.');
  assert.deepEqual(JSON.parse(Buffer.from(header, 'base64url')), { alg: 'RS256', typ: 'JWT' });
  assert.deepEqual(JSON.parse(Buffer.from(payload, 'base64url')), { iat: 940, exp: 1540, iss: '123' });
  const verifier = createVerify('RSA-SHA256');
  verifier.update(`${header}.${payload}`);
  assert.equal(verifier.verify(publicKey, Buffer.from(signature, 'base64url')), true);
  assert.throws(() => signJwt('bad', privateKey));
});

test('App permission validation rejects all capability expansion', async () => {
  const { validatePermissions } = await library;
  validatePermissions({ issues: 'write', metadata: 'read' });
  for (const permissions of [null, {}, { issues: 'read' },
    { issues: 'write', contents: 'read' }, { issues: 'write', actions: 'write' },
    { issues: 'write', secrets: 'read' }, { issues: 'write', metadata: 'write' }]) {
    assert.throws(() => validatePermissions(permissions));
  }
});

test('one-shot issuance narrows repository and permissions and rejects wrong installations', async () => {
  const { mint } = await library;
  const directory = await mkdtemp(path.join(os.tmpdir(), 'coordination-key-fixture-'));
  const keyFile = path.join(directory, 'synthetic.pem');
  try {
    const { privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
    await writeFile(keyFile, privateKey.export({ format: 'pem', type: 'pkcs8' }));
    const env = { DILDOVANDER_COORDINATION_APP_ID: '123', DILDOVANDER_COORDINATION_INSTALLATION_ID: '456',
      DILDOVANDER_COORDINATION_KEY_FILE: keyFile };
    const permissions = { issues: 'write', metadata: 'read' };
    const installation = { account: { login: 'Blato58' }, app_id: 123,
      repository_selection: 'selected', permissions };
    const calls = [];
    const api = async (url, jwt, method, body) => {
      calls.push({ url, method, body });
      return method === 'POST' ? { token: 'synthetic', permissions,
        expires_at: new Date(Date.now() + 3600_000).toISOString() } : installation;
    };
    assert.equal((await mint(env, api)).token, 'synthetic');
    assert.deepEqual(calls[1].body, { repositories: ['DildoVander-tracking'], permissions });
    assert.equal(calls[1].url, '/app/installations/456/access_tokens');
    for (const change of [() => { installation.account.login = 'other'; },
      () => { installation.account.login = 'Blato58'; installation.repository_selection = 'all'; },
      () => { installation.repository_selection = 'selected'; installation.permissions = { ...permissions, contents: 'read' }; }]) {
      change();
      await assert.rejects(mint(env, api));
    }
  } finally { await rm(keyFile, { force: true }); await rmdir(directory); }
});

test('direct invocation refuses to print credentials outside the captured client pipe', () => {
  const env = { ...process.env };
  delete env.DILDOVANDER_ISSUER_PIPE;
  const result = spawnSync(process.execPath, [path.resolve(__dirname, '../coordination-token.mjs')],
    { env, encoding: 'utf8' });
  assert.equal(result.status, 2);
  assert.equal(result.stdout, '');
  assert.match(result.stderr, /authentication failed/);
});
