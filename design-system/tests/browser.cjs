// Real-browser regressions against confined static files and local fake state only.
const assert = require('node:assert/strict');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

const root = path.resolve(__dirname, '../..');
const mobileDist = process.env.UI_MOBILE_DIST ? path.resolve(process.env.UI_MOBILE_DIST) : null;
const viewerHtml = process.env.UI_VIEWER_HTML ? path.resolve(process.env.UI_VIEWER_HTML) : null;
const installerHtml = process.env.UI_INSTALLER_HTML ? path.resolve(process.env.UI_INSTALLER_HTML) : null;
const screenshotDirectory = process.env.UI_SCREENSHOT_DIR ? path.resolve(process.env.UI_SCREENSHOT_DIR) : null;
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.ttf': 'font/ttf', '.svg': 'image/svg+xml', '.png': 'image/png' };

function confined(base, relative) {
  const file = path.resolve(base, relative);
  return file.startsWith(`${path.resolve(base)}${path.sep}`) ? file : null;
}

async function startServer() {
  const server = http.createServer(async (request, response) => {
    try {
      if (request.method !== 'GET' && request.method !== 'HEAD') {
        response.writeHead(405).end();
        return;
      }
      const url = new URL(request.url, 'http://127.0.0.1');
      const pathname = decodeURIComponent(url.pathname);
      let file;
      let simulator = false;
      let companion = false;
      if (pathname === '/viewer/' && viewerHtml) {
        file = viewerHtml;
      } else if (pathname === '/ios-preview/' && installerHtml) {
        file = installerHtml;
      } else if (pathname === '/ios-unavailable/') {
        file = path.join(root, 'backend/deploy/ip-https/ios-placeholder.html');
      } else if (pathname.startsWith('/companion/') && mobileDist) {
        file = confined(mobileDist, pathname.slice('/companion/'.length) || 'index.html');
        companion = pathname === '/companion/' || pathname.endsWith('/index.html');
      } else if (pathname.startsWith('/design-system/')) {
        file = confined(path.join(root, 'design-system'), pathname.slice('/design-system/'.length));
      } else if (pathname.startsWith('/design-system/')) {
        file = confined(path.join(root, 'design-system'), pathname.slice('/design-system/'.length));
      } else if (pathname.startsWith('/simulator/')) {
        const relative = pathname.slice('/simulator/'.length);
        if (['', 'dashboard.html', 'dashboard.js', 'dashboard-ui.js'].includes(relative)) {
          file = path.join(root, 'firmware/simulator', relative || 'dashboard.html');
          simulator = relative === '' || relative === 'dashboard.html';
        }
      } else if (pathname.startsWith('/firmware/diagnostics/servo-bench/web/')) {
        const prefix = '/firmware/diagnostics/servo-bench/web/';
        file = confined(path.join(root, prefix), pathname.slice(prefix.length) || 'index.html');
      } else if (pathname === '/favicon.ico') {
        response.writeHead(204).end();
        return;
      }
      if (!file || /(?:^|[/\\])(?:node_modules|tests)(?:[/\\]|$)/.test(file)) {
        response.writeHead(404).end();
        return;
      }
      const stat = await fs.stat(file);
      if (stat.isDirectory()) file = path.join(file, 'index.html');
      const extension = path.extname(file);
      let content = await fs.readFile(file);
      if (simulator) {
        content = Buffer.from(content.toString().replace('<body ', '<body data-static-preview '));
      }
      if (companion) {
        content = Buffer.from(content.toString().replace('<head>', '<head><meta name="kufr-browser-mode" content="local-fake">'));
      }
      response.writeHead(200, { 'Content-Type': `${mime[extension] ?? 'application/octet-stream'}${['.html', '.js', '.mjs', '.css', '.json'].includes(extension) ? '; charset=utf-8' : ''}`, 'Cache-Control': 'no-store' });
      response.end(content);
    } catch {
      response.writeHead(404).end();
    }
  });
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  return { server, origin: `http://127.0.0.1:${server.address().port}` };
}

async function noOverflow(page, label) {
  const dimensions = await page.evaluate(() => ({ width: innerWidth, content: document.documentElement.scrollWidth }));
  assert.ok(dimensions.content <= dimensions.width + 1, `${label}: horizontal page overflow ${JSON.stringify(dimensions)}`);
}

async function screenshot(page, name) {
  if (!screenshotDirectory) return;
  const theme = await page.locator('html').getAttribute('data-theme');
  name = `${theme}-${name}`;
  await page.screenshot({ path: path.join(screenshotDirectory, `${name}.png`), fullPage: true });
  await page.screenshot({ path: path.join(screenshotDirectory, `${name}-viewport.png`) });
}

