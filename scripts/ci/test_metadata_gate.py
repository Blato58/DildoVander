import copy
import unittest

from verify_metadata_gate import latest_validation, require_product_success, validation_title, verify


REPOSITORY = 'example/product'
PULL_REQUEST = {'number': 7, 'state': 'open', 'head': {'sha': 'a' * 40}, 'base': {'sha': 'b' * 40}}
RUN = {
    'id': 10, 'run_number': 20, 'run_attempt': 2, 'path': '.github/workflows/ci.yml',
    'event': 'pull_request', 'head_sha': PULL_REQUEST['head']['sha'],
    'display_title': validation_title(REPOSITORY, PULL_REQUEST),
    'pull_requests': [{'number': 7}], 'status': 'completed', 'conclusion': 'success',
}
JOBS = [{'name': name, 'conclusion': 'success', 'check_run_url': f'https://api.github.com/check-runs/{index}'}
        for index, name in enumerate(['Secret scan', 'Select changed projects', 'CI required'], 1)]
CHECK = {'id': 3, 'app': {'id': 15368, 'slug': 'github-actions'}, 'name': 'CI required',
         'head_sha': RUN['head_sha'], 'conclusion': 'success'}


class FakeGitHub:
    def __init__(self):
        self.pull_request = copy.deepcopy(PULL_REQUEST)
        self.runs = [copy.deepcopy(RUN)]
        self.jobs = copy.deepcopy(JOBS)
        self.check = copy.deepcopy(CHECK)
        self.requests = []

    def get(self, path):
        self.requests.append(path)
        if '/pulls/' in path:
            return self.pull_request
        if '/check-runs/' in path:
            return self.check
        raise AssertionError(path)

    def pages(self, path, key):
        self.requests.append(path)
        if key == 'workflow_runs':
            return copy.deepcopy(self.runs)
        if key == 'jobs':
            return self.jobs
        raise AssertionError(path)


class MetadataGateTests(unittest.TestCase):
    def test_reuses_successful_exact_candidate_and_latest_attempt(self):
        api = FakeGitHub()
        verify(api, REPOSITORY, PULL_REQUEST, 11)
        self.assertTrue(any('/runs/10/attempts/2/jobs' in path for path in api.requests))

    def test_wrong_pr_workflow_event_head_base_and_self_cannot_supply_proof(self):
        for field, value in [
            ('id', 11), ('path', '.github/workflows/other.yml'), ('event', 'workflow_dispatch'),
            ('head_sha', 'c' * 40), ('pull_requests', [{'number': 8}]),
            ('display_title', RUN['display_title'].replace('b' * 40, 'c' * 40)),
            ('display_title', RUN['display_title'].replace('validation', 'metadata')),
        ]:
            with self.subTest(field=field, value=value):
                run = {**RUN, field: value}
                with self.assertRaises(ValueError):
                    latest_validation([run], REPOSITORY, PULL_REQUEST, 11)

    def test_missing_or_ambiguous_proof_fails(self):
        for runs in [[], [RUN, {**RUN, 'id': 12}]]:
            with self.assertRaises(ValueError):
                latest_validation(runs, REPOSITORY, PULL_REQUEST, 11)

    def test_latest_failed_or_cancelled_validation_cannot_inherit_older_success(self):
        for conclusion in ['failure', 'cancelled', 'skipped', None]:
            api = FakeGitHub()
            api.runs.append({**RUN, 'id': 12, 'run_number': 21, 'conclusion': conclusion})
            with self.subTest(conclusion=conclusion), self.assertRaises(ValueError):
                verify(api, REPOSITORY, PULL_REQUEST, 11)

    def test_pending_validation_has_bounded_wait_and_does_not_mint_success(self):
        api = FakeGitHub()
        api.runs[0]['status'] = 'in_progress'
        with self.assertRaises(ValueError):
            verify(api, REPOSITORY, PULL_REQUEST, 11, wait_seconds=0)
        waits = []

        def complete(seconds):
            waits.append(seconds)
            api.runs[0]['status'] = 'completed'

        verify(api, REPOSITORY, PULL_REQUEST, 11, sleep=complete)
        self.assertEqual(waits, [20])

    def test_live_head_base_or_state_change_fails(self):
        for field in ['head', 'base', 'state']:
            api = FakeGitHub()
            api.pull_request[field] = 'closed' if field == 'state' else {'sha': 'c' * 40}
            with self.subTest(field=field), self.assertRaises(ValueError):
                verify(api, REPOSITORY, PULL_REQUEST, 11)

    def test_rerun_or_new_validation_during_proof_lookup_fails(self):
        for change in ['attempt', 'new_run', 'failure']:
            api = FakeGitHub()
            original_get = api.get

            def changed_get(path):
                if '/check-runs/' in path:
                    if change == 'attempt':
                        api.runs[0].update(run_attempt=3, status='in_progress', conclusion=None)
                    elif change == 'new_run':
                        api.runs.append({**RUN, 'id': 12, 'run_number': 21, 'status': 'queued'})
                    else:
                        api.runs[0]['conclusion'] = 'failure'
                return original_get(path)

            api.get = changed_get
            with self.subTest(change=change), self.assertRaises(ValueError):
                verify(api, REPOSITORY, PULL_REQUEST, 11)

    def test_missing_skipped_or_duplicate_gate_and_selector_fail(self):
        for name in ['Secret scan', 'Select changed projects', 'CI required']:
            for conclusion in ['failure', 'cancelled', 'skipped', None]:
                jobs = copy.deepcopy(JOBS)
                next(job for job in jobs if job['name'] == name)['conclusion'] = conclusion
                with self.subTest(name=name, conclusion=conclusion), self.assertRaises(ValueError):
                    require_product_success(RUN, jobs, CHECK)
        for jobs in [JOBS[:-1], JOBS + [JOBS[-1]]]:
            with self.assertRaises(ValueError):
                require_product_success(RUN, jobs, CHECK)

    def test_wrong_actions_app_check_identity_or_head_fails(self):
        for field, value in [
            ('id', 9), ('app', {'id': 1, 'slug': 'github-actions'}),
            ('app', {'id': 15368, 'slug': 'other'}), ('name', 'other'),
            ('conclusion', 'skipped'), ('head_sha', 'c' * 40),
        ]:
            with self.subTest(field=field), self.assertRaises(ValueError):
                require_product_success(RUN, JOBS, {**CHECK, field: value})


if __name__ == '__main__':
    unittest.main()
