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
  return String(new Function('github', 'inputs', 'format', `return ${expression}`)(
    github, inputs, (template, ...values) => template.replace(/\{(\d+)\}/g, (_, index) => values[Number(index)]),
  ));
}

test('main ruleset requires the GitHub Actions aggregate and preserves pull request protections', () => {
  const ruleset = JSON.parse(fs.readFileSync(path.join(workflows, '../main-ruleset.json'), 'utf8'));
  assert.equal(ruleset.enforcement, 'active');
  assert.deepEqual(ruleset.conditions.ref_name, { include: ['refs/heads/main'], exclude: [] });
  assert.deepEqual(ruleset.bypass_actors, []);
  const rules = new Map(ruleset.rules.map(rule => [rule.type, rule]));
  assert.ok(rules.has('deletion'));
  assert.ok(rules.has('non_fast_forward'));
  assert.equal(rules.get('pull_request').parameters.required_approving_review_count, 0);
  assert.equal(rules.get('pull_request').parameters.required_review_thread_resolution, true);
  assert.deepEqual(rules.get('required_status_checks').parameters, {
    required_status_checks: [{ context: 'CI required', integration_id: 15368 }],
    strict_required_status_checks_policy: true,
  });
  const aggregateName = job('required').match(/^    name: (.+)$/m)[1];
  assert.equal(aggregateName, rules.get('required_status_checks').parameters.required_status_checks[0].context);
});

test('metadata edits reuse exact candidate proof without superseding product validation', () => {
  const validation = { event: { action: 'synchronize', changes: {}, pull_request: { number: 42, stack: {} } }, run_id: 1 };
  const metadata = { event: { action: 'edited', changes: {}, pull_request: { number: 42, stack: {} } }, run_id: 2 };
  const baseChange = { event: { action: 'edited', changes: { base: {} }, pull_request: { number: 42, stack: {} } }, run_id: 3 };
  for (const name of ['secret-scan', 'changes']) {
    assert.equal(condition(name, metadata), false, name);
    assert.equal(condition(name, validation), true, name);
    assert.equal(condition(name, baseChange), true, name);
  }
  assert.notEqual(concurrencyGroup(metadata), concurrencyGroup(validation));
  assert.equal(concurrencyGroup(baseChange), concurrencyGroup(validation));
  assert.equal(condition('required', metadata), true);
  assert.match(job('required'), /^    name: CI required$/m);
  assert.match(job('required'), /run: python3 scripts\/ci\/verify_metadata_gate.py/);
  assert.match(job('required'), /if: github.event.action != 'edited' \|\| github.event.changes.base != null/);
  assert.match(ci, /CI candidate \| \{0\} \| PR \{1\} \| \{2\} \| \{3\} \| project=\{4\}/);
  assert.match(ci, /CI stack \{0\} \{1\} \| \{2\} \| PR \{3\} \| \{4\} \| trunk \{5\} \| layer \{6\}\/\{7\}/);
  assert.match(ci, /--base "\$CI_BASE_SHA" --head "\$CI_HEAD_SHA" \\\s+--stack-pr/);
  assert.match(ci, /CI_BASE_SHA: \$\{\{ github\.event\.pull_request\.stack\.base\.sha \|\|/);
  assert.match(ci, /github\.event\.action == 'edited' && !github\.event\.changes\.base && 'metadata' \|\| 'validation'/);
  assert.match(ci, /pull_request:\s+types: \[opened, reopened, synchronize, edited, ready_for_review\]\s+branches: \[main\]/);
  assert.match(ci, /cancel-in-progress:.*github.event_name == 'pull_request'/);
  assert.match(ci, /merge_group:\s+types: \[checks_requested\]/);
});

test('native sibling validations cannot cancel each other or replace an ordinary PR run', () => {
  const expression = ci.match(/^  cancel-in-progress: \$\{\{ (.+) \}\}$/m)[1];
  const cancels = (github, inputs = { pr_number: '' }) => new Function('github', 'inputs', `return ${expression}`)(github, inputs);
  const native = { event_name: 'pull_request', event: { action: 'synchronize', changes: {},
    pull_request: { number: 42, stack: { number: 273 } } }, run_id: 1 };
  const sibling = { ...native, run_id: 2 };
  const ordinary = { ...native, event: { ...native.event, pull_request: { number: 42, stack: {} } } };
  assert.notEqual(concurrencyGroup(native), concurrencyGroup(sibling));
  assert.notEqual(concurrencyGroup(native), concurrencyGroup(ordinary));
  assert.equal(Boolean(cancels(native)), false);
  assert.equal(Boolean(cancels(ordinary)), true);
  assert.equal(concurrencyGroup(ordinary), concurrencyGroup({ ...ordinary, run_id: 2 }));
  const metadata = { ...native, event: { ...native.event, action: 'edited' } };
  assert.notEqual(concurrencyGroup(metadata), concurrencyGroup(native));
  const dispatch = { event_name: 'workflow_dispatch', event: { pull_request: { stack: {} } }, ref: 'feature' };
  assert.equal(concurrencyGroup(dispatch, { pr_number: '42' }), concurrencyGroup(ordinary));
  assert.equal(Boolean(cancels(dispatch, { pr_number: '42' })), true);
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
