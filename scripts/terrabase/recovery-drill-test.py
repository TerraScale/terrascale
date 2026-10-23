#!/usr/bin/env python3
"""Integration checks of the host-local fixture guards using owned temporary SQLite files only."""
import importlib.util
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("drill", Path(__file__).with_name("recovery-drill.py"))
drill = importlib.util.module_from_spec(spec)
spec.loader.exec_module(drill)


class RecoveryDrillTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="recovery-drill-check-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.patch = patch.object(drill, "ROOT", self.root)
        self.patch.start()
        self.addCleanup(self.patch.stop)
        self.host_patch = patch.object(drill.socket, "gethostname", return_value="ts-sjc-1")
        self.host_patch.start()
        self.addCleanup(self.host_patch.stop)
        nonce = drill.new_id()
        self.manifest = {"organization": drill.ORGANIZATION, "project": drill.PROJECT, "owner": drill.OWNER,
                         "host": "ts-sjc-1", "nonce": nonce, "name": "recovery_drill_" + nonce.lower(),
                         "database": drill.new_id(), "live": drill.new_id(), "deleted": drill.new_id(),
                         "incarnation": drill.new_id(), "stage": "prepared"}
        self.path = self.root / drill.ORGANIZATION / drill.PROJECT / (self.manifest["database"] + ".db")
        self.path.parent.mkdir(parents=True)
        with drill.database(self.path) as db:
            db.executescript("CREATE TABLE commit_offset(id INTEGER,incarnation TEXT); CREATE TABLE _tb_state(collection TEXT,id TEXT,live INTEGER); CREATE TABLE _tb_merkle(bucket INTEGER PRIMARY KEY,h1 INTEGER,h2 INTEGER);")
            db.execute("INSERT INTO commit_offset VALUES(1,?)", (self.manifest["incarnation"],))
            db.executemany("INSERT INTO _tb_state VALUES(?,?,?)", [("_collections", drill.COLLECTION, 1), (drill.COLLECTION, self.manifest["live"], 1), (drill.COLLECTION, self.manifest["deleted"], 0)])
        with drill.database(self.root / "_system.db") as db:
            db.execute("CREATE TABLE _tb_rebuild(organization TEXT,project TEXT,database TEXT,old_incarnation TEXT,phase INTEGER,new_incarnation TEXT)")
        self.manifest_path = self.root / "fixture.json"
        drill.save(self.manifest_path, self.manifest, exclusive=True)
        self.blocker = self.path.parent / ("." + self.manifest["database"] + "." + self.manifest["incarnation"] + ".rebuild")

    def client(self, stale=False):
        test = self

        class Client:
            def fresh(self, database):
                if stale:
                    raise drill.Refused("catalog_stale503")

            def data(self, manifest, host, method, suffix):
                key = "deleted" if manifest["deleted"] in suffix else "live"
                return (404, {"error": {"code": "not_found"}}) if key == "deleted" else (200, {"document": {"recovery_drill": manifest["nonce"], "kind": key}})

            def request(self, *args, **kwargs):
                # Stand-in only for the server's durable fault record; fixture code itself never writes SYSTEM.
                with drill.database(test.root / "_system.db") as db:
                    db.execute("INSERT INTO _tb_rebuild VALUES(?,?,?,?,0,NULL)", (drill.ORGANIZATION, drill.PROJECT, test.manifest["database"], test.manifest["incarnation"]))
                return 200, b""

        return Client()

    def assert_undamaged(self):
        with drill.database(self.path) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM _tb_merkle WHERE bucket=-1").fetchone()[0], 0)
        self.assertIsNone(drill.pending(self.manifest))

    def test_stale_preparation_creates_no_manifest_or_database(self):
        target = self.root / "new.json"
        with self.assertRaises(drill.Refused):
            drill.prepare(self.client(stale=True), target)
        self.assertFalse(target.exists())
        self.assert_undamaged()

    def test_stale_arm_keeps_the_fixture_undamaged(self):
        with self.assertRaises(drill.Refused):
            drill.arm(self.client(stale=True), self.manifest_path, self.manifest, self.manifest["nonce"])
        self.assertFalse(self.blocker.exists())
        self.assert_undamaged()

    def test_extra_data_refuses_fault_injection(self):
        with drill.database(self.path) as db:
            db.execute("INSERT INTO _tb_state VALUES('unrelated',?,1)", (drill.new_id(),))
        with self.assertRaises(drill.Refused):
            drill.arm(self.client(), self.manifest_path, self.manifest, self.manifest["nonce"])
        self.assert_undamaged()

    def test_confirmed_synthetic_arm_preserves_documents_and_leaves_real_record_to_server(self):
        result = drill.arm(self.client(), self.manifest_path, self.manifest, self.manifest["nonce"])
        self.assertEqual(result["stage"], "pending")
        self.assertEqual(drill.pending(result), (result["incarnation"], 0, None))
        self.assertTrue(self.blocker.is_dir())
        with drill.database(self.path) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM _tb_state").fetchone()[0], 3)
            self.assertEqual(db.execute("SELECT bucket FROM _tb_merkle").fetchone()[0], -1)

    def test_stale_retry_keeps_blocker_and_pending_record(self):
        drill.arm(self.client(), self.manifest_path, self.manifest, self.manifest["nonce"])
        with self.assertRaises(drill.Refused):
            drill.retry(self.client(stale=True), self.manifest_path, self.manifest)
        self.assertTrue(self.blocker.is_dir())
        self.assertEqual(drill.pending(self.manifest), (self.manifest["incarnation"], 0, None))

    def test_original_database_and_foreign_tenant_are_rejected(self):
        for key, value in (("database", drill.EXISTING_DATABASE), ("organization", drill.new_id()), ("project", drill.new_id())):
            with self.assertRaises(drill.Refused):
                drill.validate({**self.manifest, key: value})
        self.assert_undamaged()

    def test_symlink_and_public_header_files_are_rejected(self):
        private = self.root / "headers"
        private.write_text("unused")
        private.chmod(0o644)
        with self.assertRaises(drill.Refused):
            drill.private_read(private)
        private.chmod(0o600)
        link = self.root / "link"
        link.symlink_to(private)
        with self.assertRaises(OSError):
            drill.private_read(link)

    def test_actual_preflight_reports_catalog_stale_as_required_refusal(self):
        client = drill.Client.__new__(drill.Client)
        client.request = lambda *args, **kwargs: (503, {"error": {"code": "catalog_stale"}})
        with self.assertRaisesRegex(drill.Refused, "503 catalog_stale is the correct fail-closed"):
            client.fresh(self.manifest["database"])
        self.assert_undamaged()

    def test_manifest_is_private_and_cannot_be_overwritten_by_prepare(self):
        self.assertEqual(os.stat(self.manifest_path).st_mode & 0o777, 0o600)
        with self.assertRaises(drill.Refused):
            drill.prepare(self.client(), self.manifest_path)
        self.assert_undamaged()


if __name__ == "__main__":
    unittest.main()
