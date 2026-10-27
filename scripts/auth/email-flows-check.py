#!/usr/bin/env python3
"""Live email audit using isolated accounts and free disposable inboxes.

Run explicitly against production; each trigger is sent once. The JSON report
contains hashes and shape checks, never addresses, credentials or message text.
Unsupported flows remain uncovered and make the overall result incomplete.
"""
import argparse
import json
import os
import re
import secrets
import signal
import sys
import tempfile
import time
import urllib.parse
from http.cookiejar import Cookie

from live_mail import AUTH, DASHBOARD, Failure, Forms, Http, Inbox, digest, now

PLATFORM = AUTH + "/v1/terrascale"
HOSTED = AUTH + "/ui/terrascale"
CLI = "00000000000000000000000007"
UNAVAILABLE = {
    "email_change": "No email-change endpoint or delivery producer exists in current auth/dashboard source. Implement verified change in the owning services before live proof.",
    "magic_link": "Template exists without an actual product producer; implement a supported magic-link flow before proof.",
    "mfa_email_code": "Template exists; supported MFA uses authenticator/recovery codes, with no email-code producer.",
    "new_device": "Template exists without a sign-in notification producer; implement the actual detection and send path first.",
    "ownership_transfer_email": "Ownership transfer changes membership and audit state; no email producer exists.",
    "allowance_operations_80": "Requires an actual current-month usage observation at 80% of the free allowance; synthetic provider/template sends do not prove the producer. Do not spend quota or fabricate usage.",
    "allowance_operations_100": "Requires actual usage at 100% of the free allowance; no supported isolated event-injection API. Do not load production to force this threshold.",
    "allowance_storage": "Auth has a storage mail renderer but dashboard producer currently reserves operations alerts only. Implement storage observation before proof.",
    "support": "Actual recipient is the fixed support mailbox, not a disposable inbox. A support send would contact staff and cannot prove disposable-inbox delivery; use an approved support sandbox.",
    "support_acknowledgement": "Only emitted after a real support-mail send; cannot safely trigger independently of the fixed staff recipient.",
    "infrastructure_alerts_and_digest": "Supported operational relay has fixed real recipients; no isolated disposable-recipient trigger exists. Do not inject production alerts or change recipients.",
    "status_incident_and_maintenance": "Publishing a status event notifies real subscribers. Do not create production incidents or maintenance to force delivery.",
    "dashboard_inbox_notifications": "In-app notifications, including API-key expiry, have no email delivery producer; verify through the notification API separately.",
    "dashboard_alert_firing": "Requires a real measured project value exceeding an owner-created rule. No usage is fabricated and no production load is generated to force an alert; use an isolated metered project with approved synthetic traffic.",
    "dashboard_alert_resolved": "Requires the actual firing episode first, then a measured recovery. No synthetic direct email call proves this producer.",
}


