// One-shot App token issuer. Called through a captured pipe by coordination.py.
// No daemon, persisted token, deployment credential or third-party dependency.
import { createPrivateKey, createSign } from 'node:crypto';
import { readFile, realpath } from 'node:fs/promises';
import https from 'node:https';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const repository = 'DildoVander-tracking';
const owner = 'Blato58';

export function signJwt(appId, key, now = Math.floor(Date.now() / 1000)) {
  if (!/^\d+$/.test(appId)) throw Error('Invalid App ID');
  if (key.asymmetricKeyType !== 'rsa' || key.asymmetricKeyDetails.modulusLength < 2048) {
    throw Error('App signing requires an RSA key of at least 2048 bits');
  }
  const encode = value => Buffer.from(JSON.stringify(value)).toString('base64url');
  const input = `${encode({ alg: 'RS256', typ: 'JWT' })}.${encode({ iat: now - 60, exp: now + 540, iss: appId })}`;
  const signer = createSign('RSA-SHA256');
  signer.update(input);
  return `${input}.${signer.sign(key).toString('base64url')}`;
}

export function validatePermissions(permissions) {
  if (!permissions || permissions.issues !== 'write'
    || Object.entries(permissions).some(([key, value]) => !(
      (key === 'issues' && value === 'write') || (key === 'metadata' && value === 'read')))) {
    throw Error('App must grant only Issues write and Metadata read');
  }
}

function requestGitHub(apiPath, token, method = 'GET', data) {
  return new Promise((resolve, reject) => {
    const body = data === undefined ? undefined : JSON.stringify(data);
    const request = https.request({
      hostname: 'api.github.com', path: apiPath, method,
      headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'dildovander-coordination',
        ...(body ? { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) } : {}) },
    }, response => {
      // https.request does not follow redirects; never forward an App credential.
      if (response.statusCode < 200 || response.statusCode >= 300) {
        response.resume();
        reject(Error(`App authentication HTTP ${response.statusCode}`));
        return;
      }
      const chunks = [];
      let size = 0;
      response.on('data', chunk => {
        size += chunk.length;
        if (size > 1_000_000) {
          response.destroy(Error('Response too large'));
          return;
        }
        chunks.push(chunk);
      });
      response.on('error', () => reject(Error('App authentication response failed')));
      response.on('end', () => {
        try { resolve(JSON.parse(Buffer.concat(chunks).toString('utf8'))); }
        catch { reject(Error('Invalid App authentication response')); }
      });
    });
    request.setTimeout(15_000, () => request.destroy(Error('Timeout')));
    request.on('error', () => reject(Error('App authentication connection failed')));
    request.end(body);
  });
}

export async function mint(env = process.env, api = requestGitHub) {
  const appId = env.DILDOVANDER_COORDINATION_APP_ID;
  const installationId = env.DILDOVANDER_COORDINATION_INSTALLATION_ID;
  const keyPath = env.DILDOVANDER_COORDINATION_KEY_FILE;
  if (!/^\d+$/.test(appId ?? '') || !/^\d+$/.test(installationId ?? '') || !keyPath || !path.isAbsolute(keyPath)) {
    throw Error('Set numeric App/installation IDs and an absolute external key-file path');
  }
  const root = await realpath(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..'));
  const resolvedKey = await realpath(keyPath);
  const relative = path.relative(root, resolvedKey);
  if (!relative.startsWith(`..${path.sep}`) && !path.isAbsolute(relative)) {
    throw Error('App key must be outside the source checkout');
  }
  const pem = await readFile(resolvedKey);
  let jwt;
  try { jwt = signJwt(appId, createPrivateKey(pem)); }
  finally { pem.fill(0); }
  const installation = await api(`/app/installations/${installationId}`, jwt);
  if (installation.account?.login !== owner || String(installation.app_id) !== appId
    || installation.repository_selection !== 'selected' || installation.suspended_at) {
    throw Error('Unexpected App installation or account');
  }
  validatePermissions(installation.permissions);
  const issued = await api(`/app/installations/${installationId}/access_tokens`, jwt, 'POST', {
    repositories: [repository], permissions: { issues: 'write', metadata: 'read' },
  });
  validatePermissions(issued.permissions);
  if (typeof issued.token !== 'string' || !issued.token
    || !Number.isFinite(Date.parse(issued.expires_at)) || Date.parse(issued.expires_at) <= Date.now()) {
    throw Error('Invalid installation token response');
  }
  return { token: issued.token, expires_at: issued.expires_at };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    if (process.env.DILDOVANDER_ISSUER_PIPE !== '1') throw Error('Captured issuer pipe required');
    process.stdout.write(JSON.stringify(await mint()));
  }
  catch {
    // Crypto/filesystem errors can include sensitive details; emit a fixed error.
    process.stderr.write('Coordination App authentication failed; check isolated credential setup.\n');
    process.exitCode = 2;
  }
}
