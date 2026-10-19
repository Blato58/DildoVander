// Real browser regression for a running isolated Twin backend. No physical input.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');

async function main() {
  const [origin, credentialFile, output] = process.argv.slice(2);
  assert(origin && credentialFile && output, 'origin, private fixture credential file, output directory required');
  assert(new URL(origin).hostname === 'localhost' || process.env.KUFR_TEST_VPS_PROOF === 'true',
    'VPS proof requires explicit opt-in; commands always address allowlisted sim-* identities');
  const credentials = JSON.parse(fs.readFileSync(credentialFile, 'utf8'));
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true,
    ...(process.env.KUFR_TEST_BROWSER ? { executablePath: process.env.KUFR_TEST_BROWSER } : {}) });
  const context = await browser.newContext({ viewport: { width: 1400, height: 900 } });
  const page = await context.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const login = async credential => {
    const response = await context.request.post(origin + '/v1/twin/session',
      { headers: { Origin: origin }, data: { credential } });
    assert.equal(response.status(), 200);
  };
  const command = async (body, id = 'sim-development') => {
    const response = await context.request.post(`${origin}/v1/twin/instances/${id}/commands`,
      { headers: { Origin: origin }, data: body });
    const result = await response.json();
    assert.equal(response.status(), 200, JSON.stringify(result));
    assert.equal(result.accepted, true, JSON.stringify(result));
    return result;
  };
  try {
    await login(credentials.control);
    await command({ action: 'stop' });
    await command({ action: 'inspect' });
    await command({ action: 'clock', paused: true });
    await page.goto(origin + '/twin');
    await page.locator('#twin-details > summary').click();
    await page.locator('#twin-refresh').click();
    await page.locator('#twin-source').selectOption('sim-development');
    await page.waitForFunction(() => document.querySelector('#twin-badge').textContent.includes('ONLINE'));
    await page.locator('#twin-slot').selectOption('3');
    await page.locator('#twin-variant').selectOption('main');
    await page.locator('#twin-details > summary').click();
    const matrices = () => page.evaluate(() => model.meshes.filter(mesh => /^tray_[1-6]$/.test(mesh.name))
      .map(mesh => ({ name: mesh.name, matrix: F2.movement(mesh), center: mesh.center, screen: F2.projectPoint(mesh.center) })));
    const before = await matrices();
    await page.screenshot({ path: path.join(output, 'desktop-rest.png') });
    await page.locator('#twin-details > summary').click();
    await page.locator('[data-twin-action="text"]').click();
    // Native TICK performs the real 10 ms owner loop; the browser only observes.
    await command({ action: 'clock', paused: true, stepMs: 5000 });
    await page.waitForFunction(() => F2.state().trayAngles[2] > 100);
    const after = await matrices();
    const changes = after.map((value, index) => ({ name: value.name, z: value.matrix[14] - before[index].matrix[14] }));
    assert.equal(changes.find(value => value.name === 'tray_3').z, 16);
    assert(changes.filter(value => value.name !== 'tray_3').every(value => value.z === 0), 'only correct CAD tray moved');
    await page.locator('#twin-details > summary').click();
    await page.screenshot({ path: path.join(output, 'desktop-raised.png') });
    const stateBeforeReload = await (await context.request.get(origin + '/v1/twin/instances/sim-development')).json();
    await page.reload();
    await page.locator('#twin-details > summary').click();
    await page.locator('#twin-refresh').click();
    await page.locator('#twin-source').selectOption('sim-development');
    await page.waitForFunction(() => F2.state().trayAngles[2] > 100);
    const stateAfterReload = await (await context.request.get(origin + '/v1/twin/instances/sim-development')).json();
    assert.equal(stateBeforeReload.snapshot.source.bootId, stateAfterReload.snapshot.source.bootId);
    assert.equal(stateBeforeReload.snapshot.timeMs, stateAfterReload.snapshot.timeMs);
    await command({ action: 'stop' });
    await page.waitForFunction(() => document.querySelector('#twin-trace-status').textContent.includes('událostí'));
    await page.locator('#twin-details > summary').click();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.screenshot({ path: path.join(output, 'mobile-stop.png') });
    assert(await page.locator('#twin-badge').isVisible());
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'mobile does not overflow');
    const postStop = await (await context.request.get(origin + '/v1/twin/instances/sim-development')).json();
    assert.equal(postStop.snapshot.reported.physicalVerification, 'unavailable');
    assert.equal(postStop.snapshot.model.slots[2].position, 1, 'Stop did not auto-lower');
    assert.equal(postStop.snapshot.reported.needsRecovery, true);
    await command({ action: 'release' });
    const integration = [];
    const integrationSkipped = process.env.KUFR_TEST_SKIP_INTEGRATION === 'true';
    if (!integrationSkipped) {
      await login(credentials.integration);
      const settings = await (await context.request.get(origin + '/v1/twin/instances/sim-integration/settings')).json();
      assert.equal(settings.engine, 'fake', 'integration browser acceptance must never call a paid provider');
      await command({ action: 'stop' }, 'sim-integration');
      await command({ action: 'inspect' }, 'sim-integration');
      for (let index = 0; index < 8; index++) {
        const result = await command({ action: 'integrationText', operationId: crypto.randomUUID(),
          text: 'Synthetic software test answer ' + index }, 'sim-integration');
        integration.push(result.code);
        if (result.code === 'integration-presentation-admitted') break;
      }
      assert(integration.includes('integration-presentation-admitted'), 'production backend fake conversation admitted presentation');
      await command({ action: 'release' }, 'sim-integration');
    }
    assert.deepEqual(errors, []);
    fs.writeFileSync(path.join(output, 'browser-result.json'), JSON.stringify({
      origin, viewport: [1400, 900], mobileViewport: [390, 844], matrices: changes,
      bootPreserved: true, stopNoAutoLower: true, integration, integrationSkipped, pageErrors: errors,
      physicalVerification: 'unavailable'
    }, null, 2));
    console.log('PASS browser: native input -> tray_3 GPU movement 16mm, reload, mobile, Stop; integration ' + (integrationSkipped ? 'explicitly skipped' : 'fake backend passed'));
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
