// Called by pinned actions/github-script in a separate verification job, before deployment secrets.
const fs = require('node:fs');

async function resolve({ github, context, core }, { runId, workflow, artifact }) {
  if (!/^[1-9]\d*$/.test(String(runId)) || !Number.isSafeInteger(Number(runId))) {
    throw new Error('A positive build run ID is required');
  }
  const repo = context.repo;
  const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: Number(runId) });
  const { data: expected } = await github.rest.actions.getWorkflow({ ...repo, workflow_id: workflow });
  if (run.repository.full_name !== `${repo.owner}/${repo.repo}` || run.workflow_id !== expected.id
      || run.status !== 'completed' || run.conclusion !== 'success'
      || !['pull_request', 'workflow_dispatch'].includes(run.event)) {
    throw new Error('Only a successful build from the expected repository and workflow can be promoted');
  }
  const artifacts = await github.paginate(github.rest.actions.listWorkflowRunArtifacts, {
    ...repo, run_id: run.id, per_page: 100,
  });
  const matches = artifacts.filter(item => item.name === artifact && !item.expired);
  if (matches.length !== 1) throw new Error('Build artifact is missing, expired or ambiguous; run CI again');
  core.setOutput('artifact_id', matches[0].id);
  core.setOutput('run_id', run.id);
  core.setOutput('run_attempt', run.run_attempt);
}

async function verify({ github, context, core }, { manifestPath, runId, workflow, signingKind }) {
  const manifest = JSON.parse(fs.readFileSync(manifestPath, 'utf8'));
  const repo = context.repo;
  const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: Number(runId) });
  const { data: expected } = await github.rest.actions.getWorkflow({ ...repo, workflow_id: workflow });
  if (run.repository.full_name !== `${repo.owner}/${repo.repo}`
      || run.workflow_id !== expected.id || run.status !== 'completed' || run.conclusion !== 'success'
      || manifest.schema !== 1 || manifest.repository !== `${repo.owner}/${repo.repo}`
      || manifest.run_id !== run.id || !Number.isSafeInteger(manifest.run_attempt) || manifest.run_attempt < 1 || manifest.run_attempt > run.run_attempt
      || manifest.event !== run.event || manifest.head_sha !== run.head_sha
      || !/^[a-f0-9]{40}$/.test(manifest.source_sha)
      || !/^[a-f0-9]{40}$/.test(manifest.source_tree)
      || !/^[a-f0-9]{64}$/.test(manifest.payload_sha256)) {
    throw new Error('Build provenance does not match the successful workflow run');
  }
  const dispatchedPr = run.event === 'workflow_dispatch' && manifest.pr_number != null;
  if (run.event === 'pull_request' || dispatchedPr) {
    if (!Number.isSafeInteger(manifest.pr_number) || manifest.pr_number < 1) throw new Error('Missing build PR');
    if (dispatchedPr && (manifest.source_sha !== run.head_sha
        || !/^[a-f0-9]{40}$/.test(manifest.base_sha)
        || typeof run.head_branch !== 'string' || !run.head_branch)) {
      throw new Error('PR dispatch provenance must identify its exact head and base');
    }
    const { data: pr } = await github.rest.pulls.get({ ...repo, pull_number: manifest.pr_number });
    if (!pr.merged || pr.base.ref !== 'main' || pr.base.repo.full_name !== manifest.repository
        || pr.head.repo?.full_name !== manifest.repository || pr.head.sha !== run.head_sha
        || (dispatchedPr && pr.head.ref !== run.head_branch)) {
      throw new Error('The build PR must be merged into main from this repository');
    }
  } else if (run.event !== 'workflow_dispatch' || run.head_branch !== 'main' || manifest.source_sha !== run.head_sha) {
    throw new Error('Recovery/signing builds must come from main');
  }
  const { data: source } = await github.rest.git.getCommit({ ...repo, commit_sha: manifest.source_sha });
  if (source.tree.sha !== manifest.source_tree) throw new Error('Source tree mismatch');
  if (run.event === 'pull_request' && manifest.source_sha !== run.head_sha
      && (source.parents.length !== 2 || source.parents[0].sha !== manifest.base_sha
          || source.parents[1].sha !== run.head_sha)) {
    throw new Error('Source commit is not the merge commit tested for this PR');
  }
  const { data: mainRef } = await github.rest.git.getRef({ ...repo, ref: 'heads/main' });
  const { data: main } = await github.rest.git.getCommit({ ...repo, commit_sha: mainRef.object.sha });
  if (context.sha && main.sha !== context.sha) throw new Error('Main changed after this workflow started; dispatch again');
  if (main.tree.sha !== manifest.source_tree) {
    throw new Error('The build tree differs from current main; update/rebuild the PR or run CI on main');
  }
  if (signingKind && manifest.signing_kind !== signingKind) throw new Error('IPA signing kind mismatch');
  core.setOutput('source_sha', manifest.source_sha);
  core.setOutput('main_sha', main.sha);
  core.setOutput('signing_kind', manifest.signing_kind || '');
  await core.summary.addHeading('Verified build promotion').addTable([
    [{data: 'Build run', header: true}, {data: 'Source', header: true}, {data: 'Current main', header: true}],
    [String(run.id), manifest.source_sha, main.sha],
  ]).write();
}

async function checkMain({ github, context }, expectedSha) {
  const { data } = await github.rest.git.getRef({ ...context.repo, ref: 'heads/main' });
  if (data.object.sha !== expectedSha) throw new Error('Main changed while this deployment was queued; select a current build');
}
module.exports = { resolve, verify, checkMain };
