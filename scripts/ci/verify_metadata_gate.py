import json
import os
from pathlib import Path
import re
import time
import urllib.parse
import urllib.request

from select_projects import GITHUB_API_VERSION, stack_revision


WORKFLOW_PATH = '.github/workflows/ci.yml'


def validation_title(repository, pull_request):
    stack = pull_request.get('stack')
    if stack:
        return (f'CI stack {stack["number"]} validation | {repository} | PR {pull_request["number"]} | '
                f'{pull_request["head"]["sha"]} | trunk {stack["base"]["sha"]} | '
                f'layer {stack["position"]}/{stack["size"]}')
    return (f'CI validation | {repository} | PR {pull_request["number"]} | '
            f'{pull_request["head"]["sha"]} | {pull_request["base"]["sha"]}')


def candidate_title(repository, pull_request):
    return (f'CI candidate | {repository} | PR {pull_request["number"]} | '
            f'{pull_request["head"]["sha"]} | {pull_request["base"]["sha"]} | project=all')


def same_candidate(actual, expected):
    return (actual['state'] == 'open' and actual['number'] == expected['number']
            and actual['head']['sha'] == expected['head']['sha']
            and actual['base']['sha'] == expected['base']['sha'])


def latest_validation(runs, repository, pull_request, current_run):
    matches = [run for run in runs
               if run['id'] != current_run
               and run['path'] == WORKFLOW_PATH
               and run['event'] == 'pull_request'
               and run['head_sha'] == pull_request['head']['sha']
               and any(item['number'] == pull_request['number']
                       for item in run['pull_requests'])]
    if not matches:
        raise ValueError('No exact head/base product validation exists; run product CI')
    expected_title = validation_title(repository, pull_request)
    metadata_title = expected_title.replace(' validation |', ' metadata |', 1)
    matches = [run for run in matches if run.get('display_title') != metadata_title]
    if not matches:
        raise ValueError('No exact head/base product validation exists; run product CI')
    if any(type(run.get('run_number')) is not int or run['run_number'] <= 0 for run in matches):
        raise ValueError('Invalid product validation sequence')
    newest = max(run['run_number'] for run in matches)
    latest = [run for run in matches if run['run_number'] == newest]
    if len(latest) != 1:
        raise ValueError('Ambiguous product validation')
    if latest[0].get('display_title') != expected_title:
        raise ValueError('Newest product validation has stale or invalid native context')
    return latest[0]


def latest_candidate_dispatch(runs, repository, pull_request, current_run):
    if pull_request.get('stack'):
        raise ValueError('No trusted complete-candidate dispatch exists for native stacks')
    matches = [run for run in runs
               if run['id'] != current_run
               and run['path'] == WORKFLOW_PATH
               and run['event'] == 'workflow_dispatch'
               and run['head_sha'] == pull_request['head']['sha']
               and run['head_branch'] == pull_request['head']['ref']
               and run.get('actor', {}).get('login') == 'github-actions[bot]'
               and run['display_title'] == candidate_title(repository, pull_request)]
    if not matches:
        raise ValueError('No trusted complete-candidate dispatch exists; run product CI')
    newest = max(run['run_number'] for run in matches)
    latest = [run for run in matches if run['run_number'] == newest]
    if len(latest) != 1:
        raise ValueError('Ambiguous candidate dispatch')
    return latest[0]


def latest_proof(runs, repository, pull_request, current_run):
    candidates = []
    for finder in (latest_validation, latest_candidate_dispatch):
        try:
            candidates.append(finder(runs, repository, pull_request, current_run))
        except ValueError as error:
            if not str(error).startswith('No exact') and not str(error).startswith('No trusted'):
                raise
    if not candidates:
        raise ValueError('No exact PR validation or trusted complete-candidate dispatch exists; run product CI')
    newest = max(run['run_number'] for run in candidates)
    latest = [run for run in candidates if run['run_number'] == newest]
    if len(latest) != 1:
        raise ValueError('Ambiguous product validation')
    return latest[0]


