import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('check_main', Path(__file__).with_name('check-main.py'))
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class MainQualificationTests(unittest.TestCase):
    def runs(self):
        return [{'databaseId': 1, 'name': 'CI', 'status': 'completed', 'conclusion': 'failure'},
                {'databaseId': 2, 'name': 'Docs Fast', 'status': 'completed', 'conclusion': 'success'}]

    def test_slow_failures_are_nonblocking_after_successful_test_rollup(self):
        jobs = {1: [{'name': 'test', 'conclusion': 'success'},
                    {'name': 'slow-auth-main', 'conclusion': 'failure'},
                    {'name': 'slow-terrabase-main', 'conclusion': 'failure'}]}
        self.assertEqual(policy.validate(self.runs(), jobs), ['slow-auth-main', 'slow-terrabase-main'])

    def test_failed_or_missing_test_rollup_blocks_deployment(self):
        for jobs in ([], [{'name': 'test', 'conclusion': 'failure'}],
                     [{'name': 'test', 'conclusion': ''}]):
            with self.subTest(jobs=jobs), self.assertRaises(ValueError):
                policy.validate(self.runs(), {1: jobs})

    def test_blocking_ci_failure_blocks_even_with_successful_rollup(self):
        with self.assertRaises(ValueError):
            policy.validate(self.runs(), {1: [{'name': 'test', 'conclusion': 'success'},
                                             {'name': 'libs', 'conclusion': 'failure'}]})

    def test_other_push_workflows_must_finish_successfully(self):
        for status, conclusion in [('completed', 'failure'), ('in_progress', '')]:
            runs = self.runs()
            runs[1].update(status=status, conclusion=conclusion)
            with self.subTest(status=status), self.assertRaises(ValueError):
                policy.validate(runs, {1: [{'name': 'test', 'conclusion': 'success'}]})

    def test_missing_main_ci_blocks_deployment(self):
        with self.assertRaises(ValueError):
            policy.validate(self.runs()[1:], {})


if __name__ == '__main__':
    unittest.main()
