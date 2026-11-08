#!/usr/bin/env python3
"""Provision the regional status canaries in the internal organization.

After a production reset the organization, project, databases and keys are
gone. This command recreates all of them: it discovers serving regions,
creates (or reuses) the internal organization's ts-status project, one
home-region canary database per region, the canary table and its fixture row
(TerraBase is SQL-only), and one database-narrowed key per region. It installs
only the two ts-status Worker secrets. It does not deploy or restart a
service, change orchestration, or touch status history in D1.

Sign-in uses the owner's password (set once below); step-up proofs approve
with that password, so no email codes are consumed while provisioning. An
approving admin still signs in once in a browser when the account has no
password yet.
"""
import fcntl
import http.cookiejar
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

AUTH = 'https://auth.terrascale.tech'
API = 'https://api.terrascale.tech'
STATUS = 'https://status.terrascale.tech'
# TerraScale's own organization: its internal plan keeps the monitor's writes off the Free allowance.
ORGANIZATION = 'terrascale'
SERVICE_PROJECTS = ('ts-dashboard', 'ts-auth', 'ts-router', 'ts-status', 'ts-orchestrator', 'ku')
PROJECT = 'ts-status'
CLI_CLIENT = '00000000000000000000000007'  # ts-auth's registered public ts-cli client.
STATE = Path.home() / '.local/state/ts-status/canaries.json'
# The Data API fixture row; src/probe/canary.ts reads the same CANARY_ROW.
ROW = '00000000000000000000000007'
SCOPES = ['sql:read', 'sql:write']
# The canary table: the Data API canary reads its fixture row, the write probe inserts and deletes rows.
TABLE = 'CREATE TABLE IF NOT EXISTS "canary" (id TEXT PRIMARY KEY, canary TEXT NOT NULL)'
CLOUDFLARE_ACCOUNT = '6cd3f3639e928077593d9b470765efeb'
WORKER = 'ts-status'
DASHBOARD_ORIGIN = 'https://dash.terrascale.tech'
ULID = '[0-7][0-9A-HJKMNP-TV-Z]{25}'
MAIL_COOLDOWN = 60


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


def private(args, payload=None):
    result = subprocess.run(args, input=payload, capture_output=True, timeout=90)
    if result.returncode:
        raise RuntimeError('Private command failed: ' + ' '.join(args[:4]))
    return result.stdout


