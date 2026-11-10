#!/usr/bin/env python3
"""Exercise missed-push recovery and refusal paths against GitHub API fixtures."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('release_gate', Path(__file__).with_name('release-gate.py'))
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
HEAD = 'a' * 40
LIVE = 'b' * 40


class RecoveryTests(unittest.TestCase):
    def state(self, runs, jobs):
        def github(*args):
            if args[:2] == ('run', 'list'):
                self.assertNotIn('--event', args)
                self.assertIn('main', args)
                self.assertIn(HEAD, args)
                return runs
            self.assertIn('/jobs?filter=latest&per_page=100', args[-1])
            return [{'jobs': jobs}]
        with patch.object(gate, 'gh', side_effect=github):
            return gate.ci(HEAD)

    def run_fixture(self, event='workflow_dispatch', conclusion='success', status='completed'):
        return {'databaseId': 42, 'event': event, 'status': status, 'conclusion': conclusion}

    def test_dispatch_without_push_qualifies_only_after_test_passes(self):
        run = self.run_fixture()
        self.assertEqual('green', self.state([run], [
            {'name': name, 'conclusion': 'success'} for name in ('test', 'main', 'main-auth-ordered')]))
        self.assertEqual('failed', self.state([run], [{'name': 'test', 'conclusion': 'skipped'}]))
        self.assertEqual('pending', self.state([self.run_fixture(status='in_progress')], []))
        # Branch/tag runs can pass Fast but skip the main-ref-only service jobs.
        self.assertEqual('failed', self.state([run], [{'name': 'test', 'conclusion': 'success'}]))
        for skipped in ('main', 'main-auth-ordered'):
            self.assertEqual('failed', self.state([run], [
                {'name': name, 'conclusion': 'skipped' if name == skipped else 'success'}
                for name in ('test', 'main', 'main-auth-ordered')]))

    def test_maintenance_and_untrusted_events_cannot_qualify(self):
        self.assertEqual('pending', self.state([self.run_fixture()], [
            {'name': 'native-maintenance-unqualified', 'conclusion': 'skipped'}]))
        self.assertEqual('pending', self.state([self.run_fixture(event='pull_request')], []))

    def test_failure_is_not_hidden_by_successful_dispatch(self):
        runs = [self.run_fixture(event='push', conclusion='failure'), self.run_fixture()]
        self.assertEqual('failed', self.state(runs, [{'name': 'test', 'conclusion': 'success'}]))
        self.assertEqual('failed', self.state([self.run_fixture(event='push')], []))
        # A push without core changes skips test but must still have succeeded overall. GitHub then reports
        # the job under its unevaluated name expression.
        skipped = [{'name': "github.event_name == 'workflow_dispatch' && inputs.native_artifacts"
                            " && 'native-maintenance-unqualified' || 'test'", 'conclusion': 'skipped'}]
        self.assertEqual('green', self.state([self.run_fixture(event='push')], skipped))
        self.assertEqual('failed', self.state([self.run_fixture(event='push', conclusion='failure')], skipped))

    def test_heal_dispatches_images_only_for_qualified_green_head_without_a_push(self):
        with (patch.object(gate, 'target', return_value=((HEAD, 'green'), LIVE)),
              patch.object(gate, 'newer_than', return_value=True),
              patch.object(gate, 'gh', return_value=[]),
              patch.object(gate, 'main_head', return_value=HEAD),
              patch.object(gate.subprocess, 'check_call') as command):
            gate.heal()
            command.assert_called_once_with(['gh', 'workflow', 'run', 'images-ghcr.yml',
                                             '--repo', gate.REPO, '--ref', 'main'])
        for found, head in [((HEAD, 'green'), LIVE), ((HEAD, 'pending'), HEAD), (None, HEAD)]:
            with (patch.object(gate, 'target', return_value=(found, LIVE)),
                  patch.object(gate, 'newer_than', return_value=True),
                  patch.object(gate, 'gh', return_value=[]),
                  patch.object(gate, 'main_head', return_value=head),
                  patch.object(gate.subprocess, 'check_call') as command):
                gate.heal()
                command.assert_not_called()

    def test_heal_reruns_dispatch_but_never_a_workflow_run_token(self):
        runs = [{'databaseId': 42, 'event': 'workflow_dispatch', 'status': 'completed', 'attempt': 1},
                {'databaseId': 43, 'event': 'workflow_run', 'status': 'completed', 'attempt': 1}]
        with (patch.object(gate, 'target', return_value=((HEAD, 'green'), LIVE)),
              patch.object(gate, 'newer_than', return_value=True),
              patch.object(gate, 'gh', return_value=runs),
              patch.object(gate.subprocess, 'check_call') as command):
            gate.heal()
            command.assert_called_once_with(['gh', 'run', 'rerun', '42', '--repo', gate.REPO])

    def test_orphaned_older_qualification_does_not_block_a_signed_candidate(self):
        orphan, candidate = 'c' * 40, 'd' * 40
        commits = [{'sha': sha} for sha in (HEAD, orphan, candidate, LIVE)]
        for event in ('push', 'workflow_dispatch'):
            with (patch.object(gate, 'live', return_value=LIVE),
                  patch.object(gate, 'main_head', return_value=HEAD),
                  patch.object(gate, 'gh', return_value=commits),
                  patch.object(gate, 'qualified', side_effect=lambda sha: sha in (orphan, candidate)),
                  patch.object(gate, 'images_runs', side_effect=lambda sha: (
                      [{'event': event}] if sha == candidate else [])),
                  patch.object(gate, 'ci', return_value='green') as ci):
                self.assertEqual(((candidate, 'green'), LIVE), gate.target())
                ci.assert_called_once_with(candidate)

    def test_qualified_head_without_images_remains_recoverable(self):
        with (patch.object(gate, 'live', return_value=LIVE),
              patch.object(gate, 'main_head', return_value=HEAD),
              patch.object(gate, 'gh', return_value=[{'sha': HEAD}, {'sha': LIVE}]),
              patch.object(gate, 'qualified', return_value=True),
              patch.object(gate, 'images_runs') as runs,
              patch.object(gate, 'ci', return_value='green')):
            self.assertEqual(((HEAD, 'green'), LIVE), gate.target())
            runs.assert_not_called()

    def test_workflow_run_cannot_make_an_orphan_deliverable(self):
        with patch.object(gate, 'gh', return_value=[{'event': 'workflow_run'}]):
            self.assertEqual([], gate.images_runs(HEAD))

    def test_images_rechecks_gate_immediately_before_release(self):
        root = Path(__file__).resolve().parents[3]
        workflow = (root / '.github/workflows/images-ghcr.yml').read_text()
        self.assertEqual(workflow, (root / 'scripts/dashboard/workflows-reference/images-ghcr.yml').read_text())
        release = workflow.split('      - name: Release to every region\n', 1)[1]
        self.assertIn('          test "$(python3 scripts/dashboard/ci/release-gate.py release "$GITHUB_SHA")" = true\n'
                      '          bash scripts/ci/release-app.sh ts-dashboard "$IMAGE"\n', release)

    def test_ci_service_jobs_preserve_the_main_ref_boundary(self):
        workflow = (Path(__file__).resolve().parents[3] / '.github/workflows/ci.yml').read_text()
        for name in ('main', 'main-auth', 'main-auth-ordered'):
            job = workflow.split(f'\n  {name}:\n', 1)[1].split('    concurrency:', 1)[0]
            self.assertIn("github.ref == 'refs/heads/main'", job)
            self.assertIn("github.event_name == 'workflow_dispatch'", job)

    def test_main_runs_keep_their_signed_sha_after_unrelated_main_pushes(self):
        for ref in ('refs/heads/feature', 'refs/tags/main'):
            with (patch.dict(gate.os.environ, {'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_REF': ref}),
                  patch.object(gate, 'target') as target):
                self.assertFalse(gate.release(HEAD))
                target.assert_not_called()
        for event in ('push', 'workflow_dispatch'):
            with (patch.dict(gate.os.environ, {'GITHUB_EVENT_NAME': event, 'GITHUB_REF': 'refs/heads/main'}),
                  patch.object(gate, 'main_head', return_value=LIVE),
                  patch.object(gate, 'target', return_value=((HEAD, 'pending'), LIVE)),
                  patch.object(gate, 'newer_than', return_value=True)):
                # Its own proofs qualify the dashboard; it never waits for CI to finish.
                self.assertTrue(gate.release(HEAD))
            # A newer qualified revision still supersedes the older run.
            with (patch.dict(gate.os.environ, {'GITHUB_EVENT_NAME': event, 'GITHUB_REF': 'refs/heads/main'}),
                  patch.object(gate, 'target', return_value=((LIVE, 'green'), None))):
                self.assertFalse(gate.release(HEAD))


if __name__ == '__main__':
    unittest.main()
