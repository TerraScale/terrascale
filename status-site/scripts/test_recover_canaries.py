import importlib.util
import io
import json
import tempfile
import unittest
from contextlib import redirect_stdout
from email.message import Message
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

spec = importlib.util.spec_from_file_location('recovery', Path(__file__).with_name('recover-canaries.py'))
r = importlib.util.module_from_spec(spec)
spec.loader.exec_module(r)
ID = '00000000000000000000000008'
CONFIG = dict(region='gru', key=ID + '_' + 'a' * 52, issuer=r.AUTH + '/terrascale', project=ID, database='status-canary-gru')


class RecoveryTests(unittest.TestCase):
    def response(self, status, body=b'', location=None):
        headers = Message()
        headers['Content-Type'] = 'application/json'
        if location:
            headers['Location'] = location
        response = Mock(status=status, headers=headers)
        response.read.return_value = body
        response.__enter__ = Mock(return_value=response)
        response.__exit__ = Mock(return_value=False)
        return response

    def test_401_json_is_unauthenticated_and_redirects_cannot_leak_bearers(self):
        setup = object.__new__(r.Setup)
        setup.http = SimpleNamespace(open=Mock(return_value=self.response(401, b'{"error":"unauthorized"}')))
        self.assertIsNone(setup.call(r.AUTH + '/terrascale/oauth/userinfo', statuses=(200, 401)))
        for target, token in [('http://auth.terrascale.tech/next', None), (r.AUTH + '/next', 'private-token')]:
            setup.http.open = Mock(return_value=self.response(302, location=target))
            with self.assertRaises(RuntimeError):
                setup.call(r.API + '/start', token=token)
            self.assertEqual(setup.http.open.call_count, 1)

    def test_live_mapping_excludes_negative_http_data_probes(self):
        catalog = {'regions': [{'clusters': [
            dict(id=ID, componentName='Data API', cluster='gru', regionCode='GRU'),
            dict(id=ID[:-1] + '9', componentName='Data API', cluster='canary', regionCode='GRU'),
            dict(id=ID[:-1] + 'A', componentName='SQL API', cluster='gru', regionCode='GRU'),
        ]}]}
        payload = r.secret_payload({'gru': CONFIG}, catalog)
        self.assertEqual(list(json.loads(json.dumps(payload['DATA_API_CANARY'])) and payload['DATA_API_CANARY']), [ID[:-1] + '9'])
        self.assertEqual(list(payload['TERRABASE_PROBES']), [ID[:-1] + 'A'])
        with self.assertRaises(RuntimeError):
            r.secret_payload({'gru': CONFIG, 'fra': dict(CONFIG, region='fra')}, catalog)

    def test_recreated_organization_and_missing_service_projects_are_created(self):
        setup = object.__new__(r.Setup)
        created = []
        def call(url, method='GET', data=None, **kwargs):
            if url.endswith('/organizations') and method == 'POST':
                created.append(('org', data))
                return dict(id=ID)
            if url.endswith('/projects') and method == 'POST':
                created.append(('project', data['name']))
                return dict(id=data['name'], name=data['name'])
            if url.endswith('/organizations'):
                return dict(items=[], next=None)
            if url.endswith('/projects'):
                return dict(items=[dict(id='0' * 25 + 'B', name='ts-dashboard')], next=None)
            raise AssertionError(url)
        setup.call = call
        organization, project = setup.internal_project({'access_token': 'grant'})
        self.assertEqual(organization, ID)
        self.assertEqual(project, 'ts-status')
        self.assertIn(('org', {'name': 'terrascale'}), created)
        names = [name for kind, name in created if kind == 'project']
        self.assertEqual(set(names), {'ts-auth', 'ts-router', 'ts-status', 'ts-orchestrator', 'ku'})
        self.assertEqual(names[-1], 'ts-status')

    def test_per_region_home_region_databases_and_narrowed_keys(self):
        database = '0' * 25 + 'D'
        project = '0' * 25 + 'B'
        posted = []
        def call(url, method='GET', data=None, **kwargs):
            if url.endswith('/databases') and method == 'POST':
                posted.append(('database', data['name'], kwargs.get('region')))
                return dict(id=database + posted[-1][1][-1], name=data['name'], homeRegion=data['homeRegion'])
            if url.endswith('/databases'):
                return dict(items=[], next=None)
            if url.endswith('/sql/execute'):
                self.assertEqual(data, {'statements': [{'sql': r.TABLE}]})
                return {'results': [{'rows': [], 'rows_affected': 0}]}
            if url.endswith('/step-up'):
                return dict(proof='private-proof', step_up_url=r.AUTH + '/ui/terrascale/step-up/' + ID)
            if url.endswith('/api-keys') and method == 'GET':
                return dict(items=[], next=None)
            if url.endswith('/api-keys'):
                posted.append(('key', data['database'], data['scopes']))
                return dict(key=ID + '_' + 'b' * 52,
                            api_key=dict(project_id=project, database=data['database'], scopes=r.SCOPES))
            raise AssertionError(url)
        with tempfile.TemporaryDirectory() as directory, patch.object(r, 'STATE', Path(directory) / 'state.json'):
            setup = r.Setup()
            setup.state.update(email='owner@example.com', password='private-password')
            setup.call = call
            setup.form = Mock(return_value=(200, ''))
            setup.internal_project = Mock(return_value=(ID, project))
            setup.owner_grant = Mock(return_value={'access_token': 'grant'})
            setup.project_token = Mock(return_value='token')
            setup.verify = Mock()
            configurations = setup.provision(['gru', 'sjc'])
            r.os.close(setup.lock)
        databases = [entry for entry in posted if entry[0] == 'database']
        self.assertEqual([(name, region) for _, name, region in databases],
                         [('status-canary-gru', 'gru'), ('status-canary-sjc', 'sjc')])
        self.assertEqual(configurations['gru']['database'], 'status-canary-gru')
        self.assertEqual(configurations['sjc']['database'], 'status-canary-sjc')
        keys = [entry for entry in posted if entry[0] == 'key']
        self.assertEqual([scopes for _, _, scopes in keys], [r.SCOPES] * 2)
        self.assertEqual(setup.verify.call_count, 2)

    def test_step_up_proof_never_reaches_printed_output(self):
        setup = object.__new__(r.Setup)
        setup.state = {'password': 'private-password'}
        pages = []
        def call(url, method='GET', data=None, **kwargs):
            if url.endswith('/step-up'):
                return dict(proof='private-proof', step_up_url=r.AUTH + '/ui/terrascale/step-up/' + ID)
            if url.endswith('/api-keys'):
                return dict(key=ID + '_' + 'b' * 52,
                            api_key=dict(project_id=ID, database='0' * 25 + 'D', scopes=r.SCOPES))
            raise AssertionError(url)
        setup.call = call
        setup.form = Mock(return_value=(200, ''))
        with redirect_stdout(io.StringIO()) as output:
            issued = setup.create_api_key({'access_token': 'grant'}, ID, ID, '0' * 25 + 'D', 'status-canary-gru')
        self.assertEqual(issued['key'][:26], ID)
        self.assertNotIn('private-proof', output.getvalue())
        self.assertNotIn('private-password', output.getvalue())
        headers = setup.form.call_args_list[0]
        self.assertNotIn('private-proof', json.dumps(list(headers.args)))

    def test_worker_secret_install_reads_token_from_environment_or_dev_secret(self):
        maps = {'DATA_API_CANARY': {'endpoint': CONFIG}}
        bodies = []
        def handler(request, **kwargs):
            bodies.append(json.loads(request.data.decode()))
            self.assertTrue(request.full_url.startswith(
                'https://api.cloudflare.com/client/v4/accounts/' + r.CLOUDFLARE_ACCOUNT + '/workers/scripts/ts-status/secrets'))
            return self.response(200, b'{"success": true}')
        with patch.object(r.urllib.request, 'urlopen', handler), \
                patch.dict(r.os.environ, {'CLOUDFLARE_API_TOKEN': 'private-token'}):
            r.install(maps, r.cloudflare_token())
        self.assertEqual([body['name'] for body in bodies], ['DATA_API_CANARY'])
        self.assertEqual(json.loads(bodies[0]['text'])['endpoint']['key'], CONFIG['key'])
        with patch.object(r, 'private', return_value=b'private-token\n') as private, \
                patch.object(r.urllib.request, 'urlopen', handler), patch.dict(r.os.environ):
            r.os.environ.pop('CLOUDFLARE_API_TOKEN', None)
            # Compare without assertEqual: a mismatch must never echo a real token.
            self.assertTrue(r.cloudflare_token() == 'private-token', 'token not read from ts-dev-secret')
            self.assertEqual(private.call_args.args[0], ['ts-dev-secret', 'get', 'dev-cloudflare-api-token'])

    def test_failed_query_still_deletes_and_never_prints_key(self):
        setup = object.__new__(r.Setup)
        statements = []
        def call(url, method='GET', data=None, **kwargs):
            if url.endswith('/oauth/token'):
                return {'access_token': 'private-token'}
            text, args = data['statements'][0]['sql'], data['statements'][0]['args']
            statements.append(text.split()[0])
            if 'count(*)' in text:
                raise RuntimeError('query failed')
            if text.startswith(('INSERT', 'DELETE')):
                return {'results': [{'rows': [], 'rows_affected': 1}]}
            return {'results': [{'rows': [[args[0]]] if args[0] == r.ROW else [['marker']], 'rows_affected': 0}]}
        setup.call = call
        with patch.object(r.secrets, 'token_hex', return_value='marker'), redirect_stdout(io.StringIO()) as output:
            with self.assertRaises(RuntimeError):
                setup.verify(CONFIG)
        self.assertEqual(statements[-1], 'DELETE')
        self.assertNotIn(CONFIG['key'], output.getvalue())


if __name__ == '__main__':
    unittest.main()
