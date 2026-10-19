"""Optional personal-Project adapter using a separate project-only credential."""
import os
from pathlib import Path
import re
import shutil
import subprocess


class ProjectError(RuntimeError):
    pass


def configured_project(client_factory):
    number = os.environ.get('DILDOVANDER_COORDINATION_PROJECT_NUMBER')
    if not number:
        return None
    if not number.isdecimal() or int(number) < 1:
        raise ProjectError('Project number must be a positive integer')
    owner = os.environ.get('DILDOVANDER_COORDINATION_PROJECT_OWNER', 'Blato58')
    if not re.fullmatch(r'[A-Za-z0-9-]+', owner):
        raise ProjectError('Invalid Project owner')
    token = os.environ.get('DILDOVANDER_COORDINATION_PROJECT_TOKEN')
    token_file = os.environ.get('DILDOVANDER_COORDINATION_PROJECT_TOKEN_FILE')
    if token and token_file:
        raise ProjectError('Configure one Project credential source')
    if token_file:
        environment = dict(os.environ, DV_PROJECT_FILE=str(Path(token_file).resolve()))
        environment.pop('PSModulePath', None)
        command = r"""$ErrorActionPreference = 'Stop'
$secret = ConvertTo-SecureString ((Get-Content -Raw -LiteralPath $env:DV_PROJECT_FILE).Trim())
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
try { [Console]::Out.Write([Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)) }
finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer); $secret.Dispose() }
"""
        try:
            result = subprocess.run([shutil.which('pwsh') or 'powershell.exe', '-NoProfile',
                                     '-NonInteractive', '-Command', command], env=environment,
                                    capture_output=True, text=True, timeout=20)
        except (OSError, subprocess.TimeoutExpired):
            raise ProjectError('Project credential decryption unavailable') from None
        if result.returncode:
            raise ProjectError('Project credential decryption failed')
        token = result.stdout.strip()
    if not token:
        raise ProjectError('Configured Project requires its separate credential; no owner fallback')
    return ProjectBoard(client_factory(token=token), owner, int(number))


class ProjectBoard:
    def __init__(self, client, owner, number):
        self.client, self.owner, self.number = client, owner, number
        self.project = None

    def graphql(self, query, variables=None):
        response = self.client.request('graphql', 'POST', {'query': query, 'variables': variables or {}})
        if response.get('errors') or not response.get('data'):
            raise ProjectError('Project API failed; inspect Issue and board before retrying a write')
        return response['data']

    def check(self):
        identity = self.client.request('user')
        scopes = {part.strip() for part in self.client.response_headers.get('x-oauth-scopes', '').split(',')}
        if identity.get('login') != self.owner or scopes != {'project'}:
            raise ProjectError('Project credential must match its owner and grant only project scope')
        query = '''query($owner:String!,$number:Int!){user(login:$owner){projectV2(number:$number){
          id public fields(first:50){nodes{... on ProjectV2FieldCommon{id name}
          ... on ProjectV2SingleSelectField{options{id name}}}}}}}'''
        user = self.graphql(query, {'owner': self.owner, 'number': self.number}).get('user')
        project = user.get('projectV2') if user else None
        if not project or project['public']:
            raise ProjectError('Expected an accessible private personal Project')
        self.project = project
        self.fields = {field['name']: field for field in project['fields']['nodes'] if field}
        if not {'Status', 'Agent/session', 'Verification', 'Public PR'} <= self.fields.keys():
            raise ProjectError('Project workflow fields are missing')
        return {'private': True, 'credential': 'project-only', 'configured': True}

    def resolve(self, issue):
        if self.project is None:
            self.check()
        # The Project is dedicated to the tracker. Personal project-only tokens
        # redact Issue content, but retain the native Title field. Ambiguity stops
        # before a write; never guess by list order or keep a local item database.
        query = '''query($id:ID!,$after:String){node(id:$id){... on ProjectV2{
          items(first:100,after:$after){pageInfo{hasNextPage endCursor} nodes{id isArchived
          fieldValueByName(name:"Title"){... on ProjectV2ItemFieldTextValue{text}}}}}}}'''
        matches, cursor = [], None
        for _ in range(100):
            connection = self.graphql(query, {'id': self.project['id'], 'after': cursor})['node']['items']
            matches.extend(item['id'] for item in connection['nodes'] if not item['isArchived']
                           and (item.get('fieldValueByName') or {}).get('text') == issue['title'])
            if not connection['pageInfo']['hasNextPage']:
                break
            cursor = connection['pageInfo']['endCursor']
        else:
            raise ProjectError('Project pagination bound exceeded')
        if len(matches) != 1:
            raise ProjectError('Expected one active Project card with the exact Issue title; check auto-add/duplicates')
        return matches[0]

    def update(self, item_id, status, session=None, verification=None, public_pr=None):
        values = {'Status': status}
        if session is not None:
            values['Agent/session'] = session
        if verification is not None:
            values['Verification'] = verification[:1000]
        if public_pr is not None:
            values['Public PR'] = public_pr
        mutation = '''mutation($project:ID!,$item:ID!,$field:ID!,$value:ProjectV2FieldValue!){
          updateProjectV2ItemFieldValue(input:{projectId:$project,itemId:$item,fieldId:$field,value:$value}){
          projectV2Item{id}}}'''
        for name, value in values.items():
            field = self.fields[name]
            if name == 'Status':
                options = [option['id'] for option in field['options'] if option['name'] == value]
                if len(options) != 1:
                    raise ProjectError('Required Project status option missing')
                payload = {'singleSelectOptionId': options[0]}
            else:
                payload = {'text': value}
            self.graphql(mutation, {'project': self.project['id'], 'item': item_id,
                                   'field': field['id'], 'value': payload})
        query = '''query($id:ID!){node(id:$id){... on ProjectV2Item{fieldValues(first:50){nodes{
          ... on ProjectV2ItemFieldSingleSelectValue{name field{... on ProjectV2FieldCommon{name}}}
          ... on ProjectV2ItemFieldTextValue{text field{... on ProjectV2FieldCommon{name}}}}}}}}'''
        node = self.graphql(query, {'id': item_id}).get('node')
        if not node:
            raise ProjectError('Project card disappeared; inspect the recorded Issue event')
        nodes = node['fieldValues']['nodes']
        actual = {value['field']['name']: value.get('name', value.get('text'))
                  for value in nodes if value.get('field')}
        if any(actual.get(name) != value for name, value in values.items()):
            raise ProjectError('Project readback differs; Issue event remains recorded, inspect before retry')
        return {'status': status, 'verified': True}
