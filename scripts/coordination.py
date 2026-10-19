"""Small Issues-only transport. GitHub owns task state; no local task database."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import subprocess
import sys
import time
from coordination_project import ProjectError, configured_project
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import HTTPRedirectHandler, Request, build_opener

REPOSITORY = 'Blato58/DildoVander-tracking'
API = 'https://api.github.com/'
BASE = 'repos/' + REPOSITORY
MARKER = '<!-- dv-coordination:v1 -->'
HUB_TITLE = 'Agent Coordination Hub'
SAVED_CONFIGURATION = ('APP_ID', 'INSTALLATION_ID', 'KEY_FILE',
                       'PROJECT_OWNER', 'PROJECT_NUMBER', 'PROJECT_TOKEN_FILE')


def load_saved_configuration():
    # Read only non-secret per-user configuration; never adopt owner gh tokens.
    if os.name != 'nt':
        return
    import winreg
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, 'Environment') as key:
            for suffix in SAVED_CONFIGURATION:
                name = 'DILDOVANDER_COORDINATION_' + suffix
                if name in os.environ:
                    continue
                try:
                    value, kind = winreg.QueryValueEx(key, name)
                    if kind == winreg.REG_SZ and value:
                        os.environ[name] = value
                except FileNotFoundError:
                    pass
    except FileNotFoundError:
        pass


class CoordinationError(RuntimeError):
    pass


class NoRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):
        raise CoordinationError('GitHub redirect refused; credential was not forwarded')


class Client:
    def __init__(self, token=None, opener=None, sleeper=time.sleep):
        self.token = token or os.environ.get('DILDOVANDER_COORDINATION_TOKEN')
        if not self.token and os.environ.get('DILDOVANDER_COORDINATION_APP_ID'):
            try:
                issued = subprocess.run(['node', str(Path(__file__).with_name('coordination-token.mjs'))],
                                        capture_output=True, text=True, timeout=40,
                                        env=dict(os.environ, DILDOVANDER_ISSUER_PIPE='1'))
                if issued.returncode:
                    raise CoordinationError('Coordination App authentication failed; check isolated credential setup.')
                self.token = json.loads(issued.stdout)['token']
                if not isinstance(self.token, str) or not self.token:
                    raise CoordinationError('Coordination App returned an invalid credential; no fallback.')
            except (OSError, subprocess.TimeoutExpired, ValueError, KeyError):
                raise CoordinationError('Coordination App authentication unavailable; no credential fallback.') from None
        if not self.token:
            raise CoordinationError('Missing DILDOVANDER_COORDINATION_TOKEN. No public/owner credential fallback.')
        self.opener = opener or build_opener(NoRedirects()).open
        self.sleep = sleeper

    def request(self, path, method='GET', data=None, anonymous=False):
        # Fixed API origin; caller cannot redirect a credential to another host.
        if path.startswith(('/', 'http:', 'https:')) or '..' in path.split('/'):
            raise CoordinationError('Invalid API path')
        headers = {'Accept': 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28',
                   'Content-Type': 'application/json', 'User-Agent': 'dildovander-coordination'}
        if not anonymous:
            headers['Authorization'] = 'Bearer ' + self.token
        request = Request(API + path, method=method, data=(
            json.dumps(data).encode() if data is not None else None), headers=headers)
        for attempt in range(3):
            try:
                with self.opener(request, timeout=20) as response:
                    self.response_headers = {key.lower(): value for key, value in
                                             getattr(response, 'headers', {}).items()}
                    raw = response.read()
                    return json.loads(raw) if raw else None
            except HTTPError as error:
                # Never retry writes: an ambiguous POST may already have succeeded.
                retryable = error.code in (429, 502, 503, 504) or (
                    error.code == 403 and error.headers.get('Retry-After') is not None)
                error.close()
                if method == 'GET' and retryable and attempt < 2:
                    try:
                        delay = float(error.headers.get('Retry-After', '2'))
                    except ValueError:
                        delay = 2
                    if 0 <= delay <= 10:
                        self.sleep(delay)
                        continue
                raise CoordinationError(f'GitHub HTTP {error.code}; access/rate-limit failure. '
                                        'No fallback. Re-discover before retrying a write.') from None
            except (URLError, TimeoutError, OSError):
                raise CoordinationError('GitHub connection failed. A write may have succeeded; '
                                        're-discover before retrying. No fallback.') from None
        raise CoordinationError('Read retry limit exceeded')

    def pages(self, path):
        rows = []
        for page in range(1, 51):
            chunk = self.request(path + ('&' if '?' in path else '?') +
                                 urlencode({'per_page': 100, 'page': page}))
            if not isinstance(chunk, list):
                raise CoordinationError('Unexpected paginated response')
            rows.extend(chunk)
            if len(chunk) < 100:
                return rows
        raise CoordinationError('Pagination bound exceeded; narrow discovery')

    def check(self):
        # Installation tokens expose their selected repositories. Owner OAuth/PAT
        # credentials do not pass this check and are never adopted from gh.
        installation = self.request('installation/repositories?per_page=100')
        repositories = installation.get('repositories', [])
        if installation.get('total_count') != 1 or len(repositories) != 1 or (
                repositories[0].get('full_name') != REPOSITORY):
            raise CoordinationError('Coordination installation must select exactly the private tracker')
        if repositories[0].get('private') is not True:
            raise CoordinationError('Tracker must be private')
        # These non-mutating probes reject a token with production or source access.
        # No workflow is dispatched and no privilege test writes production data.
        for path in [BASE + '/contents', BASE + '/actions/secrets', BASE + '/actions/runners',
                     'repos/Blato58/DildoVander-deploy', BASE + '/environments']:
            try:
                self.request(path)
            except CoordinationError as error:
                if 'HTTP 403;' in str(error) or 'HTTP 404;' in str(error):
                    continue
                raise
            raise CoordinationError('Coordination credential has forbidden access: ' + path)
        return {'repository': REPOSITORY, 'credential': 'installation', 'readOnlyDenialProbes': 'passed',
                'writePrivilegeDenials': 'not tested by this read-only command'}

    def issues(self, state='open'):
        return [x for x in self.pages(BASE + '/issues?state=' + state) if 'pull_request' not in x]

    def comments(self, issue):
        return self.pages(BASE + f'/issues/{issue}/comments')

    def comment(self, issue, body):
        return self.request(BASE + f'/issues/{issue}/comments', 'POST', {'body': body})


def events(comments):
    result = []
    for comment in comments:
        body = comment.get('body') or ''
        if body.startswith(MARKER + '\n'):
            try:
                event = json.loads(body.split('\n', 1)[1])
                if event['kind'] not in ('CLAIM', 'HANDOFF', 'RELEASE', 'DONE'):
                    raise ValueError()
                if not isinstance(event['session'], str) or not event['session']:
                    raise ValueError()
                if event['kind'] == 'CLAIM':
                    if not all(isinstance(event[k], str) and event[k] for k in ('branch', 'worktree')):
                        raise ValueError()
                    if not isinstance(event['scopes'], list) or not event['scopes']:
                        raise ValueError()
                    event['scopes'] = sorted(set(normalize_scope(x) for x in event['scopes']))
                result.append(event)
            except (ValueError, KeyError, TypeError, AttributeError):
                raise CoordinationError('Malformed coordination event; inspect task before editing') from None
    return result


def active_claims(comments):
    active = {}
    for event in events(comments):
        session = event['session']
        if event['kind'] == 'CLAIM':
            active[session] = event
        else:
            active.pop(session, None)
    return list(active.values())


def normalize_scope(value):
    value = value.replace('\\', '/').rstrip('/')
    path = PurePosixPath(value)
    if not value or path.is_absolute() or '..' in path.parts or ':' in value or '*' in value:
        raise CoordinationError('Scope must be a literal repository-relative path (or . for all files)')
    return path.as_posix()


def overlaps(left, right):
    return left == '.' or right == '.' or left == right or (
        left.startswith(right + '/') or right.startswith(left + '/'))


def discover(client, query=''):
    issues = client.issues()
    hubs = [x for x in issues if x['title'] == HUB_TITLE]
    if len(hubs) != 1:
        raise CoordinationError('Expected exactly one open coordination hub')
    # Read claims for all open tasks so query filtering cannot conceal conflicts.
    tasks = []
    for issue in issues:
        comments = client.comments(issue['number'])
        claims = active_claims(comments)
        if issue['number'] != hubs[0]['number']:
            tasks.append({'number': issue['number'], 'title': issue['title'],
                          'url': issue['html_url'], 'claims': claims,
                          'labels': [x['name'] for x in issue['labels']]})
        else:
            hub_comments = comments[-8:]
    return {'hub': hubs[0]['html_url'], 'hubMessages': [x['body'] for x in hub_comments],
            'tasks': [x for x in tasks if not query or query.casefold() in x['title'].casefold()],
            'activeClaims': [{'issue': x['number'], **claim} for x in tasks for claim in x['claims']]}


def claim(client, issue, session, branch, worktree, scopes):
    scopes = sorted(set(normalize_scope(x) for x in scopes))
    snapshot = discover(client)
    if issue not in [x['number'] for x in snapshot['tasks']]:
        raise CoordinationError('Claim target must be an open task, not the hub')
    existing = None
    for other in snapshot['activeClaims']:
        if other['session'] == session and other['issue'] == issue:
            if other['scopes'] == scopes and other['branch'] == branch and other['worktree'] == worktree:
                existing = other
                continue
            raise CoordinationError('Existing session claim differs; hand off/release explicitly')
        if other['issue'] == issue or any(overlaps(a, b) for a in scopes for b in other['scopes']):
            raise CoordinationError('Overlapping claim; coordinate before editing')
    if existing:
        return {'result': 'existing advisory claim', 'event': existing}
    event = {'kind': 'CLAIM', 'session': session, 'branch': branch, 'worktree': worktree,
             'scopes': scopes, 'utc': datetime.now(timezone.utc).isoformat()}
    client.comment(issue, MARKER + '\n' + json.dumps(event))
    # This is collision detection, not an atomic lock or exclusive lease.
    after = discover(client)
    conflicts = [x for x in after['activeClaims'] if x['session'] != session and (
        x['issue'] == issue or any(overlaps(a, b) for a in scopes for b in x['scopes']))]
    if conflicts:
        raise CoordinationError('Concurrent claim detected. Claim remains visible; stop and resolve explicitly.')
    return {'result': 'advisory claim recorded; re-read before editing; no exclusive lock', 'event': event}


def ensure(client, key, title, body):
    marker = '<!-- dv-task:' + hashlib.sha256(key.encode()).hexdigest() + ' -->'
    matches = [x for x in client.issues('all') if marker in (x['body'] or '')]
    if len(matches) > 1:
        raise CoordinationError('Duplicate creation keys; reconcile existing tasks before proceeding')
    if matches:
        return {'number': matches[0]['number'], 'url': matches[0]['html_url'], 'created': False}
    created = client.request(BASE + '/issues', 'POST', {'title': title, 'body': marker + '\n\n' + body})
    # GitHub Issues has no idempotency key. Concurrent first creation requires
    # reconciliation; never present a successful POST as uniqueness proof.
    matches = [x for x in client.issues('all') if marker in (x['body'] or '')]
    if len(matches) != 1:
        raise CoordinationError('Creation uniqueness unconfirmed; inspect existing tasks, do not retry POST')
    return {'number': created['number'], 'url': created['html_url'], 'created': True}


def complete(client, issue, session, pull_request, commit, evidence, acceptance_verified):
    if not acceptance_verified or not evidence.strip():
        raise CoordinationError('Explicit verified acceptance and actual evidence are required')
    snapshot = discover(client)
    if issue not in [x['number'] for x in snapshot['tasks']]:
        raise CoordinationError('Completion target must be an open task')
    claims = [x for x in snapshot['activeClaims'] if x['issue'] == issue]
    if len(claims) != 1 or claims[0]['session'] != session:
        raise CoordinationError('Completion requires this session to be the sole active task claimant')
    pr = client.request(f'repos/Blato58/DildoVander/pulls/{pull_request}', anonymous=True)
    if (pr.get('merged') is not True or pr.get('merge_commit_sha') != commit
            or len(commit) != 40 or any(x not in '0123456789abcdef' for x in commit)
            or pr.get('base', {}).get('repo', {}).get('full_name') != 'Blato58/DildoVander'):
        raise CoordinationError('Public merge and exact commit are not verified; task remains open')
    client.comment(issue, 'Acceptance verified by session ' + session + '\n\nPublic implementation: ' +
                   f'https://github.com/Blato58/DildoVander/pull/{pull_request}\nMerge: `{commit}`\n\n' + evidence)
    client.request(BASE + f'/issues/{issue}', 'PATCH', {'state': 'closed', 'state_reason': 'completed'})
    if client.request(BASE + f'/issues/{issue}')['state'] != 'closed':
        raise CoordinationError('Issue closure readback failed')
    return {'issue': issue, 'state': 'closed', 'merge': commit,
            'project': 'Native Done automation requires separate configured/verified Project access'}


def main():
    load_saved_configuration()
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest='command', required=True)
    commands.add_parser('doctor')
    discovery = commands.add_parser('discover')
    discovery.add_argument('--query', default='')
    show = commands.add_parser('show')
    show.add_argument('issue', type=int)
    creation = commands.add_parser('ensure')
    for name in ['key', 'title', 'body-file']:
        creation.add_argument('--' + name, required=True)
    claiming = commands.add_parser('claim')
    claiming.add_argument('issue', type=int)
    for name in ['session', 'branch', 'worktree']:
        claiming.add_argument('--' + name, required=True)
    claiming.add_argument('--scope', action='append', required=True)
    note = commands.add_parser('note')
    note.add_argument('issue', type=int)
    note.add_argument('--body-file', required=True)
    release = commands.add_parser('release')
    release.add_argument('issue', type=int)
    release.add_argument('--session', required=True)
    release.add_argument('--kind', choices=['HANDOFF', 'RELEASE', 'DONE'], required=True)
    release.add_argument('--body-file', required=True)
    completion = commands.add_parser('complete')
    completion.add_argument('issue', type=int)
    completion.add_argument('--session', required=True)
    completion.add_argument('--pull-request', type=int, required=True)
    completion.add_argument('--commit', required=True)
    completion.add_argument('--body-file', required=True)
    completion.add_argument('--acceptance-verified', action='store_true')
    status = commands.add_parser('status')
    status.add_argument('issue', type=int)
    status.add_argument('--session', required=True)
    status.add_argument('--status', choices=['In Progress', 'Blocked', 'In Review', 'Ready'], required=True)
    status.add_argument('--body-file', required=True)
    args = parser.parse_args()
    client = Client()
    checks = client.check()
    project = configured_project(Client) if args.command in ('doctor', 'claim', 'release', 'complete', 'status') else None
    if project:
        checks['project'] = project.check()
    item = None
    if project and args.command in ('claim', 'release', 'complete', 'status'):
        target = client.request(BASE + f'/issues/{args.issue}')
        if target['title'] == HUB_TITLE or target['state'] != 'open':
            raise CoordinationError('Project transition requires an open task, not the hub')
        item = project.resolve(target)
    if args.command == 'doctor':
        result = checks
    elif args.command == 'discover':
        result = discover(client, args.query)
    elif args.command == 'show':
        issue = client.request(BASE + f'/issues/{args.issue}')
        result = {'number': issue['number'], 'title': issue['title'], 'body': issue['body'],
                  'state': issue['state'], 'comments': [x['body'] for x in client.comments(args.issue)[-8:]]}
    elif args.command == 'ensure':
        result = ensure(client, args.key, args.title, Path(args.body_file).read_text(encoding='utf-8'))
    elif args.command == 'claim':
        result = claim(client, args.issue, args.session, args.branch, args.worktree, args.scope)
    elif args.command == 'note':
        result = client.comment(args.issue, Path(args.body_file).read_text(encoding='utf-8'))
        result = {'url': result['html_url']}
    elif args.command == 'status':
        if not project:
            raise CoordinationError('Status transitions require the configured Project')
        claims = [claim for claim in discover(client)['activeClaims'] if claim['issue'] == args.issue]
        if len(claims) != 1 or claims[0]['session'] != args.session:
            raise CoordinationError('Status change requires this session as sole task claimant')
        evidence = Path(args.body_file).read_text(encoding='utf-8')
        client.comment(args.issue, args.status + '\n\n' + evidence)
        result = project.update(item, args.status, args.session, evidence)
    elif args.command == 'complete':
        result = complete(client, args.issue, args.session, args.pull_request, args.commit,
                          Path(args.body_file).read_text(encoding='utf-8'), args.acceptance_verified)
    else:
        claims = [claim for claim in discover(client)['activeClaims'] if claim['issue'] == args.issue]
        if len([claim for claim in claims if claim['session'] == args.session]) != 1:
            raise CoordinationError('Release requires this session to have an active claim')
        event = {'kind': args.kind, 'session': args.session,
                 'utc': datetime.now(timezone.utc).isoformat(),
                 'message': Path(args.body_file).read_text(encoding='utf-8')}
        result = client.comment(args.issue, MARKER + '\n' + json.dumps(event))
        result = {'url': result['html_url']}
    if project and item and args.command in ('claim', 'release', 'complete'):
        if args.command == 'claim':
            result['project'] = project.update(item, 'In Progress', args.session)
        elif args.command == 'complete':
            result['project'] = project.update(item, 'Done', '',
                Path(args.body_file).read_text(encoding='utf-8'),
                f'https://github.com/Blato58/DildoVander/pull/{args.pull_request}')
        else:
            next_status = 'Blocked' if len(claims) > 1 else ('In Review' if args.kind == 'DONE' else 'Ready')
            result['project'] = project.update(item, next_status, '',
                                               Path(args.body_file).read_text(encoding='utf-8'))
    print(json.dumps(result, ensure_ascii=True, indent=2))


if __name__ == '__main__':
    try:
        main()
    except (CoordinationError, ProjectError, OSError, ValueError) as error:
        print('Coordination failed: ' + str(error), file=sys.stderr)
        raise SystemExit(2)
