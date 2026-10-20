import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const controller = readFileSync(new URL('../ios/App/App/KufrBridgeViewController.swift', import.meta.url), 'utf8');
const script = controller.match(/private static let viewportScript = """\n([\s\S]*?)\n    """/)[1];

for (const existing of [true, false]) {
  test(`native viewport sets a fixed scale (${existing ? 'existing' : 'missing'} meta)`, () => {
    const meta = existing ? { name: 'viewport', content: 'width=device-width, initial-scale=1' } : null;
    const children = meta ? [meta] : [];
    const document = {
      querySelector: () => children.find(child => child.name === 'viewport') ?? null,
      createElement: () => ({}),
      head: { appendChild: child => children.push(child) },
    };
    vm.runInNewContext(script, { document });
    vm.runInNewContext(script, { document });
    assert.equal(children.length, 1, 'reloading policy must not duplicate the viewport');
    const content = children[0].content.split(',').map(value => value.trim());
    for (const setting of ['width=device-width', 'initial-scale=1', 'minimum-scale=1', 'maximum-scale=1', 'user-scalable=no', 'viewport-fit=cover']) {
      assert.ok(content.includes(setting), setting);
    }
  });
}

test('public browser viewport remains zoomable', () => {
  const html = readFileSync(new URL('../index.html', import.meta.url), 'utf8');
  const viewport = html.match(/<meta name="viewport"[^>]+>/)[0];
  assert.doesNotMatch(viewport, /user-scalable=no|maximum-scale=1/);
});

test('packaged iPhone and iPad shell permits portrait only', () => {
  const plist = readFileSync(new URL('../ios/App/App/Info.plist', import.meta.url), 'utf8');
  for (const key of ['UISupportedInterfaceOrientations', 'UISupportedInterfaceOrientations~ipad']) {
    const start = plist.indexOf(`<key>${key}</key>`);
    assert.ok(start >= 0, key);
    const array = plist.slice(start).match(/<array>([\s\S]*?)<\/array>/)[1];
    assert.deepEqual([...array.matchAll(/<string>([^<]+)<\/string>/g)].map(match => match[1]), ['UIInterfaceOrientationPortrait']);
  }
  assert.match(plist, /<key>UIRequiresFullScreen<\/key>\s*<true\/>/);
});
