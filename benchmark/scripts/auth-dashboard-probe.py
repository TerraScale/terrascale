"""Three signed-in support-page samples; JSON output contains no credentials or bodies."""
import datetime
import http.cookiejar
import json
import pathlib
import secrets
import stat
import time
import urllib.error
import urllib.request

state_path = pathlib.Path.home() / '.cache/terrascale/dash-qa-session.json'
if stat.S_IMODE(state_path.stat().st_mode) != 0o600:
    raise SystemExit('Shared session file must have mode 0600')
state = json.loads(state_path.read_text())
jar = http.cookiejar.CookieJar()
for item in state['cookies']:
    domain = item['domain']
    if domain.lstrip('.') != 'dash.terrascale.tech':
        continue
    jar.set_cookie(http.cookiejar.Cookie(
        version=0, name=item['name'], value=item['value'], port=None, port_specified=False,
        domain=domain, domain_specified=domain.startswith('.'), domain_initial_dot=domain.startswith('.'),
        path=item['path'], path_specified=True, secure=item['secure'],
        expires=None if item.get('expires', -1) < 0 else int(item['expires']),
        discard=item.get('expires', -1) < 0, comment=None, comment_url=None,
        rest={'HttpOnly': item.get('httpOnly', False)}, rfc2109=False))
class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None
opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar), NoRedirect())
rows = []
for repeat in range(1, 4):
    trace_id = secrets.token_hex(16)
    request = urllib.request.Request('https://dash.terrascale.tech/support', headers={
        'Cache-Control': 'no-cache', 'Accept': 'text/html',
        'traceparent': f'00-{trace_id}-{secrets.token_hex(8)}-01'})
    started = time.perf_counter()
    try:
        response = opener.open(request, timeout=90)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        ttfb_ms = (time.perf_counter() - started) * 1000
        body = response.read()
        rows.append({'repeat': repeat, 'path': '/support', 'status': response.status,
            'ttfbMs': round(ttfb_ms, 1), 'totalMs': round((time.perf_counter() - started) * 1000, 1),
            'bytes': len(body), 'requestId': response.headers.get('X-Request-ID'),
            'clientTraceId': trace_id, 'timing': response.headers.get('Server-Timing')})
    if response.status != 200:
        break
print(json.dumps({'capturedAt': datetime.datetime.now(datetime.timezone.utc).isoformat(),
    'source': 'delta Python stdlib; shared storageState cookie jar; sequential full-body GET; redirects refused',
    'rows': rows}, indent=2))