def current_stack_revision(api, repository, pull_request):
    expected = pull_request.get('stack')
    if not expected:
        return None
    if not isinstance(expected, dict) or not isinstance(expected.get('base'), dict):
        raise ValueError('PR event native stack metadata is incomplete')
    prefix = f'/repos/{repository}'
    query = urllib.parse.urlencode({'pull_request': pull_request['number']})
    stacks = api.get(f'{prefix}/stacks?{query}')
    if not isinstance(stacks, list) or len(stacks) != 1:
        raise ValueError('Current native stack membership is unavailable or ambiguous')
    stack = stacks[0]
    if not isinstance(stack, dict):
        raise ValueError('Current native stack membership is malformed')
    entries = stack.get('pull_requests')
    base = stack.get('base', {})
    expected_base = expected.get('base', {})
    expected_sha = expected_base.get('sha')
    position = expected.get('position')
    if (not isinstance(stack.get('id'), int) or stack.get('number') != expected.get('number')
            or not isinstance(entries, list) or len(entries) != expected.get('size')
            or not isinstance(position, int) or not 1 <= position <= len(entries)
            or not isinstance(base, dict)
            or base.get('ref') != expected_base.get('ref')):
        raise ValueError('Current native stack identity or trunk differs from the PR event')
    if (not isinstance(base.get('ref'), str) or not isinstance(expected_sha, str)
            or not re.fullmatch(r'[0-9a-f]{40}', expected_sha)
            or any(not isinstance(entry, dict) for entry in entries)
            or any(not isinstance(entry.get('head'), dict) for entry in entries[:position])):
        raise ValueError('Current native stack trunk or layer metadata is incomplete')
    numbers = [entry.get('number') for entry in entries]
    if (len(set(numbers)) != len(numbers) or numbers[position - 1] != pull_request['number']
            or not all(isinstance(entry.get('number'), int)
                       and isinstance(entry.get('head', {}).get('ref'), str)
                       and re.fullmatch(r'[0-9a-f]{40}', entry.get('head', {}).get('sha', ''))
                       for entry in entries[:position])):
        raise ValueError('Current native stack ordered candidate metadata is incomplete')
    current = entries[position - 1]
    if (current.get('head', {}).get('sha') != pull_request['head']['sha']
            or current.get('head', {}).get('ref') != pull_request['head']['ref']):
        raise ValueError('Current native stack candidate differs from the PR event')
    branch = urllib.parse.quote(base['ref'], safe='')
    trunk = api.get(f'{prefix}/branches/{branch}')
    if trunk.get('commit', {}).get('sha') != expected_sha:
        raise ValueError('Current native stack trunk changed during metadata proof verification')
    return stack_revision(repository, stack, position, base['ref'], expected_sha)


def require_product_success(run, jobs, check, expected_stack_revision=None):
    if run['status'] != 'completed' or run['conclusion'] != 'success':
        raise ValueError('Product validation did not succeed')
    for name in ['Secret scan', 'Select changed projects', 'CI required']:
        matching = [job for job in jobs if job['name'] == name]
        if len(matching) != 1 or matching[0]['conclusion'] != 'success':
            raise ValueError(f'Product validation requires one successful {name}')
    gate = next(job for job in jobs if job['name'] == 'CI required')
    if (check['id'] != int(gate['check_run_url'].rsplit('/', 1)[1])
            or check['app']['id'] != 15368 or check['app']['slug'] != 'github-actions'
            or check['name'] != 'CI required' or check['conclusion'] != 'success'
            or check['head_sha'] != run['head_sha']):
        raise ValueError('Product gate is not the successful Actions check for this head')
    if expected_stack_revision is not None:
        proof_name = f'Stack proof {expected_stack_revision}'
        proofs = [job for job in jobs if job['name'] == proof_name]
        if len(proofs) != 1 or proofs[0]['conclusion'] != 'success':
            raise ValueError('Product validation does not cover current ordered native stack membership')