class Setup:
    def __init__(self):
        self.stage = 'initialization'
        STATE.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
        self.lock = os.open(STATE.with_suffix('.lock'), os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
        fcntl.flock(self.lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if STATE.exists() and (STATE.is_symlink() or STATE.stat().st_mode & 0o777 != 0o600 or STATE.stat().st_uid != os.getuid()):
            raise RuntimeError('Private state must be owned by this user with mode 0600')
        self.state = json.loads(STATE.read_text()) if STATE.exists() else {}
        self.cookies = STATE.with_suffix('.cookies')
        self.jar = http.cookiejar.LWPCookieJar(str(self.cookies))
        if self.cookies.exists():
            if self.cookies.is_symlink() or self.cookies.stat().st_mode & 0o777 != 0o600:
                raise RuntimeError('Private cookie jar must be 0600')
            self.jar.load(ignore_discard=True)
        self.http = urllib.request.build_opener(NoRedirect)
        self.web = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar), NoRedirect)
        self.retry_after = None

    def save(self):
        self.state.pop('cookies', None)
        temporary = STATE.with_suffix('.tmp')
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
        with os.fdopen(fd, 'w') as stream:
            os.fchmod(stream.fileno(), 0o600)
            json.dump(self.state, stream)
        temporary.replace(STATE)
        self.jar.save(ignore_discard=True)
        os.chmod(self.cookies, 0o600)

    def call(self, url, method='GET', data=None, token=None, form=False, statuses=(200,), region=None, headers=None):
        h = {'User-Agent': 'ts-status-canary-recovery/2.0', **(headers or {})}
        if token:
            h['Authorization'] = 'Bearer ' + token
        if region:
            h['X-TerraScale-Region-Pin'] = region
        body = None
        if data is not None:
            h['Content-Type'] = 'application/x-www-form-urlencoded' if form else 'application/json'
            body = (urllib.parse.urlencode(data, doseq=True) if form else json.dumps(data)).encode()
            h['Origin'] = urllib.parse.urlsplit(url).scheme + '://' + urllib.parse.urlsplit(url).netloc
        original = urllib.parse.urlsplit(url).netloc
        authenticated = token is not None or any(k.lower() == 'authorization' for k in h)
        for _ in range(12):
            parsed = urllib.parse.urlsplit(url)
            if parsed.scheme != 'https' or parsed.username or parsed.password or parsed.netloc not in {
                    urllib.parse.urlsplit(x).netloc for x in (AUTH, API, STATUS)}:
                raise RuntimeError('Unadvertised or insecure redirect refused')
            if authenticated and parsed.netloc != original:
                raise RuntimeError('Authenticated cross-origin redirect refused')
            try:
                response = self.http.open(urllib.request.Request(url, body, h, method=method), timeout=30)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                self.last_status = response.status
                self.last_operation = method + ' ' + parsed.path
                content = response.read(1_048_577)
                if response.status in (302, 303) and method == 'GET':
                    url = urllib.parse.urljoin(url, response.headers['Location'])
                    continue
                if response.status == 401 and 401 in statuses:
                    return None
                if response.status not in statuses or len(content) > 1_048_576:
                    try:
                        code = json.loads(content).get('error', {}).get('code')
                        self.last_error_code = code if code in {'remote_region_unavailable', 'unknown_project', 'forbidden_scope', 'temporarily_unavailable', 'insufficient_scope', 'upstream_unavailable', 'not_found', 'internal_error', 'invalid_client', 'invalid_grant', 'rate_limited', 'name_taken', 'database_limit', 'step_up_required'} else None
                    except (ValueError, AttributeError):
                        self.last_error_code = None
                    raise RuntimeError('HTTP ' + str(response.status) + ' at ' + urllib.parse.urlsplit(url).path)
                if region and url.startswith(API) and response.headers.get('X-TerraBase-Region') != region:
                    raise RuntimeError('Serving region mismatch')
                if response.status in (302, 303):
                    return response.headers['Location']
                return json.loads(content) if response.headers.get_content_type() == 'application/json' else content
        raise RuntimeError('Redirect limit exceeded')

    def form(self, path, data=None):
        """A same-origin hosted-UI form post through the persisted session."""
        body = urllib.parse.urlencode(data).encode() if data else None
        h = {'Origin': AUTH, 'User-Agent': 'ts-status-canary-recovery/2.0'}
        if body:
            h['Content-Type'] = 'application/x-www-form-urlencoded'
        for _ in range(12):
            request = urllib.request.Request(AUTH + path, body, h, method='POST' if data else 'GET')
            try:
                response = self.web.open(request, timeout=30)
            except urllib.error.HTTPError as error:
                response = error
            with response:
                self.retry_after = response.headers.get('Retry-After')
                content = response.read(1_048_577)
                if response.status in (302, 303):
                    return response.status, response.headers.get('Location')
                if len(content) > 1_048_576:
                    raise RuntimeError('Oversized hosted page')
                return response.status, content.decode(errors='replace')
        raise RuntimeError('Redirect limit exceeded')

    def discover(self):
        self.stage = 'public region discovery'
        regions = self.call(API + '/v1/regions')['regions']
        if not regions or len(set(regions)) != len(regions) or not all(re.fullmatch('[a-z]{3}', r) for r in regions):
            raise RuntimeError('Invalid discovery')
        for region in regions:
            ready = self.call(API + '/readyz', region=region)
            if ready['region'] != region or ready['status'] != 'ready':
                raise RuntimeError('Readiness mismatch')
        return regions

    def set_password(self, address):
        """One emailed reset code sets the password every later step reuses."""
        self.stage = 'one-time password bootstrap'
        if self.state.get('password'):
            return
        password = secrets.token_urlsafe(24)
        status, _ = self.form('/ui/terrascale/reset')
        if status != 200:
            raise RuntimeError('Reset page refused')
        from mailcode import latest_code
        from datetime import datetime, timedelta, timezone
        for attempt in range(12):
            started = datetime.now(timezone.utc)
            status, _ = self.form('/ui/terrascale/reset', {'email': address, 'next': '/ui/terrascale/sign-in'})
            if status == 200:
                break
            time.sleep(min(int(self.retry_after or MAIL_COOLDOWN), 120) + 2)
        if status != 200:
            raise RuntimeError('Reset code refused')
        found = latest_code(address, since=started - timedelta(seconds=30))
        if not found:
            raise RuntimeError('No reset code arrived')
        status, _ = self.form('/ui/terrascale/reset/complete', {'email': address, 'code': found[0], 'password': password})
        if status != 200:
            raise RuntimeError('Password refused')
        self.state['password'] = password
        self.save()
        print(json.dumps({'password_set': True, 'email': address}), flush=True)

    def sign_in(self, address):
        """Password sign-in; the session cookie stays in the private jar."""
        self.stage = 'password sign-in'
        status, _ = self.form('/ui/terrascale/account')
        if status == 200:
            return
        status, _ = self.form('/ui/terrascale/sign-in/password', {
            'email': address, 'password': self.state['password'], 'next': '/ui/terrascale/account'})
        if status not in (200, 302):
            raise RuntimeError('Password sign-in refused')
        self.save()

    def owner_grant(self):
        """A ts-cli device grant the signed-in session approves (no email)."""
        self.stage = 'session-approved device grant'
        device = self.call(AUTH + '/terrascale/oauth/device', 'POST', {'client_id': CLI_CLIENT, 'scope': 'openid email'}, form=True)
        code = urllib.parse.quote(device['user_code'])
        status, _ = self.form('/ui/terrascale/device?user_code=' + code)
        if status != 200:
            raise RuntimeError('Device page refused')
        status, _ = self.form('/ui/terrascale/device', {'user_code': device['user_code'], 'decision': 'approve'})
        if status != 200:
            raise RuntimeError('Device approval refused')
        interval = max(5, int(device.get('interval', 5)))
        deadline = time.time() + device['expires_in']
        while time.time() < deadline:
            time.sleep(interval)
            answer = self.call(AUTH + '/terrascale/oauth/token', 'POST',
                               {'grant_type': 'urn:ietf:params:oauth:grant-type:device_code',
                                'client_id': CLI_CLIENT, 'device_code': device['device_code']},
                               form=True, statuses=(200, 400, 503))
            if isinstance(answer, dict):
                if 'access_token' in answer:
                    return answer
                error = answer.get('error')
                if error == 'slow_down':
                    interval += 5
                elif error not in ('authorization_pending', 'temporarily_unavailable'):
                    raise RuntimeError('Device approval ended: ' + str(error)[:40])
        raise RuntimeError('Device approval expired')

    def internal_project(self, grant):
        """The internal organization's ts-status project, recreated after a wipe."""
        self.stage = 'internal organization discovery'
        def listing(url):
            items, after = [], None
            while True:
                page = self.call(url + ('?after=' + urllib.parse.quote(after) if after else ''), token=grant['access_token'])
                items.extend(page['items'])
                after = page['next']
                if not after:
                    return items
        organizations = [o for o in listing(AUTH + '/v1/terrascale/organizations')
                         if o['name'] == ORGANIZATION and o['role'] in ('owner', 'admin')]
        if len(organizations) > 1:
            raise RuntimeError('The approving account must administer exactly one internal organization')
        if not organizations:
            organization = self.call(AUTH + '/v1/terrascale/organizations', 'POST',
                                     {'name': ORGANIZATION}, token=grant['access_token'], statuses=(201,))['id']
            print(json.dumps({'organization_created': organization}), flush=True)
        else:
            organization = organizations[0]['id']
        projects = [p for p in listing(AUTH + '/v1/terrascale/organizations/' + organization + '/projects')
                    if p['name'] in SERVICE_PROJECTS]
        names = {p['name'] for p in projects}
        for name in SERVICE_PROJECTS:
            if name not in names and name != PROJECT:
                self.call(AUTH + '/v1/terrascale/organizations/' + organization + '/projects', 'POST',
                          {'name': name}, token=grant['access_token'], statuses=(201,))
                print(json.dumps({'project_created': name}), flush=True)
        ts_status = [p for p in projects if p['name'] == PROJECT] or self.call(
            AUTH + '/v1/terrascale/organizations/' + organization + '/projects', 'POST',
            {'name': PROJECT}, token=grant['access_token'], statuses=(201,))
        project = ts_status[0]['id'] if isinstance(ts_status, list) else ts_status['id']
        return organization, project

    def project_token(self, grant, organization, project):
        return self.call(AUTH + '/terrascale/oauth/token', 'POST',
                {'grant_type': 'urn:ietf:params:oauth:grant-type:token-exchange', 'client_id': CLI_CLIENT,
                 'subject_token': grant['access_token'], 'subject_token_type': 'urn:ietf:params:oauth:token-type:access_token',
                 'organization': organization, 'project': project,
                 'scope': 'databases:read databases:write sql:read sql:write'}, form=True)['access_token']

    def create_api_key(self, grant, organization, project, database, name):
        """One password-approved step-up proof per database-narrowed key."""
        self.stage = 'database-scoped canary credential'
        proof_body = self.call(AUTH + '/v1/terrascale/step-up', 'POST', {
            'purpose': 'create_api_key', 'audience': 'ts-auth',
            'return_uri': DASHBOARD_ORIGIN + '/auth/step-up/callback'}, token=grant['access_token'], statuses=(201,))
        proof = proof_body['proof']
        path = '/ui/terrascale/step-up/' + proof_body['step_up_url'].rsplit('/', 1)[-1]
        status, _ = self.form(path)
        if status != 200:
            raise RuntimeError('Step-up page refused')
        status, page = self.form(path, {'password': self.state['password']})
        if status != 200 or 'Verification failed' in page:
            raise RuntimeError('Step-up refused')
        root = AUTH + '/v1/terrascale/organizations/' + organization + '/projects/' + project + '/api-keys'
        return self.call(root, 'POST', {'name': name, 'database': database, 'scopes': SCOPES},
                         token=grant['access_token'], statuses=(201,),
                         headers={'X-TerraScale-Step-Up-Proof': proof})

    def provision(self, regions):
        address = self.state['email']
        self.set_password(address)
        self.sign_in(address)
        grant = self.owner_grant()
        organization, project = self.internal_project(grant)
        self.state.update(project=project, organization=organization)
        self.save()
        self.stage = 'regional canary databases'
        token = self.project_token(grant, organization, project)
        base = API + '/v1/projects/' + project + '/databases'
        databases = {}
        for region in regions:
            name = 'status-canary-' + region
            page = self.call(base, token=token, region=regions[0], statuses=(200, 404))
            found = next((d for d in (page.get('items', []) if isinstance(page, dict) else []) if d['name'] == name), None)
            if found is None:
                # The first database enrolls a recreated organization into TerraBase.
                found = self.call(base, 'POST', {'name': name, 'homeRegion': region}, token=token,
                                  region=region, statuses=(201,))
            databases[region] = found
            print(json.dumps({'database': name, 'id': found['id'], 'home_region': found.get('homeRegion')}), flush=True)
            token = self.project_token(grant, organization, project)
            self.call(base + '/' + name + '/sql/execute', 'POST', {'statements': [{'sql': TABLE}]},
                      token=token, region=region)
        root = AUTH + '/v1/terrascale/organizations/' + organization + '/projects/' + project + '/api-keys'
        metadata = self.call(root, token=grant['access_token'])['items']
        saved = self.state.get('canaries', {})
        configurations = {}
        for region, database in databases.items():
            reuse = saved.get(region)
            if reuse and any(k['id'] == reuse[:26] and k['database'] == database['id'] and k['project_id'] == project
                             and sorted(k['scopes']) == SCOPES for k in metadata):
                key = reuse
            else:
                issued = self.create_api_key(grant, organization, project, database['id'], 'status-canary-' + region)
                key = issued['key']
                if issued['api_key']['project_id'] != project or issued['api_key']['database'] != database['id']:
                    raise RuntimeError('Credential restrictions mismatch')
            saved[region] = key
            self.state['canaries'] = saved
            self.state['databases'] = {r: d['id'] for r, d in databases.items()}
            self.save()
            configurations[region] = dict(origin=API, issuer=AUTH + '/terrascale', project=project,
                                          database='status-canary-' + region, key=key, region=region)
        for config in configurations.values():
            self.verify(config)
        return configurations

    def verify(self, config):
        self.stage = config['region'] + ' pinned CRUD'
        token = self.call(config['issuer'] + '/oauth/token', 'POST',
                         {'grant_type': 'client_credentials', 'client_id': config['key'][:26],
                          'client_secret': config['key'], 'project': config['project'], 'scope': ' '.join(SCOPES)}, form=True)['access_token']
        url = API + '/v1/projects/' + config['project'] + '/databases/' + config['database'] + '/sql/execute'
        def sql(text, args):
            return self.call(url, 'POST', {'statements': [{'sql': text, 'args': args}]}, token=token,
                             region=config['region'])['results'][0]
        sql('INSERT OR REPLACE INTO "canary" (id, canary) VALUES (?, ?)', [ROW, 'Persistent status read canary'])
        if sql('SELECT id FROM "canary" WHERE id = ?', [ROW])['rows'] != [[ROW]]:
            raise RuntimeError('Fixture missing')
        # A canonical ULID using random bits; independent rows for concurrent executions.
        alphabet = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'
        number = secrets.randbits(128)
        row = ''.join(alphabet[(number >> (5 * i)) & 31] for i in reversed(range(26)))
        marker = secrets.token_hex(16)
        try:
            written = sql('INSERT INTO "canary" (id, canary) VALUES (?, ?)', [row, marker])
            read = sql('SELECT canary FROM "canary" WHERE id = ?', [row])
            queried = sql('SELECT count(*) FROM "canary" WHERE id = ? AND canary = ?', [row, marker])
            if written['rows_affected'] != 1 or read['rows'] != [[marker]] or queried['rows'] != [[1]]:
                raise RuntimeError('CRUD content mismatch')
        finally:
            if sql('DELETE FROM "canary" WHERE id = ?', [row])['rows_affected'] != 1:
                raise RuntimeError('Delete acknowledgement mismatch')
        if sql('SELECT id FROM "canary" WHERE id = ?', [row])['rows'] != []:
            raise RuntimeError('Delete not visible')
        print(json.dumps({'region': config['region'], 'project': config['project'], 'database': config['database'],
                          'key_id': config['key'][:26], 'proof': ['write', 'read', 'query', 'delete', 'read-after-delete']}), flush=True)


