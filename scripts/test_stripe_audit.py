"""Run with: python3 -m unittest discover -s scripts -p 'test_stripe_audit.py'."""

import contextlib
import importlib.util
import io
import json
import pathlib
import runpy
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("stripe_audit", pathlib.Path(__file__).with_name("audit-stripe.py"))
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class StripeAuditTest(unittest.TestCase):
    def environment(self, mode):
        values = {
            "STRIPE_MODE": mode,
            "STRIPE_SECRET_KEY": f"rk_{mode}_PRIVATE",
            "STRIPE_WEBHOOK_SECRET": "whsec_PRIVATE",
            "DASHBOARD_PUBLIC_ORIGIN": "https://dash.terrascale.tech",
        }
        for plan, interval in audit.EXPECTED:
            values[f"STRIPE_PRICE_{plan.upper()}_{interval.upper()}"] = f"price_{plan}_{interval}"
        return values

    def response(self, mode, path, params=None):
        live = mode == "live"
        if path.startswith("prices/"):
            plan, interval = path.removeprefix("prices/price_").split("_")
            return {"active": True, "livemode": live, "currency": "usd", "unit_amount": audit.EXPECTED[plan, interval],
                    "recurring": {"interval": "month" if interval == "monthly" else "year", "interval_count": 1},
                    "product": {"name": plan, "active": True, "livemode": live}}
        if path == "webhook_endpoints":
            return {"data": [{"url": "https://dash.terrascale.tech/api/auth/stripe/webhook", "livemode": live,
                              "status": "enabled", "api_version": audit.library_api_version(), "enabled_events": sorted(audit.EVENTS)}]}
        if path == "billing_portal/configurations":
            return {"data": [{"is_default": True, "active": True, "livemode": live, "features": {
                "payment_method_update": {"enabled": True}, "invoice_history": {"enabled": True},
                "subscription_cancel": {"enabled": True}, "subscription_update": {"enabled": False}}}]}
        if path in ("subscriptions", "events"):
            return {"data": []}
        self.fail(f"unexpected Stripe read: {path}")

    def run_audit(self, values, arguments, response=None, stdin=""):
        output = io.StringIO()
        with patch.dict(audit.os.environ, values, clear=True), patch.object(audit.sys, "argv", ["audit", *arguments]), \
                patch.object(audit.sys, "stdin", io.StringIO(stdin)), patch.object(audit, "stripe_get", side_effect=response) as stripe, \
                contextlib.redirect_stdout(output):
            status = audit.main()
        self.assertNotIn("PRIVATE", output.getvalue())
        return status, json.loads(output.getvalue()), stripe.call_count

    def test_online_audit_accepts_both_modes(self):
        for mode in ("test", "live"):
            with self.subTest(mode=mode):
                status, report, calls = self.run_audit(self.environment(mode), ["--mode", mode, "--online"],
                                                     lambda key, path, params=None: self.response(mode, path, params))
                self.assertEqual(status, 0)
                self.assertTrue(report["passed"])
                self.assertGreater(calls, 0)

    def test_wrong_mode_never_contacts_stripe(self):
        for expected, configured, key_mode in (("test", "live", "live"), ("live", "live", "test"), ("live", "test", "live")):
            with self.subTest(expected=expected, configured=configured, key_mode=key_mode):
                values = self.environment(configured)
                values["STRIPE_SECRET_KEY"] = f"sk_{key_mode}_PRIVATE"
                status, report, calls = self.run_audit(values, ["--mode", expected, "--online"])
                self.assertEqual(status, 1)
                self.assertFalse(report["passed"])
                self.assertEqual(calls, 0)

    def test_vault_stdin_overrides_inherited_test_settings(self):
        contents = "\n".join(f"{name}={value}" for name, value in self.environment("live").items())
        status, report, _ = self.run_audit(self.environment("test"), ["--mode", "live", "--env-file", "-"], stdin=contents)
        self.assertEqual(status, 0)
        self.assertTrue(report["passed"])

    def test_incomplete_vault_bundle_never_contacts_stripe(self):
        status, report, calls = self.run_audit(self.environment("live"),
                                              ["--mode", "live", "--online", "--env-file", "-"], stdin="STRIPE_MODE=live\n")
        self.assertEqual(status, 1)
        self.assertFalse(report["passed"])
        self.assertEqual(calls, 0)

    def test_safe_nondefault_portal_cannot_hide_unsafe_default(self):
        def response(key, path, params=None):
            value = self.response("live", path, params)
            if path == "billing_portal/configurations":
                safe = value["data"][0]
                unsafe = json.loads(json.dumps(safe))
                safe["is_default"] = False
                unsafe["features"]["subscription_update"]["enabled"] = True
                value["data"].append(unsafe)
            return value
        status, report, _ = self.run_audit(self.environment("live"), ["--mode", "live", "--online"], response)
        self.assertEqual(status, 1)
        self.assertFalse(report["checks"]["portal_payment_and_invoices"])

    def test_request_errors_never_print_credentials(self):
        output = io.StringIO()
        with patch.dict(audit.os.environ, self.environment("live"), clear=True), \
                patch.object(audit.sys, "argv", ["audit", "--mode", "live", "--online"]), \
                patch.object(audit.urllib.request, "urlopen", side_effect=ValueError("invalid header rk_live_PRIVATE")), \
                contextlib.redirect_stderr(output), self.assertRaises(SystemExit) as exited:
            runpy.run_path(str(pathlib.Path(__file__).with_name("audit-stripe.py")), run_name="__main__")
        self.assertEqual(exited.exception.code, 2)
        self.assertNotIn("PRIVATE", output.getvalue())
        self.assertEqual(json.loads(output.getvalue()), {"passed": False, "error": "ValueError"})

    def test_live_audit_rejects_test_objects_and_unsafe_portal(self):
        for target in ("prices", "product", "webhook_endpoints", "billing_portal/configurations", "portal_switching"):
            with self.subTest(target=target):
                def response(key, path, params=None):
                    value = self.response("live", path, params)
                    if target == "prices" and path.startswith("prices/"):
                        value["livemode"] = False
                    elif target == "product" and path.startswith("prices/"):
                        value["product"]["livemode"] = False
                    elif path == target:
                        value["data"][0]["livemode"] = False
                    elif target == "portal_switching" and path == "billing_portal/configurations":
                        value["data"][0]["features"]["subscription_update"]["enabled"] = True
                    return value
                status, report, _ = self.run_audit(self.environment("live"), ["--mode", "live", "--online"], response)
                self.assertEqual(status, 1)
                self.assertFalse(report["passed"])


if __name__ == "__main__":
    unittest.main()
