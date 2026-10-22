#!/usr/bin/env bash
set -euo pipefail

# Installed by root outside the checkout. A label alone does not isolate jobs.
trusted_ref='Duloc-Dungeon/DildoVander/.github/workflows/pr-sync.yml@refs/heads/main'
if [[ ${GITHUB_REPOSITORY:-} == Duloc-Dungeon/DildoVander && ${GITHUB_WORKFLOW_REF:-} == "$trusted_ref" ]]; then
  case ${GITHUB_EVENT_NAME:-} in
    pull_request_target|workflow_dispatch) exit 0 ;;
    push) [[ ${GITHUB_REF:-} == refs/heads/main ]] && exit 0 ;;
  esac
fi

# A root-reviewed migration dispatch may use a branch before main is updated.
if [[ ${GITHUB_REPOSITORY:-} == Duloc-Dungeon/DildoVander && ${GITHUB_EVENT_NAME:-} == workflow_dispatch \
      && -r /etc/kufr-control/approved-workflows ]]; then
  while read -r approved_ref approved_sha; do
    if [[ ${GITHUB_WORKFLOW_REF:-} == "$approved_ref" && ${GITHUB_WORKFLOW_SHA:-} == "$approved_sha" ]]; then
      exit 0
    fi
  done < /etc/kufr-control/approved-workflows
fi

echo 'Control runner rejected a workflow outside the trusted controller.' >&2
# Exiting alone permits a hostile step with if: always(). Kill the worker before
# it can evaluate any steps; the runner listener survives to accept the next job.
worker_exe=$(readlink -f "/proc/$PPID/exe")
if [[ $worker_exe != /opt/actions-runner-control/bin/Runner.Worker ]]; then
  echo 'Unexpected hook parent; control runner installation requires repair.' >&2
  exit 1
fi
kill -KILL "$PPID"
exit 1
