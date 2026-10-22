import json
import os
from pathlib import Path
import time
import urllib.parse
import urllib.request


WORKFLOW_PATH = '.github/workflows/ci.yml'


def validation_title(repository, pull_request):
    return (f'CI validation | {repository} | PR {pull_request["number"]} | '
            f'{pull_request["head"]["sha"]} | {pull_request["base"]["sha"]}')


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
               and run['display_title'] == validation_title(repository, pull_request)
               and any(item['number'] == pull_request['number']
                       for item in run['pull_requests'])]
    if not matches:
        raise ValueError('No exact head/base product validation exists; run product CI')
    newest = max(run['run_number'] for run in matches)
    latest = [run for run in matches if run['run_number'] == newest]
    if len(latest) != 1:
        raise ValueError('Ambiguous product validation')
    return latest[0]


def require_product_success(run, jobs, check):
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


class GitHub:
    def __init__(self, token):
        self.token = token

    def get(self, path):
        request = urllib.request.Request('https://api.github.com' + path, headers={
            'Authorization': f'Bearer {self.token}',
            'Accept': 'application/vnd.github+json',
            'X-GitHub-Api-Version': '2022-11-28',
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
        query = urllib.parse.urlencode({
            'event': 'pull_request', 'head_sha': pull_request['head']['sha'],
        })
        runs = api.pages(f'{prefix}/actions/workflows/ci.yml/runs?{query}', 'workflow_runs')
        run = latest_validation(runs, repository, pull_request, current_run)
        if run['status'] == 'completed':
            if run['conclusion'] != 'success':
                raise ValueError('Latest product validation failed or was cancelled')
            jobs = api.pages(f'{prefix}/actions/runs/{run["id"]}/attempts/{run["run_attempt"]}/jobs', 'jobs')
            gates = [job for job in jobs if job['name'] == 'CI required']
            if len(gates) != 1:
                raise ValueError('Missing or ambiguous product gate')
            check_id = int(gates[0]['check_run_url'].rsplit('/', 1)[1])
            check = api.get(f'{prefix}/check-runs/{check_id}')
            require_product_success(run, jobs, check)
            refreshed = latest_validation(
                api.pages(f'{prefix}/actions/workflows/ci.yml/runs?{query}', 'workflow_runs'),
                repository, pull_request, current_run)
            if (refreshed['id'] != run['id'] or refreshed['run_attempt'] != run['run_attempt']
                    or refreshed['status'] != 'completed' or refreshed['conclusion'] != 'success'):
                raise ValueError('Product validation changed during proof verification; retry this gate')
            if not same_candidate(api.get(f'{prefix}/pulls/{pull_request["number"]}'), pull_request):
                raise ValueError('PR changed during proof verification')
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
