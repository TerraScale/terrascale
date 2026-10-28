#!/usr/bin/env python3
"""Measure live ts-auth flow latency with one synthetic account, then delete it.

Covers hosted signup (send, inbox arrival, verify), device grant, refresh,
userinfo, platform reads, sign-in code, revoke and account deletion. Prints a
Markdown table of per-step wall time; never prints codes, tokens or addresses.
"""
import argparse
import base64
import json
import statistics
import sys
import time
import urllib.parse

from live_mail import AUTH, DASHBOARD, Failure, Forms, Http, Inbox

PLATFORM = AUTH + "/v1/terrascale"
HOSTED = AUTH + "/ui/terrascale"
OAUTH = AUTH + "/terrascale/oauth"
CLI = "00000000000000000000000007"


def code_from(inbox):
    import re

    def shape(message):
        match = re.search(r"(?m)^\s*([0-9]{6})\s*$", message.get("text", ""))
        return (match[1], message.get("createdAt")) if match else None
    (code, created), _ = inbox.receive("code", shape, timeout=180)
    return code, created


class Run:
    def __init__(self):
        self.http, self.timings, self.token, self.refresh = Http({AUTH}), {}, None, None

    def timed(self, name, action):
        started = time.monotonic()
        try:
            return action()
        finally:
            self.timings[name] = round(time.monotonic() - started, 3)

    def form(self, url, fields, expected=(200,)):
        return self.http.request(url, "POST", form=fields, expected=expected)

    def bearer(self, path, method="GET", expected=(200,)):
        return self.http.request(PLATFORM + path, method, headers={"Authorization": "Bearer " + self.token}, expected=expected)

    def email_step(self, prefix, page_url, extra):
        _, page, _ = self.timed(prefix + " GET page", lambda: self.http.request(page_url))
        fields = Forms(page).fields(urllib.parse.urlsplit(page_url).path)
        fields.update(extra)
        _, page, _ = self.timed(prefix + " POST send", lambda: self.form(page_url, fields))
        started = time.monotonic()
        code, _ = code_from(self.inbox)
        self.timings[prefix + " email arrival (inbox poll, 3 s grain)"] = round(time.monotonic() - started, 1)
        fields = Forms(page).fields(urllib.parse.urlsplit(page_url).path + "/verify")
        fields["code"] = code
        self.timed(prefix + " POST verify", lambda: self.form(page_url + "/verify", fields))

    def run(self):
        import secrets
        self.inbox = Inbox("https://api.mail.tm").create()
        try:
            self.password = secrets.token_urlsafe(24)
            self.email_step("signup", HOSTED + "/sign-up", {"email": self.inbox.address, "password": self.password})
            self.device_token(timed=True)
            self.timed("GET /oauth/userinfo", lambda: self.http.request(OAUTH + "/userinfo", headers={"Authorization": "Bearer " + self.token}))
            self.timed("GET /v1/terrascale/organizations", lambda: self.bearer("/organizations"))
            self.timed("GET /v1/terrascale/organizations (warm)", lambda: self.bearer("/organizations"))
            if self.refresh:
                _, body, _ = self.timed("token refresh_token grant", lambda: self.form(OAUTH + "/token", {"client_id": CLI, "grant_type": "refresh_token", "refresh_token": self.refresh}))
                grant = json.loads(body)
                self.token, self.refresh = grant["access_token"], grant.get("refresh_token", self.refresh)
                self.timed("GET /v1/terrascale/organizations (refreshed)", lambda: self.bearer("/organizations"))
            time.sleep(62)  # Send cooldown.
            self.http.cookies.clear()
            self.email_step("signin", HOSTED + "/sign-in", {"email": self.inbox.address})
        finally:
            self.delete()
        return self.timings

    def device_token(self, timed):
        clock = self.timed if timed else (lambda _, action: action())
        _, body, _ = clock("device POST /oauth/device", lambda: self.form(OAUTH + "/device", {"client_id": CLI, "scope": "openid email profile offline_access"}))
        device = json.loads(body)
        verification = HOSTED + "/device?" + urllib.parse.urlencode({"user_code": device["user_code"]})
        _, page, _ = clock("device GET approve page", lambda: self.http.request(verification))
        fields = Forms(page).fields("/ui/terrascale/device")
        fields.update(user_code=device["user_code"], decision="approve")
        clock("device POST approve", lambda: self.form(HOSTED + "/device", fields))
        time.sleep(max(5, device.get("interval", 5)))
        _, body, _ = clock("token device_code grant", lambda: self.form(OAUTH + "/token", {"client_id": CLI, "grant_type": "urn:ietf:params:oauth:grant-type:device_code", "device_code": device["device_code"]}))
        grant = json.loads(body)
        self.token, self.refresh = grant["access_token"], grant.get("refresh_token")

    def delete(self):
        import secrets
        for _ in range(3):
            if self.token or not any(cookie.name == "tsauth_session" for cookie in self.http.cookies):
                break
            try:
                self.device_token(timed=False)  # The measured grant failed: get a token through the live session to clean up.
            except Failure:
                time.sleep(5)
        if not self.token:
            print("cleanup: synthetic account may remain; inbox kept", file=sys.stderr)
            return  # Keep the inbox: the account may exist and can only be recovered through it.
        # Deletion needs a fresh purpose-bound step-up proof (docs/auth/step-up-proof.md): re-enter the password on the hosted page.
        _, body, _ = self.http.request(PLATFORM + "/step-up", "POST", headers={"Authorization": "Bearer " + self.token}, expected=(201,),
            payload={"purpose": "delete_account", "audience": "ts-auth", "return_uri": DASHBOARD + "/auth/step-up/callback"})
        step = json.loads(body)
        _, page, _ = self.http.request(step["step_up_url"])
        action = urllib.parse.urlsplit(step["step_up_url"]).path
        fields = Forms(page).fields(action)
        fields["password"] = self.password
        self.form(AUTH + action, fields, expected=(200, 302, 303))
        receipt = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode()
        status, _, _ = self.timed("DELETE /v1/terrascale/account", lambda: self.http.request(PLATFORM + "/account", "DELETE", headers={"Authorization": "Bearer " + self.token, "X-TerraScale-Deletion-Receipt": receipt, "X-TerraScale-Step-Up-Proof": step["proof"]}, expected=(202, 204)))
        deadline = time.monotonic() + 120
        while status == 202 and time.monotonic() < deadline:
            time.sleep(2)
            status, _, _ = self.http.request(PLATFORM + "/account/deletion", headers={"X-TerraScale-Deletion-Receipt": receipt}, expected=(202, 204))
        self.timings["account deletion settled"] = status == 204
        if status == 204:
            self.inbox.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repeats", type=int, default=3)
    parser.add_argument("--output", default="/tmp/ts-auth-latency.json")
    args = parser.parse_args()
    runs = []
    for index in range(args.repeats):
        run = Run()
        try:
            runs.append(run.run())
        except Failure as error:
            runs.append({**run.timings, "failure": str(error), "last": run.http.events[-1:] if run.http.events else []})
        print(f"run {index + 1}: {json.dumps(runs[-1])}", file=sys.stderr, flush=True)
        with open(args.output, "w") as handle:
            json.dump(runs, handle, indent=1)
    names = list(dict.fromkeys(name for run in runs for name in run))
    print("| step | " + " | ".join(f"run {i + 1}" for i in range(len(runs))) + " | median |")
    print("|---" * (len(runs) + 2) + "|")
    for name in names:
        values = [run.get(name) for run in runs]
        numbers = [v for v in values if isinstance(v, (int, float)) and not isinstance(v, bool)]
        median = round(statistics.median(numbers), 3) if numbers else ""
        print(f"| {name} | " + " | ".join("" if v is None else str(v) for v in values) + f" | {median} |")


if __name__ == "__main__":
    main()
