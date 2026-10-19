import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const clientSource = await readFile(new URL('../src/features/provisioning/ProvisioningClient.ts', import.meta.url), 'utf8');
const clientUrl = `data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(clientSource, { mode: 'transform' })).toString('base64')}`;
const panelSource = await readFile(new URL('../src/features/provisioning/ProvisioningPanel.ts', import.meta.url), 'utf8');
const panelCode = stripTypeScriptTypes(panelSource, { mode: 'transform' })
  .replace(/'\.\.\/\.\.\/\.\.\/\.\.\/design-system\/ui\.js'/, JSON.stringify(new URL('../../design-system/ui.js', import.meta.url).href))
  .replace(/import \{ registerPlugin \} from '@capacitor\/core';/, 'const registerPlugin = () => ({});')
  .replace(/'\.\/ProvisioningClient'/, JSON.stringify(clientUrl));
const { ProvisioningPanel, bridge } = await import(`data:text/javascript;base64,${Buffer.from(panelCode + '\nexport { bridge };').toString('base64')}`);

test('a queued network action retains the profile selected at the tap', async () => {
  for (const action of ['configure', 'test', 'delete']) {
    let release;
    const slot = new Promise(resolve => { release = resolve; });
    let submitted;
    bridge.configure = bridge.command = async options => { submitted = options; };
    const profile = { value: 'hotspot' };
    // Exercise the panel action boundary without a native dialog or a browser DOM.
    const panel = Object.create(ProvisioningPanel.prototype);
    panel.nativeMode = true;
    panel.busy = false;
    panel.element = { querySelector: () => profile };
    panel.client = { perform: async operation => { await slot; await operation(); } };
    const pending = panel.action(action);
    profile.value = 'fallback';
    release();
    await pending;
    assert.equal(submitted.profile, 'hotspot', action);
  }
});