class Audit:
    def __init__(self, output):
        self.output, self.http = output, Http({AUTH, DASHBOARD})
        self.inboxes, self.records, self.cleanup = [], [], []
        self.token, self.owner, self.org, self.project = None, None, None, None
        self.password, self.created, self.last_send = secrets.token_urlsafe(32), False, 0
        self.dashboard, self.stage = False, "setup"
        self.account_cleanup, self.deletion_receipt = "not_started", None
        self.state_path = output + ".private-state"

    @staticmethod
    def private_write(path, value, exclusive=False):
        directory = os.path.dirname(os.path.abspath(path))
        fd, temporary = tempfile.mkstemp(prefix=".email-audit-", dir=directory)
        try:
            with os.fdopen(fd, "w") as stream:
                json.dump(value, stream, indent=2)
                stream.flush()
                os.fsync(stream.fileno())
            if exclusive:
                os.link(temporary, path)  # Atomic claim: never overwrite another audit's recovery state.
            else:
                os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def checkpoint(self, exclusive=False):
        # Generated test credentials only; never copy production operator credentials here.
        cookie_fields = "version name value port port_specified domain domain_specified domain_initial_dot path path_specified secure expires discard comment comment_url rfc2109".split()
        self.private_write(self.state_path, {"started": self.started, "password": self.password, "token": self.token,
            "created": self.created, "dashboard": self.dashboard, "org": self.org, "project": self.project,
            "account_cleanup": self.account_cleanup, "deletion_receipt": self.deletion_receipt,
            "cleanup": self.cleanup, "flows": self.records, "http_observations": self.http.events,
            "inboxes": [{**{key: getattr(inbox, key) for key in ("provider", "token", "account_id", "address", "password", "closed")}, "seen": list(inbox.seen)} for inbox in self.inboxes],
            "cookies": [{**{key: getattr(cookie, key) for key in cookie_fields}, "rest": cookie._rest} for cookie in self.http.cookies]}, exclusive=exclusive)

    def save(self):
        report = {"started_at": self.started, "updated_at": now(), "status": "incomplete", "flows": self.records,
                  "cleanup": self.cleanup, "http_observations": self.http.events, "production_configuration_changes": [], "paid_resources_created": False}
        # Keep even the sanitized artifact private. Atomic replacement avoids partial JSON.
        self.checkpoint()
        self.private_write(self.output, report)

    def step(self, name, action):
        self.stage = name
        try:
            action()
        except Exception as error:
            self.records.append({"flow": name, "status": "failed", "reason": str(error) if isinstance(error, Failure) else type(error).__name__, "verified_at": now()})
        self.save()

    def mailbox(self):
        # Two independent free providers; never repeat an ambiguous create/send.
        for provider in ("https://api.mail.tm", "https://api.mail.gw"):
            inbox = Inbox(provider)
            self.inboxes.append(inbox)
            inbox.checkpoint = self.checkpoint
            try:
                return inbox.create()
            except Failure:
                if inbox.address:  # Creation may have succeeded: retain it for cleanup.
                    raise
        raise Failure("Both free inbox providers are unavailable")

    def form(self, url, fields, expected=(200,)):
        return self.http.request(url, "POST", form=fields, expected=expected)

    def api(self, path, method="GET", payload=None):
        if not self.token:
            raise Failure("Own sign-in access token is unavailable")
        status, body, _ = self.http.request(PLATFORM + path, method, payload=payload,
            headers={"Authorization": "Bearer " + self.token}, expected=(200, 201, 202, 204))
        return json.loads(body) if body else status

    def code(self, flow, inbox=None):
        inbox = inbox or self.owner
        def shape(message):
            body = message.get("text", "")
            match = re.search(r"(?m)^\s*([0-9]{6})\s*$", body)
            if match and match[1] in message.get("subject", "") and inbox.address in body:
                return match[1]
            return None
        value, evidence = inbox.receive(flow, shape)
        evidence["evidence"] = "Six-digit code in full text and subject; intended recipient in full text"
        self.records.append(evidence)
        self.save()
        return value

    def cooldown(self):
        remaining = 62 - (time.monotonic() - self.last_send)
        if remaining > 0:
            time.sleep(remaining)

    def signup(self):
        self.owner = self.mailbox()
        url = HOSTED + "/sign-up"
        _, page, _ = self.http.request(url)
        fields = Forms(page).fields("/ui/terrascale/sign-up")
        fields.update(email=self.owner.address, password=self.password)
        _, page, _ = self.form(url, fields)
        self.last_send = time.monotonic()
        fields = Forms(page).fields("/ui/terrascale/sign-up/verify")
        fields["code"] = self.code("signup_verification")
        # A lost response can still create the account. Retain cleanup credentials before redemption.
        self.created = True
        self.checkpoint()
        self.form(url + "/verify", fields)
        self.records[-1]["code_redeemed"] = self.has_session()
        if not self.has_session():
            raise Failure("Signup code did not establish hosted session")

    def has_session(self):
        return any(cookie.name == "tsauth_session" for cookie in self.http.cookies)

    def device(self):
        _, page, _ = self.form(AUTH + "/terrascale/oauth/device", {"client_id": CLI, "scope": "openid email profile offline_access"})
        device = json.loads(page)
        verification = HOSTED + "/device?" + urllib.parse.urlencode({"user_code": device["user_code"]})
        _, page, _ = self.http.request(verification)
        fields = Forms(page).fields("/ui/terrascale/device")
        fields.update(user_code=device["user_code"], decision="approve")
        self.form(HOSTED + "/device", fields)
        time.sleep(max(5, device.get("interval", 5)))
        _, page, _ = self.form(AUTH + "/terrascale/oauth/token", {"client_id": CLI, "grant_type": "urn:ietf:params:oauth:grant-type:device_code", "device_code": device["device_code"]})
        self.token = json.loads(page)["access_token"]
        self.checkpoint()
        self.api("/organizations")  # Server validates the live grant and identity.

    def signin(self):
        self.cooldown()
        _, page, _ = self.http.request(HOSTED + "/sign-in")
        fields = Forms(page).fields("/ui/terrascale/sign-in")
        fields["email"] = self.owner.address
        _, page, _ = self.form(HOSTED + "/sign-in", fields)
        self.last_send = time.monotonic()
        fields = Forms(page).fields("/ui/terrascale/sign-in/verify")
        fields["code"] = self.code("sign_in_code")
        self.form(HOSTED + "/sign-in/verify", fields)
        if not self.has_session():
            raise Failure("Code redemption did not establish a hosted session")
        self.records[-1]["code_redeemed"] = True

    def existing_signup(self):
        self.cooldown()
        url = HOSTED + "/sign-up"
        _, page, _ = self.http.request(url)
        fields = Forms(page).fields("/ui/terrascale/sign-up")
        fields.update(email=self.owner.address, password=secrets.token_urlsafe(32))
        _, page, _ = self.form(url, fields)
        self.last_send = time.monotonic()
        fields = Forms(page).fields("/ui/terrascale/sign-up/verify")
        fields["code"] = self.code("existing_account_signup_signin_code")
        self.form(url + "/verify", fields)
        if not self.has_session():
            raise Failure("Code redemption did not establish a hosted session")
        self.records[-1]["code_redeemed"] = True

    def tenant_flows(self):
        if not self.project:
            raise Failure("Isolated project was not created")
        inbox, password = self.mailbox(), secrets.token_urlsafe(32)
        hosted = AUTH + "/ui/" + self.project
        last_send = 0
        for flow, route, verify in (("project_tenant_signup_verification", "sign-up", "sign-up/verify"),
                                    ("project_tenant_sign_in_code", "sign-in", "sign-in/verify"),
                                    ("project_tenant_password_reset", "reset", "reset/complete")):
            delay = 62 - (time.monotonic() - last_send)
            if delay > 0:
                time.sleep(delay)
            _, page, _ = self.http.request(hosted + "/" + route)
            fields = Forms(page).fields("/ui/" + self.project + "/" + route)
            fields["email"] = inbox.address
            if route == "sign-up":
                fields["password"] = password
            _, page, _ = self.form(hosted + "/" + route, fields)
            last_send = time.monotonic()
            fields = Forms(page).fields("/ui/" + self.project + "/" + verify)
            fields["code"] = self.code(flow, inbox)
            if route == "reset":
                password = secrets.token_urlsafe(32)
                fields["password"] = password
            self.form(hosted + "/" + verify, fields)
            if route == "reset":
                self.form(hosted + "/sign-in/password", {"email": inbox.address, "password": password})
            # An ownership-checked API proves this actual tenant has exactly our synthetic user.
            _, body, _ = self.http.request(AUTH + "/v1/" + self.project + "/users", headers={"Authorization": "Bearer " + self.token})
            users = json.loads(body)["items"]
            if len(users) != 1 or users[0]["email"] != inbox.address:
                raise Failure("Isolated tenant identity could not be confirmed")
            if not any(cookie.name == "tsauth_session_" + self.project for cookie in self.http.cookies):
                raise Failure("Project tenant code did not establish a hosted session")
            self.records[-1]["code_redeemed"] = True
            self.save()

    def invitations(self):
        # Identity and ownership APIs create resources owned only by this new synthetic user.
        self.org = self.api("/organizations", "POST", {"name": "Email audit " + secrets.token_hex(6)})["id"]
        self.checkpoint()
        self.project = self.api("/organizations/" + self.org + "/projects", "POST", {"name": "Email audit project"})["id"]
        self.checkpoint()
        invitee = self.mailbox()
        for flow, project in (("organization_invitation", None), ("project_invitation", self.project)):
            payload = {"email": invitee.address, "role": "viewer"}
            if project:
                payload["project_id"] = project
            created = self.api("/organizations/" + self.org + "/invitations", "POST", payload)
            invitation, token = created["invitation"]["id"], created["token"]
            try:
                def shape(message):
                    text = message.get("text", "") + " ".join(message.get("html", []))
                    return True if "/invitations/" + token in text else None
                _, evidence = invitee.receive(flow, shape)
                evidence["evidence"] = "Full message contains the exact product-generated invitation link"
                self.records.append(evidence)
                self.save()
            finally:
                self.api("/organizations/" + self.org + "/invitations/" + invitation, "DELETE")

    def dashboard_session(self):
        url, page = self.http.follow(DASHBOARD + "/login?returnTo=%2Fprofile")
        if url.startswith(AUTH):
            # Hosted authentication renders an explicit continue link after setting its cookie.
            link = next((link for link in Forms(page).links if link.startswith("/terrascale/oauth/authorize")), None)
            if link:
                url, page = self.http.follow(AUTH + link)
        if not url.startswith(DASHBOARD):
            raise Failure("Dashboard OIDC continuation is incomplete")
        _, context, _ = self.http.request(DASHBOARD + "/api/v1/dashboard/context")
        if json.loads(context)["user"]["email"] != self.owner.address:
            raise Failure("Dashboard session does not belong to the synthetic user")
        self.dashboard = True
        self.records.append({"flow": "dashboard_session", "status": "authenticated", "verified_at": now()})

    def lockout(self):
        # Intentionally exhaust only this isolated account's code-redemption budget (5/15 min).
        # The method lockout is independent of password recovery and never targets another user.
        _, page, _ = self.http.request(HOSTED + "/sign-in")
        fields = Forms(page).fields("/ui/terrascale/sign-in")
        fields.update(email=self.owner.address, code="invalid")
        for _ in range(6):
            status, _, _ = self.form(HOSTED + "/sign-in/verify", fields, expected=(401, 429))
            if status == 429:
                break
        if status != 429:
            raise Failure("Isolated account did not reach documented lockout")
        def shape(message):
            text = message.get("text", "").lower()
            return True if "temporar" in text and "utc" in text and not re.search(r"(?m)^\s*[0-9]{6}\s*$", text) else None
        _, evidence = self.owner.receive("lockout_notice", shape)
        evidence["evidence"] = "Temporary authentication pause with UTC deadline, without a passcode"
        self.records.append(evidence)

    def reset(self):
        self.cooldown()
        _, page, _ = self.http.request(HOSTED + "/reset")
        fields = Forms(page).fields("/ui/terrascale/reset")
        fields["email"] = self.owner.address
        _, page, _ = self.form(HOSTED + "/reset", fields)
        self.last_send = time.monotonic()
        fields = Forms(page).fields("/ui/terrascale/reset/complete")
        fields.update(code=self.code("password_reset"), password=secrets.token_urlsafe(32))
        self.password = fields["password"]
        self.checkpoint()  # A completed reset must never leave only the old password in recovery state.
        self.form(HOSTED + "/reset/complete", fields)
        self.token = None  # Reset invalidates previous tokens and cookies.
        _, page, _ = self.http.request(HOSTED + "/sign-in")
        fields = Forms(page).fields("/ui/terrascale/sign-in")
        fields.update(email=self.owner.address, password=self.password)
        self.form(HOSTED + "/sign-in/password", fields)
        if not self.has_session():
            raise Failure("New password did not establish a hosted session")
        self.records[-1]["code_redeemed_and_new_password_authenticated"] = True

    def method_lockout(self, method):
        route, maximum = ("sign-in/password", 11) if method == "password" else ("reset/complete", 6)
        fields = {"email": self.owner.address, "password": secrets.token_urlsafe(32)}
        if method != "password":
            fields["code"] = "invalid"
        for _ in range(maximum):
            status, _, _ = self.form(HOSTED + "/" + route, fields, expected=(401, 429))
            if status == 429:
                break
        if status != 429:
            raise Failure("Isolated account did not reach documented method lockout")
        def shape(message):
            text = message.get("text", "").lower()
            return True if "temporar" in text and "utc" in text and not re.search(r"(?m)^\s*[0-9]{6}\s*$", text) else None
        _, evidence = self.owner.receive(method + "_lockout_notice", shape)
        evidence["evidence"] = "Documented method budget exhausted for owned synthetic account; fresh full-message temporary pause with UTC deadline"
        self.records.append(evidence)

    def finish(self):
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        if any(item["resource"] == "synthetic_account_and_owned_resources" and item["status"] == "deleted" for item in self.cleanup):
            self.account_cleanup, self.created = "deleted", False
        self.cleanup = [item for item in self.cleanup if item["resource"] not in ("synthetic_account_and_owned_resources", "disposable_inbox")]
        # Supported deletion is scoped to a newly-created user and the resources it alone owns.
        if self.account_cleanup == "deleted":
            self.cleanup.append({"resource": "synthetic_account_and_owned_resources", "status": "deleted"})
        elif self.created:
            try:
                self.delete_account()
                self.account_cleanup, self.created = "deleted", False
                self.cleanup.append({"resource": "synthetic_account_and_owned_resources", "status": "deleted"})
                self.checkpoint()  # Do not require a deleted account's credentials on a later inbox retry.
            except Exception as error:
                self.cleanup.append({"resource": "synthetic_account_and_owned_resources", "status": "failed", "reason": str(error) if isinstance(error, Failure) else type(error).__name__})
        for inbox in reversed(self.inboxes):
            try:
                inbox.close()
                self.cleanup.append({"resource": "disposable_inbox", "status": "deleted"})
                self.checkpoint()
            except Exception:
                self.cleanup.append({"resource": "disposable_inbox", "status": "failed", "reason": "Inbox deletion could not be confirmed"})
        self.save()
        if all(item["status"] in ("deleted", "not_created") for item in self.cleanup):
            os.unlink(self.state_path)

    def delete_account(self):
        if self.account_cleanup == "dashboard_pending":
            # The saved receipt cookie continues this deletion even after sign-in is revoked.
            _, page, _ = self.http.request(DASHBOARD + "/account-deletion", expected=(202,))
            fields = Forms(page).fields("/api/v1/dashboard/profile/delete/status")
            status, _, location = self.form(DASHBOARD + "/api/v1/dashboard/profile/delete/status", fields, expected=(202, 302, 303))
            self.checkpoint()
            if status not in (302, 303) or location != "/account-deleted":
                raise Failure("Synthetic account deletion remains pending")
            return
        if self.account_cleanup == "auth_pending":
            status, _, _ = self.http.request(PLATFORM + "/account/deletion",
                headers={"X-TerraScale-Deletion-Receipt": self.deletion_receipt}, expected=(202, 204))
            if status != 204:
                raise Failure("Synthetic account deletion remains pending")
            return
        if self.dashboard:
            self.dashboard_session()  # Re-open after reset without spending another email.
            _, page, _ = self.http.request(DASHBOARD + "/profile?tab=danger")
            fields = Forms(page).fields("/api/v1/dashboard/profile/delete")
            fields["email"] = self.owner.address
            self.account_cleanup = "dashboard_pending"
            self.checkpoint()  # Preserve the original session cookie before an ambiguous deletion POST.
            _, _, location = self.form(DASHBOARD + "/api/v1/dashboard/profile/delete", fields, expected=(302, 303))
            self.checkpoint()  # Preserve the receipt cookie even if completion is still pending.
            if location != "/account-deleted":
                raise Failure("Synthetic account deletion remains pending")
        else:
            if not self.token:
                raise Failure("Own sign-in access token is unavailable")
            self.deletion_receipt = secrets.token_urlsafe(32)
            self.account_cleanup = "auth_pending"
            self.checkpoint()
            status, _, _ = self.http.request(PLATFORM + "/account", "DELETE",
                headers={"Authorization": "Bearer " + self.token, "X-TerraScale-Deletion-Receipt": self.deletion_receipt}, expected=(202, 204))
            if status != 204:
                raise Failure("Synthetic account deletion remains pending")

    def run(self):
        self.started = now()
        self.checkpoint(exclusive=True)
        try:
            self.step("signup_verification", self.signup)
            if self.created:
                self.step("sign_in_code", self.signin)
                self.step("existing_account_signup_signin_code", self.existing_signup)
                self.step("dashboard_session", self.dashboard_session)
                if self.dashboard:
                    from live_mail import dashboard_flows
                    self.step("dashboard_additional_flows", lambda: dashboard_flows(self))
                else:
                    for flow in ("organization_invitation", "project_invitation", "organization_invitation_resend", "project_invitation_resend", "project_tenant_signup_verification", "project_tenant_sign_in_code", "project_tenant_password_reset"):
                        self.records.append({"flow": flow, "status": "uncovered", "reason": "A legitimate signed-in session could not complete. Protected proofs remain pending until sign-in is confirmed healthy; then retry the supported product path."})
                self.step("lockout_notice", self.lockout)
                self.step("password_reset", self.reset)
                self.step("password_lockout_notice", lambda: self.method_lockout("password"))
                self.step("reset_lockout_notice", lambda: self.method_lockout("reset"))
                from live_mail import status_confirmation
                self.step("status_subscription_confirmation", lambda: status_confirmation(self))
        except KeyboardInterrupt:
            self.records.append({"flow": self.stage, "status": "interrupted", "reason": "Process signal received; supported cleanup attempted"})
        finally:
            covered = {item["flow"] for item in self.records}
            for flow, reason in UNAVAILABLE.items():
                if flow not in covered:
                    self.records.append({"flow": flow, "status": "uncovered", "reason": reason})
            self.finish()
        return 1  # Never represent a partial audit as an all-flows pass.

    def load_checkpoint(self):
        with open(self.state_path) as stream:
            state = json.load(stream)
        try:
            with open(self.output) as stream:
                report = json.load(stream)
        except FileNotFoundError:
            report = {}
        self.http.events = state.get("http_observations", report.get("http_observations", []))
        self.started = state["started"]
        self.records, self.cleanup = state.get("flows", report.get("flows", [])), state.get("cleanup", report.get("cleanup", []))
        self.account_cleanup, self.deletion_receipt = state.get("account_cleanup", "not_started"), state.get("deletion_receipt")
        for key in ("password", "token", "created", "dashboard", "org", "project"):
            setattr(self, key, state[key])
        for values in state["inboxes"]:
            inbox = Inbox(values["provider"])
            for key, value in values.items():
                setattr(inbox, key, set(value) if key == "seen" else value)
            inbox.seen_hashes = {item["message_id_sha256"] for item in self.records if item.get("recipient_sha256") == digest(inbox.address or "") and item.get("message_id_sha256")}
            inbox.checkpoint = self.checkpoint
            self.inboxes.append(inbox)
        self.owner = next((inbox for inbox in self.inboxes if inbox.address), None)
        for cookie in state["cookies"]:
            self.http.cookies.set_cookie(Cookie(**cookie))

    def cleanup_checkpoint(self):
        self.load_checkpoint()
        if any(item["resource"] == "synthetic_account_and_owned_resources" and item["status"] == "deleted" for item in self.cleanup):
            self.account_cleanup, self.created = "deleted", False
        if self.created and self.account_cleanup == "not_started" and not self.dashboard and not self.token:
            self.step("cleanup_dashboard_session", self.dashboard_session)
        self.finish()
        return int(any(item["status"] not in ("deleted", "not_created") for item in self.cleanup))

    def resume_dashboard(self):
        self.load_checkpoint()
        if self.account_cleanup != "not_started" or any(item["resource"] == "synthetic_account_and_owned_resources" and item["status"] == "deleted" for item in self.cleanup):
            raise Failure("Account cleanup already started; use cleanup-state instead")
        try:
            self.step("dashboard_session", self.dashboard_session)
            if self.dashboard:
                from live_mail import dashboard_flows
                self.step("dashboard_additional_flows", lambda: dashboard_flows(self))
        except KeyboardInterrupt:
            self.records.append({"flow": self.stage, "status": "interrupted"})
        finally:
            covered = {item["flow"] for item in self.records}
            for flow, reason in UNAVAILABLE.items():
                if flow not in covered:
                    self.records.append({"flow": flow, "status": "uncovered", "reason": reason})
            self.finish()
        return 1


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", default="/tmp/task-email-proof/all-flows.json")
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--cleanup-state", action="store_true", help="Retry only supported cleanup using this report's private generated-credential checkpoint")
    mode.add_argument("--resume-dashboard", action="store_true", help="Continue the existing synthetic account through normal dashboard OIDC, without creating another signup")
    args = parser.parse_args()
    directory = os.path.dirname(os.path.abspath(args.output))
    os.makedirs(directory, mode=0o700, exist_ok=True)
    if os.stat(directory).st_mode & 0o077:
        parser.error("The report directory must already be private (mode 700)")
    def interrupted(signum, frame):
        raise KeyboardInterrupt
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    audit = Audit(args.output)
    if not (args.cleanup_state or args.resume_dashboard) and (os.path.lexists(audit.state_path) or os.path.lexists(audit.output)):
        parser.error("Existing audit evidence or recovery state must be preserved; resume cleanup or choose a new output path")
    sys.exit(audit.cleanup_checkpoint() if args.cleanup_state else audit.resume_dashboard() if args.resume_dashboard else audit.run())