async function assertOffline(page, label) {
  const attempts = await page.evaluate(() => globalThis.__uiTransportAttempts);
  assert.deepEqual(attempts, [], `${label}: transport must remain unused`);
}

async function playground(page, origin) {
  await page.goto(`${origin}/design-system/playground/`);
  await page.locator('#open-dialog').waitFor();
  await page.evaluate(() => document.fonts.ready);
  for (const width of [320, 390, 768, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    for (const theme of ['olivander', 'lord-voldemort']) {
      await page.locator('#theme').selectOption(theme);
      await page.waitForFunction((selectedTheme) => {
        const style = getComputedStyle(document.querySelector('#buttons .aw-button--primary'));
        return style.backgroundColor === (selectedTheme === 'olivander' ? 'rgb(184, 192, 255)' : 'rgb(96, 106, 204)') &&
          style.color === (selectedTheme === 'olivander' ? 'rgb(27, 36, 48)' : 'rgb(255, 255, 255)');
      }, theme);
      const styles = await page.evaluate(() => {
        const button = document.querySelector('#buttons .aw-button--primary');
        const body = getComputedStyle(document.body);
        const computed = getComputedStyle(button);
        return {
          background: body.backgroundColor, foreground: body.color, font: body.fontFamily,
          actionBackground: computed.backgroundColor, actionForeground: computed.color,
          radius: computed.borderRadius, token: body.getPropertyValue('--aw-touch').trim(),
          mist: body.getPropertyValue('--aw-mist').trim(), midnight: body.getPropertyValue('--aw-midnight').trim(),
          steel: body.getPropertyValue('--aw-steel').trim(), periwinkle: body.getPropertyValue('--aw-periwinkle').trim(),
          interLoaded: Array.from(document.fonts).some((font) => font.family === 'Inter' && font.status === 'loaded'),
        };
      });
      const expected = theme === 'olivander'
        ? ['rgb(27, 36, 48)', 'rgb(238, 241, 245)', 'rgb(184, 192, 255)', 'rgb(27, 36, 48)']
        : ['rgb(15, 15, 17)', 'rgb(227, 228, 230)', 'rgb(96, 106, 204)', 'rgb(255, 255, 255)'];
      assert.deepEqual([styles.background, styles.foreground, styles.actionBackground, styles.actionForeground], expected, `${theme} ${width}: computed color contract`);
      assert.deepEqual([styles.mist, styles.midnight, styles.steel, styles.periwinkle], ['#EEF1F5', '#1B2430', '#8A94A6', '#B8C0FF']);
      assert.equal(styles.radius, '6px');
      assert.equal(styles.token, '44px');
      assert.ok(styles.font.includes('Inter'));
      assert.equal(styles.interLoaded, true, 'self-hosted Inter is loaded');
      await noOverflow(page, `playground ${theme} ${width}`);
      const smallTargets = await page.locator('button:visible, input:visible, select:visible').evaluateAll((controls) => controls.map((control) => ({ text: control.textContent.trim(), rect: control.getBoundingClientRect() })).filter(({ rect }) => rect.width < 43.9 || rect.height < 43.9).map(({ text, rect }) => ({ text, width: rect.width, height: rect.height })));
      assert.deepEqual(smallTargets, [], `playground ${theme} ${width}: minimum 44px touch targets`);
      await screenshot(page, `playground-${theme}-${width}`);
    }
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await noOverflow(page, `playground 200% text ${width}`);
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  }

  const validation = await page.locator('#example-name').evaluate((input) => ({ invalid: input.getAttribute('aria-invalid'), label: input.labels[0]?.textContent, descriptions: input.getAttribute('aria-describedby').split(/\s+/).map((id) => Boolean(document.getElementById(id))) }));
  assert.equal(validation.invalid, 'true');
  assert.equal(validation.label, 'Název ukázky');
  assert.deepEqual(validation.descriptions, [true, true]);

  await page.locator('#tab-wands').focus();
  await page.keyboard.press('ArrowRight');
  assert.equal(await page.locator('#tab-history').getAttribute('aria-selected'), 'true');
  assert.equal(await page.locator('#panel-wands').isVisible(), false);
  assert.equal(await page.locator('#panel-history').isVisible(), true);
  await page.keyboard.press('ArrowRight');
  assert.equal(await page.locator('#tab-wands').getAttribute('aria-selected'), 'true', 'Arrow skips disabled tab and wraps');
  await page.keyboard.press('End');
  assert.equal(await page.locator('#tab-history').getAttribute('aria-selected'), 'true');
  await page.keyboard.press('Home');
  assert.equal(await page.locator('#tab-wands').getAttribute('aria-selected'), 'true');
  assert.equal(await page.locator('#tab-device').getAttribute('tabindex'), '-1');
  assert.equal(await page.locator('[role=tab][tabindex="0"]').count(), 1);
  assert.equal(await page.locator('#tab-wands').evaluate((tab) => getComputedStyle(tab).outlineWidth), '2px');

  await page.locator('#open-dialog').click();
  assert.equal(await page.locator('#example-dialog').evaluate((dialog) => dialog.open), true);
  assert.equal(await page.locator('#example-dialog [autofocus]').evaluate((control) => control === document.activeElement), true);
  await page.locator('#open-dialog').evaluate((control) => control.focus());
  assert.equal(await page.locator('#example-dialog').evaluate((dialog) => dialog.contains(document.activeElement)), true, 'modal prevents background programmatic focus');
  for (const key of ['Tab', 'Tab', 'Tab', 'Shift+Tab', 'Shift+Tab']) {
    await page.keyboard.press(key);
    // Chromium can temporarily report body when Tab reaches the browser chrome sentinel.
    // No background document control may receive focus while the modal is open.
    assert.equal(await page.locator('#example-dialog').evaluate((dialog) => dialog.contains(document.activeElement) || document.activeElement === document.body), true, 'native dialog excludes background controls from keyboard focus');
  }
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('#example-dialog').evaluate((dialog) => dialog.open), false);
  assert.equal(await page.locator('#open-dialog').evaluate((control) => control === document.activeElement), true, 'Escape restores invoker focus');
  await page.locator('#open-dialog').click();
  await page.locator('#example-dialog [value="confirmed"]').click();
  assert.equal(await page.locator('#example-dialog').evaluate((dialog) => dialog.open), false);
  assert.equal(await page.locator('#open-dialog').evaluate((control) => control === document.activeElement), true, 'native method=dialog restores invoker focus');

  await page.emulateMedia({ reducedMotion: 'reduce', colorScheme: 'light' });
  await page.locator('#theme').selectOption('olivander');
  const reduced = await page.locator('#buttons button').first().evaluate((control) => ({ transition: getComputedStyle(control).transitionDuration, duration: getComputedStyle(document.body).getPropertyValue('--aw-motion-fast').trim(), background: getComputedStyle(document.body).backgroundColor }));
  assert.ok(reduced.transition.split(',').every((duration) => duration.trim() === '0s'));
  assert.equal(reduced.duration, '0ms');
  assert.equal(reduced.background, 'rgb(27, 36, 48)', 'identity remains stable when the OS changes');
  const nested = await page.evaluate(() => {
    const outer = document.createElement('section');
    outer.dataset.theme = 'dark';
    const inner = document.createElement('p');
    inner.dataset.theme = 'light';
    inner.textContent = 'Nested theme';
    outer.append(inner);
    document.body.append(outer);
    const colors = [getComputedStyle(outer).color, getComputedStyle(inner).color];
    outer.remove();
    return colors;
  });
  assert.deepEqual(nested, ['rgb(238, 241, 245)', 'rgb(27, 36, 48)']);
  await page.emulateMedia({ reducedMotion: 'no-preference', colorScheme: 'dark' });
  await assertOffline(page, 'playground');
  console.log('PASS playground: Lord Voldemort/Olivander 320/390/768/1280, 200% text, tokens, touch, tabs, dialog, fields, reduced motion');
}

