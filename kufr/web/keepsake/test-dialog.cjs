/* Browser assertions only; this module is excluded from the published runtime. */
const assert = require('node:assert/strict');

async function assertSharedDialog(page, selector) {
  await page.keyboard.press('Tab');
  // Chromium can visit the modal's document focus sentinel at the cycle boundary.
  // A second Tab must return to an actual modal control, never the page behind it.
  if (!await page.locator(selector).evaluate(dialog => dialog.contains(document.activeElement))) {
    await page.keyboard.press('Tab');
  }
  const state = await page.locator(selector).evaluate(dialog => {
    const probe = document.createElement('div');
    probe.style.cssText = 'background:var(--aw-surface);border-radius:var(--aw-radius-card);border:1px solid var(--aw-border)';
    dialog.append(probe);
    const expected = getComputedStyle(probe);
    const actual = getComputedStyle(dialog);
    const result = {
      shared: dialog.classList.contains('aw-dialog'),
      background: actual.backgroundColor === expected.backgroundColor,
      radius: actual.borderRadius === expected.borderRadius,
      border: actual.borderColor === expected.borderColor,
      focused: dialog.contains(document.activeElement),
      focusVisible: document.activeElement.matches(':focus-visible')
        && getComputedStyle(document.activeElement).outlineStyle !== 'none',
      touchTargets: [...dialog.querySelectorAll('button, a, input, select')]
        .filter(control => control.getClientRects().length && !control.disabled)
        .every(control => control.getBoundingClientRect().height >= 44
          && control.getBoundingClientRect().width >= 44)
    };
    probe.remove();
    return result;
  });
  for (const [contract, passed] of Object.entries(state)) {
    assert.equal(passed, true, `${selector}: shared dialog ${contract}`);
  }
}

module.exports = { assertSharedDialog };
