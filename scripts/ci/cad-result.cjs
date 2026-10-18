'use strict';

// Artifacts are immutable. Resolve their successful canonical main run rather
// than trusting PR-scoped actions/cache entries or an artifact name alone.
async function resolve({ github, context, core }, { key, force = false }) {
  if (!/^[a-f0-9]{64}$/.test(key)) throw new Error('Invalid CAD result key');
  if (force) return;
  const repo = { owner: context.repo.owner, repo: context.repo.repo };
  const canonical = `${repo.owner}/${repo.repo}`;
  // Bounded discovery: expiration/old results are safe misses, never acceptance.
  const { data } = await github.rest.actions.listArtifactsForRepo({ ...repo, per_page: 100 });
  const candidates = data.artifacts.filter(a => !a.expired && a.name.startsWith('cad-result-v1-'));
  candidates.sort((a, b) => Number(b.name === `cad-result-v1-${key}`)
    - Number(a.name === `cad-result-v1-${key}`) || b.id - a.id);
  for (const artifact of candidates.slice(0, 20)) {
    const id = artifact.workflow_run?.id;
    if (!id || artifact.workflow_run.head_branch !== 'main') continue;
    const { data: run } = await github.rest.actions.getWorkflowRun({ ...repo, run_id: id });
    if (run.status !== 'completed' || run.conclusion !== 'success'
      || run.head_branch !== 'main' || run.head_repository?.full_name !== canonical
      || !['push', 'workflow_dispatch'].includes(run.event)
      || !['.github/workflows/kufr-cad.yml', '.github/workflows/ci.yml'].includes(run.path)) continue;
    core.setOutput('artifact-id', String(artifact.id));
    core.setOutput('run-id', String(id));
    core.setOutput('exact', String(artifact.name === `cad-result-v1-${key}`));
    core.info(`CAD trusted result: main run ${id}, artifact ${artifact.id}`);
    return;
  }
  core.info('CAD trusted result: no eligible successful main artifact; safe miss');
}

module.exports = { resolve };
