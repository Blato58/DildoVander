#!/usr/bin/env bash
# Requires repository administration permission; private rulesets require Pro.
set -euo pipefail
cd "$(dirname "$0")/../.."
repo='Duloc-Dungeon/DildoVander'
if [[ "${1:-}" != '--apply' ]]; then
  echo "Target: $repo"
  cat .github/repository-settings.json .github/main-ruleset.json
  echo 'Apply with an administrator identity: bash scripts/ci/apply-repository-settings.sh --apply'
  exit 0
fi
gh api --method PATCH "repos/$repo" --input .github/repository-settings.json \
  --jq '{delete_branch_on_merge,allow_squash_merge,allow_merge_commit,allow_rebase_merge,allow_update_branch}'
# Read errors must stop before attempting a write with incomplete state.
rules=$(gh api --paginate "repos/$repo/rulesets" --jq '.[] | select(.name == "main quality gate") | .id')
mapfile -t rule_ids <<< "$rules"
if [[ -z "$rules" ]]; then
  gh api --method POST "repos/$repo/rulesets" --input .github/main-ruleset.json --jq '{id,name,enforcement}'
elif [[ "${#rule_ids[@]}" -eq 1 ]]; then
  gh api --method PUT "repos/$repo/rulesets/${rule_ids[0]}" --input .github/main-ruleset.json --jq '{id,name,enforcement}'
else
  echo 'Multiple main quality gate rulesets exist; reconcile them manually.' >&2
  exit 1
fi
