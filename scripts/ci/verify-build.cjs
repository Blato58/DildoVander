// Runs from trusted main workflow code before signing or deployment credentials.
const fs = require('node:fs');

function repository(context, sourceRepo) {
  const fullName = sourceRepo || `${context.repo.owner}/${context.repo.repo}`;
  if (!/^[\w.-]+\/[\w.-]+$/.test(fullName)) throw new Error('Invalid source repository');
  const [owner, repo] = fullName.split('/');
  return { owner, repo };
}

async function trustedRun(github, repo, runId, workflow) {
  if (!/^[1-9]\d*$/.test(String(runId)) || !Number.isSafeInteger(Number(runId))) {
    throw new Error('A positive build run ID is required');
  }
  if (!['ci.yml', 'kufr-ios-sign.yml'].includes(workflow)) throw new Error('Unexpected source workflow');
  const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: Number(runId) });
  const { data: expected } = await github.rest.actions.getWorkflow({ ...repo, workflow_id: workflow });
  const name = `${repo.owner}/${repo.repo}`;
  if (run.repository.full_name !== name || run.head_repository?.full_name !== name
      || run.workflow_id !== expected.id || run.status !== 'completed' || run.conclusion !== 'success'
      || run.event !== 'workflow_dispatch' || run.head_branch !== 'main') {
    throw new Error('Only a successful direct main build from the expected repository and workflow can be promoted');
  }
  return run;
}

async function resolve({ github, context, core }, { runId, workflow, artifact, sourceRepo }) {
  const repo = repository(context, sourceRepo);
  const run = await trustedRun(github, repo, runId, workflow);
  const artifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, {
    ...repo, run_id: run.id, per_page: 100,
  });
  const matches = artifacts.filter(item => item.name === artifact && !item.expired);
  if (matches.length !== 1) throw new Error('Build artifact is missing, expired or ambiguous; run CI again');
  if (!Number.isSafeInteger(matches[0].id) || matches[0].id < 1
      || !/^sha256:[a-f0-9]{64}$/.test(matches[0].digest || '')) {
    throw new Error('Immutable artifact ID and SHA-256 digest are required');
  }
  core.setOutput('artifact_id', matches[0].id);
  core.setOutput('artifact_digest', matches[0].digest);
  core.setOutput('run_id', run.id);
  core.setOutput('run_attempt', run.run_attempt);
}

async function verify({ github, context, core }, { manifestPath, runId, workflow, signingKind, sourceRepo }) {
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const repo = repository(context, sourceRepo);
  const run = await trustedRun(github, repo, runId, workflow);
  if (manifest.schema !== 1 || manifest.repository !== `${repo.owner}/${repo.repo}`
      || manifest.run_id !== run.id || !Number.isSafeInteger(manifest.run_attempt)
      || manifest.run_attempt < 1 || manifest.run_attempt > run.run_attempt
      || manifest.event !== 'workflow_dispatch' || manifest.pr_number != null
      || manifest.base_sha != null || manifest.head_sha !== run.head_sha || manifest.source_sha !== run.head_sha
      || !/^[a-f0-9]{40}$/.test(manifest.source_sha) || !/^[a-f0-9]{40}$/.test(manifest.source_tree)
      || !/^[a-f0-9]{64}$/.test(manifest.payload_sha256)) {
    throw new Error('Build provenance does not match a direct trusted main workflow run');
  }
  const { data: source } = await github.rest.git.getCommit({ ...repo, commit_sha: manifest.source_sha });
  if (source.tree.sha !== manifest.source_tree) throw new Error('Source tree mismatch');
  const { data: mainRef } = await github.rest.git.getRef({ ...repo, ref: 'heads/main' });
  const { data: main } = await github.rest.git.getCommit({ ...repo, commit_sha: mainRef.object.sha });
  if (main.sha !== run.head_sha || main.tree.sha !== manifest.source_tree
      || (!sourceRepo && context.sha && main.sha !== context.sha)) {
    throw new Error('Build must match the exact current main commit and tree; run CI again');
  }
  if (signingKind && manifest.signing_kind !== signingKind) throw new Error('IPA signing kind mismatch');
  core.setOutput('source_sha', manifest.source_sha);
  core.setOutput('main_sha', main.sha);
  core.setOutput('signing_kind', manifest.signing_kind || '');
  await core.summary.addHeading('Verified direct main build').addTable([
    [{ data: 'Build run', header: true }, { data: 'Source', header: true }],
    [String(run.id), manifest.source_sha],
  ]).write();
}

async function checkMain({ github, context }, expectedSha, sourceRepo) {
  const { data } = await github.rest.git.getRef({ ...repository(context, sourceRepo), ref: 'heads/main' });
  if (data.object.sha !== expectedSha) throw new Error('Main changed while this operation was queued; select a current build');
}
module.exports = { resolve, verify, checkMain };
