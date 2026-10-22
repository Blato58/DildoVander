const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const workflows = path.resolve(__dirname, '../../.github/workflows');
const ci = fs.readFileSync(path.join(workflows, 'ci.yml'), 'utf8');

function job(name) {
  return ci.split(`\n  ${name}:\n`)[1].split(/\n  [\w-]+:\n/)[0];
}

function condition(name, github, needs = {}) {
  const expression = job(name).match(/^    if: (.+)$/m)[1];
  return new Function('github', 'needs', 'always', `return ${expression}`)(github, needs, () => true);
}

function concurrencyGroup(github, inputs = {}) {
  const expression = ci.match(/^  group: ci-\$\{\{ (.+) \}\}$/m)[1];
  return new Function('github', 'inputs', 'format', `return ${expression}`)(
    github, inputs, (template, value) => template.replace('{0}', value),
  );
}

test('metadata edits skip runners and cannot supersede current product validation', () => {
  const validation = { event: { action: 'synchronize', changes: {}, pull_request: { number: 42 } }, run_id: 1 };
  const metadata = { event: { action: 'edited', changes: {}, pull_request: { number: 42 } }, run_id: 2 };
  const baseChange = { event: { action: 'edited', changes: { base: {} }, pull_request: { number: 42 } }, run_id: 3 };
  for (const name of ['secret-scan', 'changes', 'required']) {
    assert.equal(condition(name, metadata), false, name);
    assert.equal(condition(name, validation), true, name);
    assert.equal(condition(name, baseChange), true, name);
  }
  assert.notEqual(concurrencyGroup(metadata), concurrencyGroup(validation));
  assert.equal(concurrencyGroup(baseChange), concurrencyGroup(validation));
  const checkNameExpression = job('required').match(/^    name: \$\{\{ (.+) \}\}$/m)[1];
  const checkName = new Function('github', `return ${checkNameExpression}`);
  assert.equal(checkName(metadata), 'CI metadata');
  assert.equal(checkName(validation), 'CI required');
  assert.equal(checkName(baseChange), 'CI required');
  assert.match(ci, /cancel-in-progress:.*github.event_name == 'pull_request'/);
  assert.match(ci, /merge_group:\s+types: \[checks_requested\]/);
});

test('site requires selected web build success and permits independent site builds', () => {
  for (const [webSelected, webResult, expected] of [
    ['true', 'success', true], ['true', 'failure', false], ['true', 'cancelled', false], ['true', 'skipped', false],
    ['false', 'skipped', true],
  ]) {
    const needs = { changes: { result: 'success', outputs: { web: webSelected, site: 'true' } }, web: { result: webResult } };
    assert.equal(condition('site', {}, needs), expected);
    needs.changes.result = 'failure';
    assert.equal(condition('site', {}, needs), false);
  }
  assert.match(job('site'), /needs: \[changes, web\]/);
  assert.match(job('site'), /reuse-web-dist: \$\{\{ needs.changes.outputs.web == 'true' \}\}/);
  assert.match(job('web'), /share-web-dist: \$\{\{ needs.changes.outputs.site == 'true' \}\}/);
});

test('site reuses same-run compiled dist while main promotion artifact names remain stable', () => {
  const web = fs.readFileSync(path.join(workflows, 'kufr-web.yml'), 'utf8');
  const site = fs.readFileSync(path.join(workflows, 'kufr-site.yml'), 'utf8');
  assert.match(web, /if: inputs.share-web-dist[\s\S]+name: shared-web-dist/);
  assert.match(site, /if: inputs.reuse-web-dist[\s\S]+name: shared-web-dist/);
  assert.match(site, /name: Build shared application at its canonical root\s+if: '!inputs.reuse-web-dist'/);
  assert.match(web, /name: web-dist/);
  assert.match(site, /name: arcane-site/);
  assert.doesNotMatch(site.split('name: Reuse compiled application from web checks')[1].split('      - name:')[0], /run-id:|github-token:/);
});