async function simulator(page, origin) {
  await page.goto(`${origin}/simulator/`);
  await page.locator('#connect.aw-button').waitFor();
  assert.equal(await page.locator('body').getAttribute('data-static-preview'), '');
  assert.equal(await page.locator('#static-preview-help').isVisible(), true);
  assert.equal(await page.locator('#connect').isDisabled(), true);
  assert.equal(await page.locator('#disconnect').isDisabled(), true);
  assert.equal(await page.locator('#token').isDisabled(), true);
  // A synthetic event reaches registered JS even when native disabled blocks user clicks.
  await page.locator('#connect').dispatchEvent('click');
  await page.locator('#token').evaluate((input) => input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true })));
  await page.waitForTimeout(50);
  assert.equal(await page.locator('#connect').isDisabled(), true);
  assert.equal(await page.locator('#disconnect').isDisabled(), true);
  await assertOffline(page, 'static simulator click/Enter guard');
  for (const width of [320, 390, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await noOverflow(page, `simulator ${width}`);
    assert.ok((await page.locator('#model-guidance').textContent()).includes('Veřejný náhled simulace'));
    await screenshot(page, `simulator-static-${width}`);
  }
  const before = await page.locator('html').getAttribute('data-theme');
  await page.locator('#theme-toggle').click();
  assert.equal(await page.locator('html').getAttribute('data-theme'), before === 'olivander' ? 'lord-voldemort' : 'olivander');
  console.log('PASS simulator: real modules, static-preview credential/observer guard, theme, responsive layout');
}

