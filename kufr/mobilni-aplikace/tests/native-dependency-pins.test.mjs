import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const read = relative => readFile(new URL(relative, import.meta.url), 'utf8');

test('committed Capacitor npm and native pins agree before cap sync can modify them', async () => {
  const manifest = JSON.parse(await read('../package.json'));
  const version = manifest.dependencies['@capacitor/core'];
  assert.match(version, /^\d+\.\d+\.\d+(?:-[\w.-]+)?(?:\+[\w.-]+)?$/);
  assert.equal(manifest.dependencies['@capacitor/ios'], version);
  assert.equal(manifest.devDependencies['@capacitor/cli'], version);

  const lock = JSON.parse(await read('../package-lock.json'));
  for (const name of ['core', 'ios', 'cli']) {
    assert.equal(lock.packages[`node_modules/@capacitor/${name}`].version, version);
  }
  const swift = await read('../ios/App/CapApp-SPM/Package.swift');
  const nativeVersion = swift.match(/\.package\(url: "https:\/\/github\.com\/ionic-team\/capacitor-swift-pm\.git", exact: "([^"]+)"\)/);
  assert.ok(nativeVersion, 'expected the generated exact Capacitor package');
  assert.equal(nativeVersion[1], version);

  const resolved = JSON.parse(await read('../ios/App/App.xcodeproj/project.xcworkspace/xcshareddata/swiftpm/Package.resolved'));
  const pins = resolved.pins.filter(pin => pin.identity === 'capacitor-swift-pm');
  assert.equal(pins.length, 1);
  assert.equal(pins[0].state.version, version);
  assert.equal(pins[0].location, 'https://github.com/ionic-team/capacitor-swift-pm.git');
  assert.match(pins[0].state.revision, /^[a-f0-9]{40}$/);
});
