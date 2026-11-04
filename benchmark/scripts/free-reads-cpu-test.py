import importlib.util
import json
import tempfile
import unittest
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch


def module():
    spec = importlib.util.spec_from_file_location("free_reads_cpu", Path(__file__).with_name("free-reads-cpu.py"))
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


class CpuGuardTests(unittest.TestCase):
    def test_ancestor_quota_limits_four_cpus_to_two(self):
        guard = module()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            process = root / "proc/123"
            process.mkdir(parents=True)
            (process / "cgroup").write_text("0::/parent/leaf\n")
            guard.root = root / "cgroup"
            leaf = guard.root / "parent/leaf"
            leaf.mkdir(parents=True)
            (leaf / "cpu.max").write_text("400000 100000\n")
            (leaf.parent / "cpu.max").write_text("200000 100000\n")
            guard.os = SimpleNamespace(sched_getaffinity=lambda pid: {0, 1, 2, 3})
            self.assertEqual((2.0, 4), guard.capacity(process))

    def test_unreadable_capacity_stops_before_another_sample(self):
        guard = module()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            process = root / "proc/123"
            process.mkdir(parents=True)
            (process / "cgroup").write_text("0::/missing\n")
            fields = ["R"] + ["0"] * 18 + ["1"]
            (process / "stat").write_text("123 (terrabase) " + " ".join(fields))
            guard.root = root / "cgroup"
            output = []
            guard.os = SimpleNamespace(sched_getaffinity=lambda pid: {0, 1, 2, 3})
            guard.time = SimpleNamespace(sleep=lambda delay: self.fail("Guard continued"))
            guard.print = lambda line, **kwargs: output.append(line)
            with patch.object(guard, "process", return_value=(process, 1)):
                guard.main()
            records = [json.loads(line) for line in output]
            self.assertEqual(1, len(records))
            self.assertEqual("FileNotFoundError", records[0]["reason"])
            self.assertIn("error", records[0])


if __name__ == "__main__":
    cases = list(unittest.defaultTestLoader.loadTestsFromTestCase(CpuGuardTests))
    with ThreadPoolExecutor() as executor:
        results = list(executor.map(lambda case: case.run(unittest.TestResult()), cases))
    failures = [error for result in results for _, error in result.errors + result.failures]
    for failure in failures:
        print(failure)
    print(f"CPU guard: {len(cases)} tests, {len(failures)} failures")
    raise SystemExit(bool(failures))