async function servo(page, origin) {
  await page.goto(`${origin}/firmware/diagnostics/servo-bench/web/`);
  await page.locator('#cal-move.aw-button').waitFor();
  assert.equal(await page.locator('#arm').isDisabled(), true);
  assert.equal(await page.locator('#pulse').isDisabled(), true);
  assert.equal(await page.locator('#cal-move').isDisabled(), true);
  assert.equal(await page.locator('#stop').isDisabled(), true);
  assert.ok((await page.locator('#connection').textContent()).includes('Odpojeno'));
  for (const width of [320, 390, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await noOverflow(page, `servo ${width}`);
    assert.equal(await page.locator('#bench-readiness').isVisible(), true);
    assert.equal(await page.locator('#stop').evaluate((control) => getComputedStyle(control).minHeight), '60px');
    await screenshot(page, `servo-disconnected-${width}`);
  }
  await assertOffline(page, 'disconnected servo');
  console.log('PASS servo: shared modules/styles and disabled disconnected safety controls; no serial action');
}

async function installer(page, origin) {
  const routes = ['/ios-unavailable/', ...(installerHtml ? ['/ios-preview/'] : [])];
  for (const route of routes) {
    await page.goto(origin + route);
    assert.equal(await page.locator('h1').textContent(), 'DildoVander pro iPhone');
    for (const width of [320, 390, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await noOverflow(page, `installer ${route} ${width}`);
      await screenshot(page, `installer-${route.includes('unavailable') ? 'unavailable' : 'ready'}-${width}`);
    }
    if (route === '/ios-preview/') {
      await page.locator('summary').click();
      assert.ok((await page.locator('details').textContent()).includes('Safari'));
      assert.ok((await page.locator('[href^="itms-services:"]').getAttribute('href')).includes('download-manifest'));
      await noOverflow(page, 'installer recovery expanded');
    } else {
      assert.equal(await page.locator('[href^="itms-services:"]').count(), 0, 'unavailable build cannot promise installation');
    }
    await assertOffline(page, `installer ${route}`);
  }
  console.log('PASS installer: truthful unavailable/ready states, Safari recovery and 320/390/1280 layout; no install invoked');
}

async function viewer(page, origin) {
  if (!viewerHtml) {
    console.log('SKIP generated offline viewer: set UI_VIEWER_HTML to preview_ui_fixture.py output');
    return;
  }
  const requests = [];
  const requestListener = (request) => {
    if (/^https?:/.test(request.url())) requests.push(request.url());
  };
  page.on('request', requestListener);
  try {
    for (const width of [320, 390, 1280]) {
      await page.setViewportSize({ width, height: 900 });
      await page.goto(`${origin}/viewer/`);
      await page.waitForFunction(() => Boolean(window.F2?.projectPoint));
      await page.locator('#viewer-status').filter({ hasText: 'SYNTETICKÁ UI FIXTURE' }).waitFor();
      assert.equal(await page.locator('#viewer-error').isVisible(), false, 'actual WebGL viewer rendered without fallback');
      assert.equal(await page.locator('#download-package').isVisible(), false, 'synthetic fixture cannot imply a CAD download');
      assert.equal(await page.locator('#mode-actions .aw-button').count(), 4, 'mode toolbar uses the shared button runtime');
      for (const id of ['show-open', 'view-reset', 'view-top', 'play-main', 'play-short', 'effect-stop']) {
        assert.equal(await page.locator(`#${id}`).evaluate((control) => control.classList.contains('aw-button')), true, `${id} uses shared button styling`);
      }
      const canvasSize = await page.locator('#view').evaluate((canvas) => ({ width: canvas.width, height: canvas.height }));
      assert.ok(canvasSize.width > 0 && canvasSize.height > 0, 'viewer owns an actual rendered canvas');
      await noOverflow(page, `offline viewer ${width}`);

      const projection = () => page.evaluate(() => window.F2.projectPoint([130, 90, 0]));
      const overview = await projection();
      await page.locator('#camera-menu > summary').click();
      await page.locator('#view-top').click();
      await page.waitForFunction(() => document.getElementById('view-top').getAttribute('aria-pressed') === 'true');
      assert.equal(await page.locator('#view-top').getAttribute('aria-pressed'), 'true');
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const top = await projection();
      assert.notDeepEqual(top, overview, 'top camera changes the real projected geometry');
      await page.locator('#camera-menu > summary').click();
      await page.locator('#view-reset').click();
      await page.waitForFunction(() => document.getElementById('view-overview').getAttribute('aria-pressed') === 'true');
      assert.equal(await page.locator('#view-overview').getAttribute('aria-pressed'), 'true');
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      assert.notDeepEqual(await projection(), top, 'reset restores the overview projection');

      if (width === 390) {
        assert.equal(await page.locator('#workspace-dock').isVisible(), true);
        for (const [toggle, panel, close] of [['explorer-toggle', 'sidebar', 'explorer-close'], ['inspector-toggle', 'inspector-rail', 'inspector-close']]) {
          await page.locator(`#${toggle}`).click();
          assert.equal(await page.locator(`#${toggle}`).getAttribute('aria-expanded'), 'true');
          assert.equal(await page.locator(`#${panel}`).getAttribute('data-open'), 'true');
          assert.equal(await page.locator(`#${close}`).evaluate((control) => control === document.activeElement), true, 'mobile panel places focus on its close action');
          await page.keyboard.press('Escape');
          assert.equal(await page.locator(`#${toggle}`).getAttribute('aria-expanded'), 'false');
          assert.equal(await page.locator(`#${toggle}`).evaluate((control) => control === document.activeElement), true, 'closing mobile panel restores dock focus');
        }
      }

      await page.locator('#show-open').click();
      assert.equal(await page.locator('#show-inspector').evaluate((dialog) => dialog.tagName === 'DIALOG' && dialog.open), true);
      assert.equal(await page.locator('#show-close').evaluate((control) => control === document.activeElement), true);
      assert.equal(await page.locator('#show-open').getAttribute('aria-expanded'), 'true');
      await page.keyboard.press('Escape');
      assert.equal(await page.locator('#show-inspector').evaluate((dialog) => dialog.open), false);
      // Native dialog close is a queued event; wait for its owning state callback.
      await page.waitForFunction(() => document.getElementById('show-open').getAttribute('aria-expanded') === 'false');
      assert.equal(await page.locator('#show-open').getAttribute('aria-expanded'), 'false');
      assert.equal(await page.locator('#show-open').evaluate((control) => control === document.activeElement), true, 'timeline Escape restores invoker');
      await page.locator('#show-open').click();
      await page.locator('#show-close').click();
      await page.waitForFunction(() => document.getElementById('show-open').getAttribute('aria-expanded') === 'false');
      assert.equal(await page.locator('#show-open').evaluate((control) => control === document.activeElement), true, 'timeline close action restores invoker');
      await noOverflow(page, `offline viewer after controls ${width}`);
      await assertOffline(page, `offline viewer ${width}`);
      await screenshot(page, `viewer-synthetic-${width}`);
      await page.locator('#assembly-mode').click();
      await page.locator('#assembly-next').click();
      const stepTitle = await page.locator('#assembly-current-title').textContent();
      assert.ok(stepTitle.includes('1 / 2'), 'assembly context comes from the current guide step');
      await page.locator('#guide-toggle').click();
      assert.equal(await page.locator('#assembly-panel').isVisible(), false);
      assert.equal(await page.locator('#assembly-current').isVisible(), true, 'current step stays visible with instructions collapsed');
      const explorerToggle = page.locator('#explorer-toggle');
      if (await explorerToggle.isVisible()) await explorerToggle.click();
      await page.locator('.layers-panel > summary').click();
      await page.locator('#cover').uncheck();
      if (await page.locator('#explorer-close').isVisible()) await page.locator('#explorer-close').click();
      await page.locator('#restore-view').click();
      await page.locator('#restore-view').waitFor({ state: 'hidden' });
      assert.equal(await page.locator('#cover').isChecked(), true, 'recovery restores geometry toggles');
      assert.equal(await page.locator('#assembly-current-title').textContent(), stepTitle, 'visibility recovery preserves assembly progress');
      await noOverflow(page, `assembly context ${width}`);
      await screenshot(page, `assembly-context-${width}`);
    }
    assert.deepEqual(requests, Array(3).fill(`${origin}/viewer/`), 'each offline viewer document requires zero additional network resources');
    console.log('PASS generated offline viewer: camera, mobile docks, timeline focus, persistent assembly context and visibility recovery at 320/390/1280; zero extra resources');
  } finally {
    page.off('request', requestListener);
  }
}

async function main() {
  if (screenshotDirectory) await fs.mkdir(screenshotDirectory, { recursive: true });
  const { server, origin } = await startServer();
  let browser;
  try {
    browser = await chromium.launch({ headless: true, channel: process.env.UI_BROWSER_CHANNEL ?? 'chromium' });
    const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, colorScheme: 'dark' });
    const errors = [];
    const blocked = [];
    context.on('page', (page) => {
      page.on('pageerror', (error) => errors.push(error.message));
      page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });
    });
    await context.route('**/*', async (route) => {
      const request = route.request();
      if (!request.url().startsWith(`${origin}/`) || ['fetch', 'xhr', 'eventsource', 'websocket'].includes(request.resourceType())) {
        blocked.push(`${request.resourceType()} ${request.url()}`);
        await route.abort('blockedbyclient');
      } else await route.continue();
    });
    await context.addInitScript(() => {
      globalThis.__uiTransportAttempts = [];
      globalThis.fetch = (...args) => {
        globalThis.__uiTransportAttempts.push(`fetch ${String(args[0])}`);
        return Promise.reject(new Error('UI regression fixture forbids transport'));
      };
      for (const name of ['WebSocket', 'EventSource']) {
        globalThis[name] = class {
          constructor(url) {
            globalThis.__uiTransportAttempts.push(`${name} ${String(url)}`);
            throw new Error('UI regression fixture forbids transport');
          }
        };
      }
      if ('serial' in navigator) {
        Object.defineProperty(navigator, 'serial', { configurable: true, value: {
          requestPort() {
            globalThis.__uiTransportAttempts.push('serial requestPort');
            return Promise.reject(new Error('UI regression fixture forbids serial access'));
          },
          getPorts() {
            globalThis.__uiTransportAttempts.push('serial getPorts');
            return Promise.resolve([]);
          },
          addEventListener() {},
          removeEventListener() {},
        } });
      }
    });
    const page = await context.newPage();
    page.setDefaultTimeout(10000);
    await playground(page, origin);
    for (const theme of ['lord-voldemort', 'olivander']) {
      for (const inspect of [simulator, servo, installer, viewer]) {
        await page.evaluate(selected => globalThis.DildoVanderTheme.setTheme(selected), theme);
        await inspect(page, origin);
      }
    }
    assert.deepEqual(blocked, [], 'all resources remain confined to this loopback static server');
    assert.deepEqual(errors, [], 'no browser console or module errors');
    await context.close();
    if (viewerHtml) {
      const offline = await browser.newContext({ offline: true, viewport: { width: 390, height: 900 } });
      try {
        const filePage = await offline.newPage();
        const extraRequests = [];
        const fileErrors = [];
        const fileUrl = pathToFileURL(viewerHtml).href;
        filePage.on('request', (request) => { if (request.url() !== fileUrl) extraRequests.push(request.url()); });
        filePage.on('pageerror', (error) => fileErrors.push(error.message));
        await filePage.goto(fileUrl);
        await filePage.waitForFunction(() => Boolean(window.F2?.projectPoint && window.VapourUI?.button));
        await filePage.locator('#viewer-status').filter({ hasText: 'SYNTETICKÁ UI FIXTURE' }).waitFor();
        await filePage.locator('#show-open').click();
        await filePage.keyboard.press('Escape');
        await filePage.waitForFunction(() => document.getElementById('show-open').getAttribute('aria-expanded') === 'false');
        assert.equal(await filePage.locator('#show-open').evaluate((control) => control === document.activeElement), true);
        assert.deepEqual(extraRequests, [], 'literal file viewer has zero subresource requests with networking offline');
        assert.deepEqual(fileErrors, [], 'literal file viewer has no script errors');
        console.log('PASS literal file:// viewer with browser network offline and native timeline focus');
      } finally {
        await offline.close();
      }
    }
    console.log(`PASS real-browser UI regressions (${process.env.UI_BROWSER_CHANNEL ?? 'chromium'} on ${os.platform()})`);
  } finally {
    if (browser) await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }
}

main().catch((error) => { console.error(error); process.exitCode = 1; });