class GitHub:
    def __init__(self, token):
        self.token = token

    def get(self, path):
        request = urllib.request.Request('https://api.github.com' + path, headers={
            'Authorization': f'Bearer {self.token}',
            'Accept': 'application/vnd.github+json',
            'X-GitHub-Api-Version': GITHUB_API_VERSION,
        })
        with urllib.request.urlopen(request, timeout=30) as response:
            return json.load(response)

    def pages(self, path, key):
        separator = '&' if '?' in path else '?'
        items = []
        for page in range(1, 11):
            batch = self.get(f'{path}{separator}per_page=100&page={page}')[key]
            items.extend(batch)
            if len(batch) < 100:
                return items
        raise ValueError('API evidence exceeds the bounded pagination budget')


def verify(api, repository, pull_request, current_run, wait_seconds=1200,
           monotonic=time.monotonic, sleep=time.sleep):
    prefix = f'/repos/{repository}'
    deadline = monotonic() + wait_seconds
    while True:
        if not same_candidate(api.get(f'{prefix}/pulls/{pull_request["number"]}'), pull_request):
            raise ValueError('PR head/base changed; fresh validation is required')
        expected_stack_revision = current_stack_revision(api, repository, pull_request)
        query = urllib.parse.urlencode({'head_sha': pull_request['head']['sha']})
        runs = api.pages(f'{prefix}/actions/workflows/ci.yml/runs?{query}', 'workflow_runs')
        run = latest_proof(runs, repository, pull_request, current_run)
        if run['status'] == 'completed':
            if run['conclusion'] != 'success':
                raise ValueError('Latest product validation failed or was cancelled')
            jobs = api.pages(f'{prefix}/actions/runs/{run["id"]}/attempts/{run["run_attempt"]}/jobs', 'jobs')
            gates = [job for job in jobs if job['name'] == 'CI required']
            if len(gates) != 1:
                raise ValueError('Missing or ambiguous product gate')
            check_id = int(gates[0]['check_run_url'].rsplit('/', 1)[1])
            check = api.get(f'{prefix}/check-runs/{check_id}')
            require_product_success(run, jobs, check, expected_stack_revision)
            refreshed = latest_proof(
                api.pages(f'{prefix}/actions/workflows/ci.yml/runs?{query}', 'workflow_runs'),
                repository, pull_request, current_run)
            if (refreshed['id'] != run['id'] or refreshed['run_attempt'] != run['run_attempt']
                    or refreshed['status'] != 'completed' or refreshed['conclusion'] != 'success'):
                raise ValueError('Product validation changed during proof verification; retry this gate')
            if not same_candidate(api.get(f'{prefix}/pulls/{pull_request["number"]}'), pull_request):
                raise ValueError('PR changed during proof verification')
            if current_stack_revision(api, repository, pull_request) != expected_stack_revision:
                raise ValueError('Native stack membership changed during proof verification')
            print(f'Exact head/base product validation passed in run {run["id"]}, attempt {run["run_attempt"]}')
            return
        if monotonic() >= deadline:
            raise ValueError('Product validation is still pending; retry this gate after it completes')
        sleep(min(20, max(0, deadline - monotonic())))


def main():
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text(encoding='utf-8'))
    if (os.environ['GITHUB_EVENT_NAME'] != 'pull_request' or event['action'] != 'edited'
            or 'base' in event.get('changes', {})):
        raise ValueError('Proof reuse is only allowed for metadata-only PR edits')
    verify(GitHub(os.environ['GITHUB_TOKEN']), os.environ['GITHUB_REPOSITORY'],
           event['pull_request'], int(os.environ['GITHUB_RUN_ID']))


if __name__ == '__main__':
    main()
