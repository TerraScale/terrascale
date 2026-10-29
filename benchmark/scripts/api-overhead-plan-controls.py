"""Parallel offline controls for the exact on-host diagnostic function; no SSH or credentials."""
import ast
from concurrent.futures import ThreadPoolExecutor
import importlib.util
import json
from pathlib import Path
from threading import Barrier
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location('plan_helper', Path(__file__).with_name('api-overhead-plan.py'))
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
function = next(node for node in ast.parse(helper.REMOTE).body if isinstance(node, ast.FunctionDef) and node.name == 'refusal')
namespace = {'json': json}
exec(compile(ast.Module(body=[function], type_ignores=[]), '<on-host-diagnostic>', 'exec'), namespace)
secret = 'sensitive-control-marker'
cases = [
    ('success', 0, ('{"id":"' + secret + '"}\n200').encode(), None),
    ('transport', 7, secret.encode(), 'transport (curl_exit 7)'),
    ('authentication', 0, ('{"error":{"code":"invalid_key","message":"' + secret + '"}}\n401').encode(), 'HTTP 401 (invalid_key)'),
    ('storage', 0, ('{"error":{"code":"storage_busy","message":"' + secret + '"}}\n503').encode(), 'HTTP 503 (storage_busy)'),
    ('unknown-code', 0, ('{"error":{"code":"' + secret + '"}}\n503').encode(), 'HTTP 503 (other)'),
    ('malformed-body', 0, (secret + '\n503').encode(), 'HTTP 503 (other)'),
    ('malformed-status', 0, secret.encode(), 'HTTP unclassified (other)'),
    ('string-code', 0, b'{"error":"not_found"}\n404', 'HTTP 404 (not_found)'),
    ('invalid-status-range', 0, b'{}\n999', 'HTTP unclassified (other)'),
    ('invalid-error-shape', 0, b'{"error":[]}\n503', 'HTTP 503 (other)'),
]
barrier = Barrier(len(cases))


def control(case):
    name, exit_code, body, expected = case
    barrier.wait(timeout=10)
    actual = namespace['refusal'](SimpleNamespace(returncode=exit_code, stdout=body, stderr=secret.encode()))
    if actual != expected or secret in str(actual):
        raise SystemExit('Diagnostic control failed: ' + name)
    return name


with ThreadPoolExecutor(max_workers=len(cases)) as pool:
    completed = list(pool.map(control, cases))
print('Plan diagnostics: ' + str(len(completed)) + ' parallel controls passed; no network calls.')
