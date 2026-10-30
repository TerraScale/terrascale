"""Exercise the complete live probe with deterministic provider HTTP contracts."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import unittest
import urllib.parse
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("signup_probe", Path(__file__).parents[1] / "signup-mail-check.py")
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class Contracts:
    def __init__(self, primary=False, wrong_recipient=False, cleanup_fails=False, signup_fails=False, fallback_mx=True):
        self.primary = primary
        self.wrong_recipient = wrong_recipient
        self.cleanup_fails = cleanup_fails
        self.signup_fails = signup_fails
        self.fallback_mx = fallback_mx
        self.address = "initial@fallback.test"
        self.sent = 0
        self.created = 0
        self.deleted = False
        self.forgotten = False
        self.seconds = 0

    def sleep(self, _):
        self.seconds += 120

    def request(self, opener, url, method="GET", data=None, headers=None, retry=False, deadline=None):
        parsed = urllib.parse.urlsplit(url)
        if parsed.hostname == "dns.google":
            domain = urllib.parse.parse_qs(parsed.query)["name"][0]
            valid = self.primary if domain == "primary.test" else self.fallback_mx
            return json.dumps({"Status": 0 if valid else 3, "Answer": [{"type": 15, "data": "10 mx.example.test."}] if valid else []})
        if parsed.hostname == "auth.terrascale.tech":
            if method == "POST":
                self.sent += 1
                if self.signup_fails:
                    raise probe.CheckFailure("HTTP request failed or timed out")
                return '<form method="post" action="/ui/terrascale/sign-up/verify"><input name="pending_token" value="private-pending"></form>'
            return '<form method="post" action="/ui/terrascale/sign-up"><input name="csrf" value="private-csrf"></form>'
        if parsed.hostname == "api.mail.tm":
            if parsed.path == "/domains":
                return json.dumps({"hydra:member": [{"domain": "primary.test", "isActive": True, "isPrivate": False}]})
            if parsed.path == "/accounts":
                self.created += 1
                self.address = json.loads(data)["address"]
                return '{"id":"private-account"}'
            if parsed.path == "/token":
                return '{"token":"private-token"}'
            if parsed.path == "/messages":
                return '{"hydra:member":[{"id":"private-message"}]}'
            if parsed.path == "/messages/private-message":
                return json.dumps({"from": {"address": probe.SENDER}, "to": [{"address": self.address}],
                    "text": self.address + "\n123456\n", "subject": "123456"})
            if method == "DELETE":
                self.deleted = True
                return ""
        if parsed.hostname == "www.guerrillamail.com":
            fields = urllib.parse.parse_qs(parsed.query if method == "GET" else data.decode())
            function = fields["f"][0]
            if function == "get_email_address":
                return json.dumps({"email_addr": self.address})
            if function == "set_email_user":
                self.address = fields["email_user"][0] + "@fallback.test"
                return json.dumps({"email_addr": self.address})
            if function == "get_email_list":
                return json.dumps({"email": self.address, "list": [] if self.deleted else [{"mail_id": "private-message"}]})
            if function == "fetch_email":
                return json.dumps({"mail_id": "private-message", "mail_from": "TerraScale <" + probe.SENDER + ">",
                    "mail_recipient": "other" if self.wrong_recipient else self.address.partition("@")[0],
                    "mail_subject": "Verify: 123456", "mail_body": "<p>" + self.address + "</p><div><b>123456</b></div>"})
            if function == "del_email":
                self.deleted = not self.cleanup_fails
                return '{"deleted_ids":[]}'
            if function == "forget_me":
                self.forgotten = True
                return "true"
        raise AssertionError("Unexpected HTTP contract request")

    def run(self):
        output = io.StringIO()
        with patch.object(probe, "request", self.request), patch.object(probe.time, "monotonic", lambda: self.seconds), \
                patch.object(probe.time, "sleep", self.sleep), contextlib.redirect_stdout(output):
            code = probe.run()
        # Every emitted record is safe even when a later cleanup step fails.
        text = output.getvalue()
        for secret in (self.address, "123456", "private-pending", "private-csrf", "private-token", "private-account", "private-message"):
            if secret in text:
                raise AssertionError("Probe output exposed private message or session data")
        return code, [json.loads(line) for line in text.splitlines()]


class SignupProbeTests(unittest.TestCase):
    def test_expired_primary_domain_uses_independent_inbox_and_full_message(self):
        contract = Contracts()
        code, records = contract.run()
        self.assertEqual(code, 0)
        self.assertEqual(contract.created, 0)
        self.assertEqual(contract.sent, 1)
        self.assertTrue(contract.deleted and contract.forgotten)
        self.assertEqual(records[0]["inbox_provider"], "guerrillamail.com")
        self.assertEqual(records[0]["mailbox_cleanup"], "messages_deleted_session_released")

    def test_healthy_primary_still_fetches_message_and_deletes_account(self):
        contract = Contracts(primary=True)
        code, records = contract.run()
        self.assertEqual(code, 0)
        self.assertEqual(contract.created, 1)
        self.assertEqual(contract.sent, 1)
        self.assertTrue(contract.deleted)
        self.assertFalse(contract.forgotten)
        self.assertEqual(records[0]["inbox_provider"], "mail.tm")

    def test_wrong_recipient_never_counts_as_delivery(self):
        contract = Contracts(wrong_recipient=True)
        code, records = contract.run()
        self.assertEqual(code, 1)
        self.assertEqual(records[0]["stage"], "inbox_delivery")
        self.assertTrue(contract.deleted and contract.forgotten)

    def test_cleanup_failure_stays_red_and_preserves_delivery_evidence(self):
        code, records = Contracts(cleanup_fails=True).run()
        self.assertEqual(code, 1)
        self.assertEqual(records[0]["status"], "delivered")
        self.assertEqual(records[0]["mailbox_cleanup"], "failed")
        self.assertEqual(records[1]["stage"], "mailbox_cleanup")

    def test_ambiguous_signup_is_never_repeated(self):
        contract = Contracts(signup_fails=True)
        code, records = contract.run()
        self.assertEqual(code, 1)
        self.assertEqual(contract.sent, 1)
        self.assertEqual(records[0]["stage"], "hosted_signup")
        self.assertTrue(contract.deleted and contract.forgotten)

    def test_two_broken_domains_cannot_spend_auth_mail(self):
        contract = Contracts(fallback_mx=False)
        code, records = contract.run()
        self.assertEqual(code, 1)
        self.assertEqual(contract.sent, 0)
        self.assertEqual(records[0]["stage"], "fallback_mailbox_setup")


if __name__ == "__main__":
    unittest.main()
