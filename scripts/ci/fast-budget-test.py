#!/usr/bin/env python3
"""Exercise timing boundaries without spending wall-clock time on slow fixtures."""
import contextlib
import importlib.util
import io
from pathlib import Path
import tempfile

spec = importlib.util.spec_from_file_location('budget', Path(__file__).with_name('fast-budget.py'))
budget = importlib.util.module_from_spec(spec)
spec.loader.exec_module(budget)
with tempfile.TemporaryDirectory() as scratch:
    report = Path(scratch) / 'result.trx'
    def check(duration, suite):
        report.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
                          f'<UnitTestResult testName="critical" duration="{duration}" />'
                          '</Results></TestRun>')
        with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
            return budget.check(report, suite)
    assert not check('00:00:10', 120)
    assert check('00:00:10.000001', 1)
    assert check('00:00:01', 120.000001)
    assert check('00:01:00', 1)
    report.write_text('<TestRun/>')
    try:
        budget.durations(report)
    except ValueError:
        pass
    else:
        raise AssertionError('Missing timings must not pass')
print('fast-budget-test: ok (120s suite and 10s test boundaries; missing timing rejected)')