def secret_payload(configurations, catalog):
    maps = {'DATA_API_CANARY': {}, 'TERRABASE_PROBES': {}}
    for region in catalog['regions']:
        for cluster in region['clusters']:
            component = cluster['componentName']
            if component == 'Data API' and cluster['cluster'] == 'canary':
                name = 'DATA_API_CANARY'
            elif component == 'SQL API':
                name = 'TERRABASE_PROBES'
            else:
                continue
            code = cluster['regionCode'].lower()
            endpoint = cluster['id']
            if not re.fullmatch(ULID, endpoint):
                raise RuntimeError('Invalid live endpoint ID')
            if code in configurations:
                maps[name][endpoint] = configurations[code]
    if {c['region'] for c in maps['TERRABASE_PROBES'].values()} != set(configurations):
        raise RuntimeError('Live status catalog lacks a discovered SQL API region; coordinate catalog update')
    if not maps['DATA_API_CANARY']:
        raise RuntimeError('Live status catalog lacks the retained Data API canary')
    return maps


def cloudflare_token():
    """The dev Cloudflare token: environment first, else ts-dev-secret stdin."""
    token = os.environ.get('CLOUDFLARE_API_TOKEN')
    if token:
        return token
    return private(['ts-dev-secret', 'get', 'dev-cloudflare-api-token']).decode().strip()


