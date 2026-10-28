#!/usr/bin/env python3
"""Own a disposable public-auth tenant while running a benchmark command; never log credentials."""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import subprocess
import sys
import tempfile
import time
import urllib.parse
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "scripts" / "auth"))
from live_mail import AUTH, Failure, Forms, Http, Inbox

API = "https://api.terrascale.tech"
PLATFORM = AUTH + "/v1/terrascale"
HOSTED = AUTH + "/ui/terrascale"
OAUTH = AUTH + "/terrascale/oauth"
CLI = "00000000000000000000000007"


class DeletionAuthorization(urllib.request.BaseHandler):
    def __init__(self, save):
        self.save = save

    def https_response(self, request, response):
        if request.full_url == PLATFORM + "/account" and request.get_method() == "DELETE" and response.code in (202, 204):
            values = response.headers.get_all("X-TerraScale-Deletion-Authorization") or []
            if values:
                if len(values) != 1 or not re.fullmatch(r"[A-Za-z0-9_-]{43}", values[0]):
                    raise Failure("Deletion continuation authorization was malformed")
                self.save(values[0])
        return response


class Tenant:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.http = Http({AUTH, API})
        self.password = secrets.token_urlsafe(32)
        self.inbox = Inbox("https://api.mail.tm")
        self.inbox.checkpoint = self.checkpoint
        self.token = self.refresh = self.key = self.project = self.authorization = None
        self.renew_at = 0
        self.receipt = secrets.token_urlsafe(32)
        self.settled = False
        self.deletion_started = False
        self.http.opener.add_handler(DeletionAuthorization(self.retain_authorization))

    def retain_authorization(self, value):
        self.authorization = value
        self.checkpoint()

    def checkpoint(self):
        # Keep recovery material if cleanup fails; successful cleanup removes the complete private directory.
        state = dict(password=self.password, mail_address=self.inbox.address, mail_password=self.inbox.password,
                     mail_token=self.inbox.token, mail_account=self.inbox.account_id, token=self.token,
                     refresh=self.refresh, key=self.key, project=self.project, deletion_receipt=self.receipt,
                     deletion_started=self.deletion_started, deletion_authorization=self.authorization)
        path = self.directory / "recovery.json"
        with tempfile.NamedTemporaryFile(mode="w", dir=self.directory, delete=False) as stream:
            json.dump(state, stream)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(stream.name, path)

    def form(self, url, fields):
        return self.http.request(url, "POST", form=fields)

    def grant(self, fields):
        sent = time.monotonic()
        _, body, _ = self.form(OAUTH + "/token", fields)
        grant = json.loads(body)
        self.token = grant["access_token"]
        self.refresh = grant.get("refresh_token", self.refresh)
        self.renew_at = sent + max(0, grant["expires_in"] * 0.9)
        self.checkpoint()

    def bearer(self):
        if time.monotonic() >= self.renew_at:
            if not self.refresh:
                raise Failure("Synthetic user token expired without a refresh token")
            self.grant(dict(client_id=CLI, grant_type="refresh_token", refresh_token=self.refresh))
        return {"Authorization": "Bearer " + self.token}

    def platform(self, path, method="GET", payload=None, purpose=None, extra=None, expected=(200,)):
        headers = {**self.bearer(), **(extra or {})}
        status, body, location = self.http.request(PLATFORM + path, method, payload=payload, headers=headers,
                                                  expected=(*expected, 403))
        error = json.loads(body).get("error") if status == 403 else None
        code = error.get("code") if isinstance(error, dict) else error
        if status == 403 and purpose and code == "step_up_required":
            headers["X-TerraScale-Step-Up-Proof"] = self.proof(purpose)
            return self.http.request(PLATFORM + path, method, payload=payload, headers=headers, expected=expected)
        if status not in expected:
            raise Failure("Synthetic tenant platform operation was forbidden")
        return status, body, location

    def proof(self, purpose):
        _, body, _ = self.platform("/step-up", "POST", dict(purpose=purpose, audience="ts-auth",
            return_uri="https://dash.terrascale.tech/auth/step-up/callback"), expected=(201,))
        pending = json.loads(body)
        url = pending["step_up_url"]
        if urllib.parse.urlsplit(url).netloc != urllib.parse.urlsplit(AUTH).netloc:
            raise Failure("Step-up origin was unexpected")
        _, page, _ = self.http.request(url)
        fields = Forms(page).fields(urllib.parse.urlsplit(url).path)
        fields["password"] = self.password
        self.form(url, fields)  # Approval page only; do not follow its dashboard callback.
        return pending["proof"]

    def create(self):
        self.inbox.create()
        url = HOSTED + "/sign-up"
        _, page, _ = self.http.request(url)
        fields = Forms(page).fields("/ui/terrascale/sign-up")
        fields.update(email=self.inbox.address, password=self.password)
        _, page, _ = self.form(url, fields)
        def code(message):
            found = re.search(r"(?m)^\s*([0-9]{6})\s*$", message.get("text", ""))
            return found[1] if found else None
        value, _ = self.inbox.receive("signup", code, timeout=180)
        fields = Forms(page).fields("/ui/terrascale/sign-up/verify")
        fields["code"] = value
        self.form(url + "/verify", fields)
        _, body, _ = self.form(OAUTH + "/device", dict(client_id=CLI, scope="openid email profile offline_access"))
        device = json.loads(body)
        url = HOSTED + "/device?" + urllib.parse.urlencode(dict(user_code=device["user_code"]))
        _, page, _ = self.http.request(url)
        fields = Forms(page).fields("/ui/terrascale/device")
        fields.update(user_code=device["user_code"], decision="approve")
        self.form(HOSTED + "/device", fields)
        time.sleep(max(5, device.get("interval", 5)))
        self.grant(dict(client_id=CLI, grant_type="urn:ietf:params:oauth:grant-type:device_code", device_code=device["device_code"]))
        _, body, _ = self.platform("/organizations", "POST", {"name": "API overhead synthetic tenant"}, expected=(201,))
        organization = json.loads(body)["id"]
        _, body, _ = self.platform(f"/organizations/{organization}/projects", "POST", {"name": "api-overhead"}, expected=(201,))
        self.project = json.loads(body)["id"]
        path = f"/organizations/{organization}/projects/{self.project}/api-keys"
        _, body, _ = self.platform(path, "POST", dict(name="API overhead benchmark", scopes=["databases:read", "databases:write",
            "documents:read", "documents:write"]), purpose="create_api_key", expected=(201,))
        self.key = json.loads(body)["key"]
        self.checkpoint()
        return organization

    def close(self):
        status, _, _ = self.http.request(PLATFORM + "/account/deletion",
            headers={"X-TerraScale-Deletion-Receipt": self.receipt}, expected=(202, 204, 404))
        if status == 404:
            if not self.token:
                raise Failure("No user grant for cleanup; private recovery material retained")
            self.deletion_started = True
            self.checkpoint()
            status, _, _ = self.platform("/account", "DELETE", purpose="delete_account",
                extra={"X-TerraScale-Deletion-Receipt": self.receipt}, expected=(202, 204))
        elif status == 202 and self.authorization:
            status, _, _ = self.platform("/account", "DELETE", extra={"X-TerraScale-Deletion-Receipt": self.receipt,
                "X-TerraScale-Deletion-Authorization": self.authorization}, expected=(202, 204))
        deadline = time.monotonic() + 180
        while status == 202 and time.monotonic() < deadline:
            time.sleep(2)
            status, _, _ = self.http.request(PLATFORM + "/account/deletion",
                headers={"X-TerraScale-Deletion-Receipt": self.receipt}, expected=(202, 204))
        if status != 204:
            raise Failure("Account deletion did not settle; private recovery material retained")
        self.inbox.close()
        self.settled = True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plan-command", help="Quoted argv for an operator helper accepting this run's organization ULID; no shell evaluation")
    parser.add_argument("--cleanup-directory", type=Path, help="Resume closure using this run's retained private recovery file")
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not args.cleanup_directory and (not command or not args.plan_command):
        parser.error("a plan command and benchmark command are required")
    directory = args.cleanup_directory or tempfile.mkdtemp(prefix="api-overhead-")
    tenant = Tenant(directory)
    try:
        if args.cleanup_directory:
            state = json.loads((args.cleanup_directory / "recovery.json").read_text())
            tenant.password, tenant.token, tenant.refresh = state["password"], state["token"], state["refresh"]
            tenant.receipt, tenant.deletion_started = state["deletion_receipt"], state.get("deletion_started", False)
            tenant.authorization = state.get("deletion_authorization")
            tenant.key, tenant.project = state["key"], state["project"]
            tenant.inbox.address, tenant.inbox.password = state["mail_address"], state["mail_password"]
            tenant.inbox.token, tenant.inbox.account_id = state["mail_token"], state["mail_account"]
            return
        organization = tenant.create()
        subprocess.run([*shlex.split(args.plan_command), organization], check=True)
        credentials = Path(directory) / "benchmark.json"
        with open(credentials, "w", opener=lambda p, flags: os.open(p, flags, 0o600)) as stream:
            json.dump(dict(api_key=tenant.key, project=tenant.project), stream)
        subprocess.run(command, check=True, env={**os.environ, "TERRABASE_API_OVERHEAD_CREDENTIALS": str(credentials)})
    finally:
        try:
            tenant.close()
        finally:
            if tenant.settled:
                import shutil
                shutil.rmtree(directory)
                print("Synthetic account and inbox cleanup settled.", flush=True)
            else:
                print("Cleanup incomplete; recovery material retained in private directory: " + str(directory), file=sys.stderr)


if __name__ == "__main__":
    try:
        main()
    except (Failure, subprocess.CalledProcessError) as error:
        print("API overhead tenant run failed: " + (str(error) if isinstance(error, Failure) else "Child command failed"), file=sys.stderr)
        sys.exit(1)
