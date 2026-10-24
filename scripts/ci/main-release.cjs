'use strict';

const crypto = require('node:crypto');
const fs = require('node:fs');

const REPOSITORY = 'Duloc-Dungeon/DildoVander';
const CONTROLLER = 'main-release.yml';
const UUID = /^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/;
const STAGES = [
  { workflow: 'ci.yml', output: 'ci_run_id' },
  { workflow: 'kufr-ios-sign.yml', output: 'adhoc_run_id' },
  { workflow: 'kufr-ios-app-store.yml', output: 'app_store_run_id' },
];

class Superseded extends Error {}

function positiveId(value) {
  const id = Number(value);
  if (!/^[1-9]\d*$/.test(String(value)) || !Number.isSafeInteger(id)) {
    throw new Error('A positive GitHub run ID is required');
  }
  return id;
}

function releaseRequest(runId) {
  // Stable across reruns so an interrupted controller can recover its children.
  const hex = crypto.createHash('sha256').update(`${REPOSITORY}:${positiveId(runId)}`).digest('hex');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-4${hex.slice(13, 16)}-8${hex.slice(17, 20)}-${hex.slice(20, 32)}`;
}

function requestTitle(request) {
  if (typeof request !== 'string' || !UUID.test(request)) throw new Error('Invalid release request UUID');
  return `Main release | ${request}`;
}

function validateRun(run, workflowId, sha, event, title) {
  if (!run || run.repository?.full_name !== REPOSITORY || run.head_repository?.full_name !== REPOSITORY
      || run.workflow_id !== workflowId || run.head_sha !== sha || run.head_branch !== 'main'
      || run.event !== event || (title && run.display_title !== title)) {
    throw new Error('Release run identity or dispatch provenance does not match');
  }
  positiveId(run.id);
  positiveId(run.run_attempt);
}

async function run({ github, context, core }, options = {}) {
  const now = options.now || Date.now;
  const sleep = options.sleep || (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const deadline = now() + (options.timeoutMs ?? 150 * 60 * 1000);
  const pollMs = options.pollMs ?? 20000;
  const discoveryTimeoutMs = options.discoveryTimeoutMs ?? 3 * 60 * 1000;
  const repo = { owner: 'Duloc-Dungeon', repo: 'DildoVander' };
  if (`${context.repo.owner}/${context.repo.repo}` !== REPOSITORY
      || context.ref !== 'refs/heads/main' || !['push', 'workflow_dispatch'].includes(context.eventName)
      || !/^[a-f0-9]{40}$/.test(context.sha || '')) {
    throw new Error('Main releases require the canonical repository and direct main event');
  }
  const runId = positiveId(context.runId);
  const runAttempt = positiveId(context.runAttempt ?? process.env.GITHUB_RUN_ATTEMPT);
  const request = releaseRequest(runId);
  const title = requestTitle(request);
  const receipt = {
    schema: 1, repository: REPOSITORY, source_sha: context.sha,
    controller_run_id: runId, controller_run_attempt: runAttempt,
    ci_run_id: null, adhoc_run_id: null, app_store_run_id: null, outcome: 'failure',
  };
  const workflowIds = new Map();

  async function workflowId(workflow) {
    if (!workflowIds.has(workflow)) {
      const { data } = await github.rest.actions.getWorkflow({ ...repo, workflow_id: workflow });
      workflowIds.set(workflow, positiveId(data.id));
    }
    return workflowIds.get(workflow);
  }

  async function checkCurrent() {
    if (now() >= deadline) throw new Error('Main release deadline exceeded');
    const { data } = await github.rest.git.getRef({ ...repo, ref: 'heads/main' });
    if (data.object.sha !== context.sha) throw new Superseded('Main changed; newer main release owns promotion');
  }

  async function matchingRuns(workflow, expectedId) {
    // Bound pagination as well as the polling deadline. Never guess between matches.
    const matches = [];
    for (let page = 1; page <= 10; page++) {
      const { data } = await github.rest.actions.listWorkflowRuns({
        ...repo, workflow_id: workflow, event: 'workflow_dispatch', branch: 'main',
        head_sha: context.sha, per_page: 100, page,
      });
      for (const candidate of data.workflow_runs) {
        if (candidate.display_title !== title) continue;
        validateRun(candidate, expectedId, context.sha, 'workflow_dispatch', title);
        matches.push(candidate);
      }
      if (matches.length > 1) throw new Error(`Ambiguous release children for ${workflow}`);
      if (data.workflow_runs.length < 100) return matches[0];
    }
    throw new Error('Release child search exceeds its bounded page limit');
  }

  async function child(stage) {
    const expectedId = await workflowId(stage.workflow);
    await checkCurrent();
    let selected = await matchingRuns(stage.workflow, expectedId);
    if (!selected) {
      await checkCurrent();
      // Dispatch is deliberately not retried: a failed response may have created a run.
      // Rerunning this controller recovers by its stable request before dispatching.
      await github.rest.actions.createWorkflowDispatch({
        ...repo, workflow_id: stage.workflow, ref: 'main', inputs: {
          release_request: request,
          ...(stage.output === 'ci_run_id' ? { project: 'all' } : { build_run_id: String(receipt.ci_run_id) }),
        },
      });
    }
    const discoveryDeadline = Math.min(deadline, now() + discoveryTimeoutMs);
    while (!selected) {
      await checkCurrent();
      selected = await matchingRuns(stage.workflow, expectedId);
      if (selected) break;
      if (now() >= discoveryDeadline) throw new Error(`Timed out correlating ${stage.workflow}`);
      await sleep(Math.min(pollMs, discoveryDeadline - now()));
    }
    receipt[stage.output] = selected.id;
    core.info(`Release child ${stage.workflow}: ${selected.id}`);
    while (true) {
      await checkCurrent();
      // Detect duplicate deliveries even after selecting the first visible run.
      const correlated = await matchingRuns(stage.workflow, expectedId);
      if (!correlated || correlated.id !== selected.id) throw new Error('Release child correlation changed');
      const { data: current } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: selected.id });
      validateRun(current, expectedId, context.sha, 'workflow_dispatch', title);
      if (current.status === 'completed') {
        await checkCurrent();
        if (current.conclusion !== 'success') {
          throw new Error(`Release child ${stage.workflow} completed with ${current.conclusion}`);
        }
        return;
      }
      await sleep(Math.min(pollMs, deadline - now()));
    }
  }

  try {
    const { data: ownRun } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: runId });
    validateRun(ownRun, await workflowId(CONTROLLER), context.sha, context.eventName);
    if (ownRun.id !== runId || ownRun.run_attempt !== runAttempt) throw new Error('Controller attempt changed');
    await checkCurrent();
    for (const stage of STAGES) await child(stage);
    await checkCurrent();
    receipt.outcome = 'success';
  } catch (error) {
    if (!(error instanceof Superseded)) throw error;
    receipt.outcome = 'superseded';
    core.info(error.message);
  } finally {
    if (options.receiptPath) fs.writeFileSync(options.receiptPath, `${JSON.stringify(receipt, null, 2)}\n`);
    for (const [name, value] of Object.entries(receipt)) {
      if (value != null) core.setOutput(name, value);
    }
    await core.summary.addHeading('Main release builds').addTable([
      [{ data: 'Outcome', header: true }, { data: 'Source', header: true },
        { data: 'CI / Ad Hoc / App Store', header: true }],
      [receipt.outcome, context.sha,
        [receipt.ci_run_id, receipt.adhoc_run_id, receipt.app_store_run_id].map(id => id || '-').join(' / ')],
    ]).write();
  }
  return receipt;
}

module.exports = { run, releaseRequest, requestTitle };
