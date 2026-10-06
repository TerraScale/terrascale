"""Pure checks for the deployed runner; live runs need the benchmark project's credentials."""
from pathlib import Path
import json
import shlex
import signal
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch

import deployed


class DeployedTests(unittest.TestCase):
    def test_member_summary_uses_deltas_within_the_load_window(self):
        def tick(t, cpu=(10, 5), rss=104857600, gc=7, written=1048576):
            lines = [f"T {t}", *(f"dotnet_process_cpu_time_seconds_total {value}" for value in cpu),
                     f"dotnet_process_memory_working_set_bytes {rss}", f"dotnet_gc_collections_total {gc}",
                     "dotnet_gc_pause_time_seconds_total 0.5", "dotnet_gc_heap_total_allocated_bytes_total 0",
                     "io_read_bytes 0", f"io_write_bytes {written}"]
            return "\n".join(lines)

        log = "\n".join([
            tick(100.0),
            # An empty metrics scrape (only /proc/<pid>/io) and a partial one are skipped, not zero.
            "T 103.0\nio_read_bytes 0\nio_write_bytes 1048576",
            "T 106.0\ndotnet_process_cpu_time_seconds_total 1\nio_read_bytes 0\nio_write_bytes 2097152",
            tick(110.0, cpu=(14, 6), rss=209715200, gc=9, written=3145728),
            tick(500.0, cpu=(99, 99)),
        ])
        summary = deployed.member_summary(log, 101, 109)
        self.assertEqual(summary["cpu_percent"], 50.0)
        self.assertEqual(summary["rss_peak_mb"], 200.0)
        self.assertEqual(summary["gc_collections"], 2)
        self.assertEqual(summary["disk_write_mb"], 2.0)
        self.assertEqual(summary["complete_ticks"], 2)
        self.assertIsNone(deployed.member_summary("T 100\nio_read_bytes 0\nT 110\nio_read_bytes 1\n", 101, 109))

    def test_cli_output_search_and_lb_and_cost(self):
        output = {"database": {"id": "0f8fad5b-d9cb-469f-a165-70867728950e", "name": "bench"}}
        self.assertEqual(deployed.find(output, lambda value: len(value) == 36), "0f8fad5b-d9cb-469f-a165-70867728950e")
        self.assertEqual(deployed.find([{"secret": "tb_x"}], lambda value: value.startswith("tb_")), "tb_x")
        members = {"10.251.0.11:9465": {"offline": False, "drain": False}, "10.251.0.22:9465": {"offline": True, "drain": True}}
        self.assertEqual(deployed.active(members), {"10.251.0.11:9465"})
        self.assertNotEqual(deployed.active(members), deployed.FINAL_LB)
        self.assertEqual(deployed.cost_per_million(0, 4), None)
        self.assertAlmostEqual(deployed.cost_per_million(100, 1), deployed.MEMBER_USD_PER_HOUR / 0.36, places=3)
        self.assertEqual(deployed.client_cpu_busy({"cpu_jiffies": [0, 0, 0, 0, 0]}, {"cpu_jiffies": [30, 0, 10, 50, 10]}), 40.0)

    def test_cleanup_revokes_the_key_when_database_deletion_fails(self):
        calls, record = [], {}

        def ts(_cli, *args):
            calls.append(args[:2])
            if args[:2] == ("databases", "delete"):
                raise subprocess.CalledProcessError(1, "ts")

        with patch.object(deployed, "ts", ts):
            errors = deployed.cleanup([], "ts", record, "db-id", "bench-x", "tsk_1")
        self.assertEqual(calls, [("databases", "delete"), ("keys", "revoke")])
        self.assertEqual(errors, ["delete database: CalledProcessError"])
        self.assertEqual(record, {"key_id": "tsk_1", "key_revoked": True})

    def test_partial_transfer_still_removes_the_client_scratch_directory(self):
        commands = []

        def run(argv, **_options):
            raise subprocess.CalledProcessError(1, argv)

        with patch.object(deployed, "client_shell", lambda _client, command: commands.append(command)), patch.object(deployed.subprocess, "run", run):
            with self.assertRaises(subprocess.CalledProcessError):
                with deployed.client_scratch("beta", "/tmp/ts606-x", ["driver", "token"]):
                    self.fail("the body must not run after a failed transfer")
        self.assertEqual(commands, ["install -d -m 700 /tmp/ts606-x", "shred -u /tmp/ts606-x/token 2>/dev/null; rm -rf /tmp/ts606-x"])

    def test_samplers_started_before_a_failure_are_registered(self):
        started = []

        class Process:
            def __init__(self, *_args, **_kwargs):
                if started:
                    raise OSError("second sampler")
                started.append(self)

        samplers = []
        with tempfile.TemporaryDirectory() as scratch, patch.object(deployed, "netbird_ips", lambda _: {"gru-1-ctl-1": "a", "gru-1-ctl-2": "b"}), patch.object(deployed.subprocess, "Popen", Process):
            with self.assertRaises(OSError):
                deployed.start_samplers(Path(scratch), "netbird", 60, samplers)
        self.assertEqual([member for member, _ in samplers], ["gru-1-ctl-1"])

    def test_large_seeds_are_padded_and_unknown_lb_has_no_cost(self):
        self.assertEqual(deployed.seed_document(3, 0), {"rank": 3, "group": 1, "payload": "ts500"})
        self.assertEqual(len(deployed.seed_document(3, 16384)["padding"]), 16384)
        self.assertIsNone(deployed.cost_per_million(100, 0))

    def test_run_payload_reports_exit_codes_and_stops_at_the_duration_bound(self):
        with patch.object(deployed, "GUARDS", {}):
            returncode, guard = deployed.run_payload("local", "bash -c 'exit 3'", 10, "/tmp/unused")
            self.assertEqual((returncode, guard.tripped, guard.is_alive()), (3, None, False))
            returncode, guard = deployed.run_payload("local", "sleep 30", 0.2, "/tmp/unused")
            self.assertNotEqual(returncode, 0)
            self.assertEqual(guard.tripped, "duration bound reached")
            self.assertFalse(guard.is_alive())

    def test_cleanup_continues_past_stalled_steps(self):
        calls, record = [], {}

        def ts(_cli, *args):
            calls.append(args[:2])
            if args[:2] == ("databases", "delete"):
                raise subprocess.TimeoutExpired("ts", deployed.TS_CLI_TIMEOUT_S)

        # A sampler that ignores SIGTERM is killed after the grace period.
        stuck = subprocess.Popen(["bash", "-c", "trap '' TERM; sleep 30 & wait"])
        time.sleep(0.2)
        with patch.object(deployed, "ts", ts), patch.object(deployed, "STOP_GRACE_S", 0.3):
            errors = deployed.cleanup([("gru-1-ctl-1", stuck)], "ts", record, "db-id", "bench-x", "tsk_1")
        self.assertIsNotNone(stuck.poll())
        self.assertEqual(calls, [("databases", "delete"), ("keys", "revoke")])
        self.assertEqual(errors, ["delete database: TimeoutExpired"])
        self.assertEqual(record, {"key_id": "tsk_1", "key_revoked": True})

    def test_client_probe_without_a_cpu_model(self):
        probe = "host=arm\ncpus=4\nmodel=\nloadavg=0.1 0.2 0.3 1/2 3\nstat=cpu 1 2 3 4 5\n" + "rtt=0.020 0.045 0.073 200\n" * 3 + "rtt=0 0 0 000\n"
        with patch.object(deployed, "client_shell", lambda _client, _command: probe):
            state = deployed.client_state("arm")
        self.assertEqual((state["host"], state["cpu_count"], state["cpu_model"]), ("arm", 4, "unknown"))
        self.assertEqual(state["cpu_jiffies"], [1, 2, 3, 4, 5])
        self.assertEqual(state["rtt_ms"]["samples"], 3)
        self.assertAlmostEqual(state["rtt_ms"]["tls_ready"], 45.0)

    def test_guard_trip_kills_a_driver_that_ignores_sigterm(self):
        # Port 9 refuses connections, so the guard trips after two polls (about 4 s).
        with tempfile.TemporaryDirectory() as scratch:
            pidfile = Path(scratch) / "pid"
            driver = f"bash -c 'echo $$ > {pidfile}; trap \"\" TERM; sleep 60 & wait; sleep 60'"
            with patch.object(deployed, "GUARDS", {"api": "http://127.0.0.1:9/"}), patch.object(deployed, "STOP_GRACE_S", 0.5):
                started = time.monotonic()
                returncode, guard = deployed.run_payload("local", driver, 600, "/tmp/unused")
            self.assertNotEqual(returncode, 0)
            self.assertTrue(guard.tripped.startswith("api answered None"))
            self.assert_dies(int(pidfile.read_text()))
        self.assertLess(time.monotonic() - started, 20)

    @staticmethod
    def stopped(pid):
        # A zombie is stopped: CI containers run `tail` as PID 1, which never reaps orphans.
        try:
            return Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()[0] == "Z"
        except FileNotFoundError:
            return True

    def assert_dies(self, pid, within=10):
        deadline = time.monotonic() + within
        while not self.stopped(pid):
            if time.monotonic() > deadline:
                self.fail(f"process {pid} is still running")
            time.sleep(0.1)

    def test_ambiguous_create_is_reconciled_by_name(self):
        def ts(_cli, kind, action, *_args):
            if action == "create":
                raise subprocess.TimeoutExpired("ts", deployed.TS_CLI_TIMEOUT_S)
            return {kind: [{"id": "other", "name": "bench-other"}, {"id": "mine", "name": "bench-x"}]}

        with patch.object(deployed, "ts", ts):
            with self.assertRaises(deployed.CreateFailed) as caught:
                deployed.create("ts", "databases", "bench-x", "--region", "GRU")
        self.assertEqual(caught.exception.resource_id, "mine")

        def malformed(_cli, kind, action, *_args):
            if action == "create":
                raise ValueError("invalid JSON")
            return {kind: []}

        with patch.object(deployed, "ts", malformed):
            with self.assertRaises(deployed.CreateFailed) as caught:
                deployed.create("ts", "keys", "bench-x")
        self.assertIsNone(caught.exception.resource_id)

    def test_client_cpu_busy_ignores_guest_time(self):
        before = {"cpu_jiffies": [0] * 10}
        after = {"cpu_jiffies": [30, 0, 10, 50, 10, 0, 0, 0, 25, 5]}
        self.assertEqual(deployed.client_cpu_busy(before, after), 40.0)

    def test_finished_run_is_not_timed_out_by_slow_cleanup(self):
        def slow_stop(process, **_options):
            process.wait()
            time.sleep(0.5)

        with patch.object(deployed, "GUARDS", {}), patch.object(deployed, "stop", slow_stop):
            returncode, guard = deployed.run_payload("local", "true", 0.3, "/tmp/unused")
        self.assertEqual((returncode, guard.tripped), (0, None))

    def test_runner_verdict_overrides_a_passed_driver_receipt(self):
        passed = {"status": "passed", "rows": []}
        self.assertEqual(deployed.payload_result(passed, 0, True, None)["status"], "passed")
        partial = deployed.payload_result(passed, 0, False, None)
        self.assertEqual((partial["status"], partial["driver_status"], partial["evidence_copied"]), ("failed", "passed", False))
        aborted = deployed.payload_result(passed, 0, True, "auth answered 502 twice")
        self.assertEqual((aborted["status"], aborted["aborted"]), ("aborted", "auth answered 502 twice"))
        self.assertEqual(deployed.payload_result(passed, 1, True, None)["status"], "failed")
        self.assertEqual(deployed.payload_result({}, 0, False, None)["status"], "failed")
        self.assertEqual(deployed.payload_result({"status": "aborted"}, 1, True, None)["status"], "aborted")
        self.assertEqual(set(deployed.PAYLOAD_VERDICT) - set(partial), set())

    def test_interrupted_create_is_reconciled_and_still_cancels(self):
        def ts(_cli, kind, action, *_args):
            if action == "create":
                raise KeyboardInterrupt
            return {kind: [{"id": "committed", "name": "bench-x"}]}

        with patch.object(deployed, "ts", ts):
            with self.assertRaises(KeyboardInterrupt) as caught:
                deployed.create("ts", "keys", "bench-x")
        self.assertEqual(caught.exception.resource_id, "committed")

    def test_seeding_is_paced_and_stops_when_the_guard_trips(self):
        times = []

        def request(_opener, _origin, path, method, *_args):
            times.append((time.monotonic(), method, path))

        with patch.object(deployed.acceptance, "request", request), patch.object(deployed, "GUARDS", {}):
            deployed.seed(None, {}, "/db", 200)
        self.assertEqual([method for _, method, _ in times].count("POST"), 2)
        self.assertEqual(len(times), 66)
        # Pacing holds between request starts; a recorded timestamp may lag its start by up to one
        # spacing on a loaded host, so check the whole span rather than each gap.
        self.assertGreaterEqual(times[-1][0] - times[0][0], (len(times) - 2) / 200)
        times.clear()
        # Port 9 refuses connections: the guard trips after about 4 s, well before 66 requests at 10/s.
        with patch.object(deployed.acceptance, "request", request), patch.object(deployed, "GUARDS", {"auth": "http://127.0.0.1:9/"}):
            with self.assertRaisesRegex(RuntimeError, "setup aborted: auth answered None twice"):
                deployed.seed(None, {}, "/db", 10)
        self.assertLess(len(times), 66)

    def test_lease_stops_the_client_driver_when_remote_cleanup_fails(self):
        # The "client" is a local shell standing in for SSH: terminating it does not reach the
        # driver, and the cleanup SSH fails. Only the lease is left to stop the driver.
        def unreachable(_client, _command):
            raise subprocess.CalledProcessError(255, "ssh")

        with tempfile.TemporaryDirectory() as scratch:
            pidfile = Path(scratch) / "pid"
            driver = f"bash -c 'echo $$ > {pidfile}; trap \"\" TERM; sleep 60 & wait; sleep 60'"
            with patch.object(deployed, "client_argv", lambda _client, command: ["bash", "-c", command]), \
                    patch.object(deployed, "client_shell", unreachable), patch.object(deployed, "GUARDS", {}), \
                    patch.object(deployed, "STOP_GRACE_S", 0.5):
                with self.assertRaises(subprocess.CalledProcessError):
                    deployed.run_payload("beta", driver, 1, "/tmp/ts606-x")
            self.assert_dies(int(pidfile.read_text()))

    def test_watchdog_returns_the_driver_exit_code(self):
        # stdin stays open (an unexpired lease) while the driver runs.
        process = subprocess.Popen(["bash", "-c", deployed.watchdog("bash -c 'exit 7'", 10)], stdin=subprocess.PIPE)
        self.assertEqual(process.wait(timeout=30), 7)
        process.stdin.close()

    def test_local_driver_is_already_stopped_when_run_payload_returns(self):
        with tempfile.TemporaryDirectory(prefix="ts606 space ") as scratch:
            pidfile = Path(scratch) / "pid"
            # A path with spaces must reach the driver as one argument.
            driver = deployed.driver_command("bash", scratch, "x").replace("--config", "-c 'echo $$ > \"$1\"; trap \"\" TERM; sleep 60 & wait; sleep 60' _ " + shlex.quote(str(pidfile)) + " --config", 1)
            with patch.object(deployed, "GUARDS", {}), patch.object(deployed, "STOP_GRACE_S", 0.5):
                deployed.run_payload("local", driver, 1, scratch)
            self.assertTrue(self.stopped(int(pidfile.read_text())), "driver still running when run_payload returned")

    def test_driver_command_quotes_paths(self):
        command = deployed.driver_command("/opt/my driver", "/tmp/a b;rm", "small")
        self.assertEqual(shlex.split(command), ["/opt/my driver", "--config", "/tmp/a b;rm/small.json", "--token-file", "/tmp/a b;rm/token", "--output", "/tmp/a b;rm/small"])

    def test_cleanup_finds_a_create_that_commits_late(self):
        lookups, calls, record = [], [], {}

        def ts(_cli, kind, action, *args):
            calls.append((kind, action, *args))
            if action == "list":
                lookups.append(kind)
                # The database becomes visible on the third lookup; one lookup fails outright.
                if kind == "databases" and len(lookups) == 2:
                    raise subprocess.TimeoutExpired("ts", 1)
                return {kind: [{"id": "late", "name": "bench-x"}] if kind == "databases" and len(lookups) >= 3 else []}

        with patch.object(deployed, "ts", ts), patch.object(deployed, "RECONCILE_DELAY_S", 0):
            errors = deployed.cleanup([], "ts", record, None, "bench-x", None, {"databases", "keys"})
        self.assertIn(("databases", "delete", "--database-id", "late", "--confirm-name", "bench-x", "--yes"), calls)
        self.assertEqual((record["id"], record["deleted"]), ("late", True))
        # The key never appeared: reported, not skipped.
        self.assertEqual(errors, [f"revoke key: unresolved, no keys named bench-x after {deployed.RECONCILE_TRIES} lookups"])

    def test_successful_create_with_an_unrecognized_reply_is_still_cleaned_up(self):
        calls, attempted, record = [], set(), {}

        def ts(_cli, kind, action, *args):
            calls.append((kind, action, *args))
            if action == "create":
                return {"result": "ok"}  # committed, but no recognizable ID
            return {kind: [{"id": "d-1", "name": "bench-x"}]}

        with patch.object(deployed, "ts", ts), patch.object(deployed, "RECONCILE_DELAY_S", 0):
            _, database = deployed.create_tracked("ts", "databases", "bench-x", attempted, r"[0-9a-f-]{36}", "--region", "GRU")
            self.assertIsNone(database)
            self.assertEqual(attempted, {"databases"})
            errors = deployed.cleanup([], "ts", record, database, "bench-x", None, attempted)
        self.assertEqual(errors, [])
        self.assertIn(("databases", "delete", "--database-id", "d-1", "--confirm-name", "bench-x", "--yes"), calls)
        self.assertEqual(record, {"id": "d-1", "deleted": True})

    def test_created_ids_prefer_the_named_object(self):
        output = {"key": {"id": "tsk_abc", "name": "bench-x", "secret": "tb_s"}}
        with patch.object(deployed, "ts", lambda *_args: output):
            self.assertEqual(deployed.create_tracked("ts", "keys", "bench-x", set(), r"tsk_\S+")[1], "tsk_abc")

    def test_missing_member_metrics_are_reported(self):
        summary = {"seconds": 30}
        stats = {"gru-1-ctl-1": summary, "gru-1-ctl-2": None, "gru-1-ctl-3": summary, "gru-1-db-1": None, "gru-1-db-2": None}
        active = ["10.251.0.11:9465", "10.251.0.12:9465", "10.251.0.13:9465"]
        self.assertEqual(deployed.missing_members(stats, active, True), ["gru-1-ctl-2"])
        # A drained member needs no metrics; an unknown serving backend counts as missing.
        self.assertEqual(deployed.missing_members(stats, ["10.251.0.11:9465", "10.251.0.99:9465"], True), ["10.251.0.99:9465"])
        self.assertEqual(deployed.missing_members(stats, [], False), ["gru-1-ctl-2", "gru-1-db-1", "gru-1-db-2"])
        self.assertEqual(deployed.missing_members(dict.fromkeys(deployed.MEMBERS), active, True), ["gru-1-ctl-1", "gru-1-ctl-2", "gru-1-ctl-3"])

    def test_rtt_medians_are_per_column(self):
        probe = "host=h\ncpus=1\nmodel=m\nloadavg=0 0 0 1/1 1\nstat=cpu 1 2 3 4 5\n" + "".join(
            f"rtt={a / 1000} {b / 1000} {c / 1000} 200\n" for a, b, c in [(10, 100, 110), (20, 30, 40), (30, 50, 60)])
        with patch.object(deployed, "client_shell", lambda _client, _command: probe):
            rtt = deployed.client_state("h")["rtt_ms"]
        self.assertEqual((round(rtt["tcp_connect"]), round(rtt["tls_ready"]), round(rtt["healthz_total"])), (20, 50, 60))

    def test_watchdog_snapshots_cpu_counters_around_the_driver(self):
        with tempfile.TemporaryDirectory(prefix="ts606 cpu ") as scratch:
            prefix = f"{scratch}/small-cpu"
            process = subprocess.Popen(["bash", "-c", deployed.watchdog("true", 10, prefix)], stdin=subprocess.PIPE)
            self.assertEqual(process.wait(timeout=30), 0)
            process.stdin.close()
            for suffix in (".before", ".after"):
                line = Path(prefix + suffix).read_text()
                self.assertTrue(line.startswith("cpu "), suffix)
                self.assertGreater(sum(deployed.jiffies(line)), 0)

    def test_netbird_discovery_is_bounded(self):
        with patch.object(deployed, "NETBIRD_TIMEOUT_S", 0.5):
            started = time.monotonic()
            with self.assertRaises(subprocess.TimeoutExpired):
                deployed.netbird_ips("bash -c 'sleep 60'")
        self.assertLess(time.monotonic() - started, 10)

    def test_sigterm_after_creation_still_deletes_the_database_and_key(self):
        script = """
import sys, time
from unittest.mock import patch
sys.path.insert(0, sys.argv[1])
import deployed
log = open(sys.argv[2], "a", buffering=1)

def ts(_cli, kind, action, *args):
    log.write(f"{kind} {action}\\n")
    if action == "create":
        return {"id": "0f8fad5b-d9cb-469f-a165-70867728950e" if kind == "databases" else "tsk_1", "name": args[1], "secret": "tb_x"}
    return {}

def seed(*_args):
    log.write("seeding\\n")
    time.sleep(60)

client = {"host": "c", "cpu_count": 1, "cpu_model": "m", "loadavg": "0 0 0", "cpu_jiffies": [0] * 10, "rtt_ms": {}}
with patch.object(deployed, "ts", ts), patch.object(deployed, "seed", seed), patch.object(deployed, "get", lambda *_a, **_k: 200), patch.object(deployed, "client_state", lambda _c: client), \\
        patch.object(deployed.acceptance, "benchmark_source_state", lambda: {}):
    deployed.main(["--client", "local", "--driver", "/bin/true", "--no-lb-check", "--no-metrics", "--output-parent", sys.argv[3]])
"""
        with tempfile.TemporaryDirectory() as scratch:
            log = Path(scratch) / "calls.log"
            log.touch()
            env = {**deployed.os.environ, "TERRASCALE_PROJECT_ID": "p", "TERRASCALE_DASHBOARD_TOKEN": "t"}
            process = subprocess.Popen([sys.executable, "-c", script, str(Path(deployed.__file__).parent), str(log), scratch], env=env, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            deadline = time.monotonic() + 30
            while "seeding" not in log.read_text():
                self.assertLess(time.monotonic(), deadline, "the run never reached seeding")
                time.sleep(0.1)
            process.send_signal(signal.SIGTERM)
            self.assertNotEqual(process.wait(timeout=30), 0)
            calls = log.read_text().splitlines()
            self.assertIn("databases delete", calls)
            self.assertIn("keys revoke", calls)
            receipt = json.loads(next(Path(scratch).glob("run-*/receipt.json")).read_text())
            self.assertEqual((receipt["database"]["deleted"], receipt["database"]["key_revoked"]), (True, True))


if __name__ == "__main__":
    unittest.main()