def install(maps, token):
    for name, values in maps.items():
        body = json.dumps({'name': name, 'type': 'secret_text', 'text': json.dumps(values)}).encode()
        request = urllib.request.Request(
            'https://api.cloudflare.com/client/v4/accounts/' + CLOUDFLARE_ACCOUNT + '/workers/scripts/' + WORKER + '/secrets',
            body, {'Authorization': 'Bearer ' + token, 'Content-Type': 'application/json'}, method='PUT')
        response = urllib.request.urlopen(request, timeout=60)
        if not json.load(response).get('success'):
            raise RuntimeError('Cloudflare refused secret ' + name)


def main():
    setup = None
    try:
        setup = Setup()
        setup.state.setdefault('email', os.environ.get('TS_STATUS_OWNER') or 'mariogk@terrascale.tech')
        regions = setup.discover()
        print('PLAN: password sign-in; session-approved device grant; one home-region canary database per region in the internal ts-status project; database-narrowed keys; verify pinned CRUD; update only the two ts-status secrets.', flush=True)
        configurations = setup.provision(regions)
        setup.stage = 'Worker secret installation'
        catalog = json.loads(private(['curl', '-sS', STATUS + '/api/v1/regions']))
        install(secret_payload(configurations, catalog), cloudflare_token())
        print(json.dumps({'installed': ['DATA_API_CANARY', 'TERRABASE_PROBES'], 'regions': regions,
                          'organization': setup.state['organization'], 'project': setup.state['project'],
                          'databases': setup.state['databases']}))
        return 0
    except Exception as error:
        # Exception repr, HTTP bodies and subprocess stderr may contain secrets.
        print(json.dumps({'failed_stage': setup.stage if setup else 'private state', 'error_type': type(error).__name__,
                          'last_http_status': getattr(setup, 'last_status', None),
                          'operation': getattr(setup, 'last_operation', None),
                          'error_code': getattr(setup, 'last_error_code', None),
                          'recovery': 'Retain private state; rerun after resolving the cause.'}), file=sys.stderr)
        return 1
    finally:
        if setup is not None:
            try:
                os.close(setup.lock)
            except OSError:
                pass


if __name__ == '__main__':
    sys.exit(main())
