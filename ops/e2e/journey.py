#!/usr/bin/env python3
import argparse
import base64
import email
import email.header
import email.utils
import http.cookiejar
import imaplib
import json
import os
import re
import secrets
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from html.parser import HTMLParser


class JourneyError(Exception):
    pass


CODE_SUBJECT = re.compile(r"^(\d{6}) is your TerraScale")
CODE_BODY = re.compile(r"(?m)^\s*([0-9]{6})\s*$")
CLI_CLIENT_ID = "00000000000000000000000007"
SCOPES = ["databases:read", "databases:write", "sql:read", "sql:write"]


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


class Forms(HTMLParser):
    def __init__(self, page):
        super().__init__()
        self.forms, self.current = [], None
        self.feed(page)

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag == "form":
            self.current = {"action": attrs.get("action", ""), "fields": {}}
            self.forms.append(self.current)
        elif tag == "input" and self.current is not None and attrs.get("name"):
            self.current["fields"][attrs["name"]] = attrs.get("value", "")

    def handle_endtag(self, tag):
        if tag == "form":
            self.current = None

    def fields(self, action):
        for form in self.forms:
            if form["action"] == action:
                return dict(form["fields"])
        raise JourneyError("Expected hosted form is missing: " + action)


class HttpClient:
    def __init__(self):
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(
            NoRedirect(), urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, url, method="GET", payload=None, form=None, headers=None, expected=(200,)):
        headers = dict(headers or {})
        data = None
        if form is not None:
            data = urllib.parse.urlencode(form).encode()
            origin = urllib.parse.urlsplit(url)
            headers.update({"Content-Type": "application/x-www-form-urlencoded",
                            "Origin": origin.scheme + "://" + origin.netloc,
                            "Referer": url, "Accept-Language": "en"})
        elif payload is not None:
            data = json.dumps(payload).encode()
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request(url, data, headers, method=method)
        try:
            response = self.opener.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        except (urllib.error.URLError, TimeoutError, OSError):
            raise JourneyError("HTTP transport failed: " + method + " " + redact(url)) from None
        with response:
            status = response.code
            body = response.read().decode("utf-8", "replace")
        if status not in expected:
            raise JourneyError("HTTP status {} for {} {}".format(status, method, redact(url)))
        return status, body


def redact(url):
    parts = urllib.parse.urlsplit(url)
    return parts.scheme + "://" + parts.netloc + re.sub(r"/[A-Za-z0-9_-]{20,}", "/:id", parts.path)


def imap_code(host, user, password_file, address, since, wait):
    try:
        with open(password_file, "rb") as handle:
            password = handle.read().decode().strip()
    except OSError:
        raise JourneyError("IMAP secret file is unreadable") from None
    try:
        box = imaplib.IMAP4_SSL(host, 993, ssl_context=ssl.create_default_context(), timeout=20)
        box.login(user, password)
        password = None
        try:
            box.select("INBOX", readonly=True)
            deadline = time.monotonic() + wait
            while time.monotonic() < deadline:
                box.noop()
                status, data = box.uid("search", None, "TO", '"' + address + '"')
                uids = sorted((int(uid) for uid in data[0].split()), reverse=True)[:10] if status == "OK" else []
                for uid in uids:
                    _, raw = box.uid("fetch", str(uid), "(BODY.PEEK[])")
                    message = email.message_from_bytes(raw[0][1])
                    subject = str(email.header.make_header(email.header.decode_header(message.get("Subject", ""))))
                    match = CODE_SUBJECT.match(subject)
                    text = ""
                    if message.is_multipart():
                        for part in message.walk():
                            if part.get_content_type() == "text/plain":
                                text += part.get_payload(decode=True).decode("utf-8", "replace")
                    else:
                        text = message.get_payload(decode=True).decode("utf-8", "replace")
                    if not match:
                        body = CODE_BODY.search(text)
                        if body and body.group(1) in subject:
                            match = body
                    if match and email.utils.parsedate_to_datetime(message.get("Date")) >= since:
                        return match.group(1)
                time.sleep(3)
            raise JourneyError("No sign-in code arrived within {} seconds".format(wait))
        finally:
            box.logout()
    except imaplib.IMAP4.error:
        raise JourneyError("IMAP login failed") from None


class Journey:
    def __init__(self, options):
        self.options = options
        self.http = HttpClient()
        self.steps = []
        self.started = time.monotonic()
        self.since = datetime.now(timezone.utc)
        self.password = secrets.token_urlsafe(32)
        self.user_token = None
        self.api_key = None
        self.key_id = None
        self.access_token = None
        self.org_id = None
        self.project_id = None
        self.database_name = None
        self.deletion_receipt = None
        self.first_query_ms = None

    def auth(self, path):
        return self.options.auth_url.rstrip("/") + path

    def api(self, path):
        return self.options.base_url.rstrip("/") + path

    def form(self, path, fields):
        return self.http.request(self.auth(path), "POST", form=fields)

    def bearer(self, path, method="GET", payload=None, extra=None):
        headers = {"Authorization": "Bearer " + self.user_token}
        headers.update(extra or {})
        return self.http.request(self.auth(path), method, payload=payload, headers=headers, expected=(200, 201, 202, 204))

    def key_token_bearer(self, path, method="GET", payload=None, expected=(200, 201, 202, 204)):
        headers = {"Authorization": "Bearer " + self.access_token}
        return self.http.request(self.api(path), method, payload=payload, headers=headers, expected=expected)

    def run(self, name, action):
        if self.options.dry_run:
            return None
        mark = time.monotonic()
        try:
            result = action()
        except JourneyError as error:
            self.steps.append((name, False, int((time.monotonic() - mark) * 1000), str(error)))
            raise
        self.steps.append((name, True, int((time.monotonic() - mark) * 1000), ""))
        return result

    def sign_up(self):
        _, page = self.http.request(self.auth("/ui/terrascale/sign-up"))
        fields = Forms(page).fields("/ui/terrascale/sign-up")
        fields.update(email=self.options.email, password=self.password)
        _, page = self.form("/ui/terrascale/sign-up", fields)
        fields = Forms(page).fields("/ui/terrascale/sign-up/verify")
        code = self.wait_code()
        fields.update(code=code)
        self.form("/ui/terrascale/sign-up/verify", fields)
        if not any(cookie.name == "tsauth_session" for cookie in self.http.cookies):
            raise JourneyError("Sign-up did not establish a hosted session")

    def wait_code(self):
        return imap_code(self.options.imap_host, self.options.imap_user, self.options.imap_secret_file,
                         self.options.email, self.since, 180)

    def user_access_token(self):
        _, body = self.form("/terrascale/oauth/device",
                            {"client_id": CLI_CLIENT_ID, "scope": "openid email profile offline_access"})
        device = json.loads(body)
        _, page = self.http.request(
            self.auth("/ui/terrascale/device?" + urllib.parse.urlencode({"user_code": device["user_code"]})))
        fields = Forms(page).fields("/ui/terrascale/device")
        fields.update(user_code=device["user_code"], decision="approve")
        self.form("/ui/terrascale/device", fields)
        time.sleep(max(5, device.get("interval", 5)))
        _, body = self.form("/terrascale/oauth/token", {"client_id": CLI_CLIENT_ID,
                                                        "grant_type": "urn:ietf:params:oauth:grant-type:device_code",
                                                        "device_code": device["device_code"]})
        self.user_token = json.loads(body)["access_token"]

    def create_org(self):
        _, body = self.bearer("/v1/terrascale/organizations", "POST",
                              payload={"name": self.options.prefix + "org"})
        self.org_id = json.loads(body)["id"]

    def create_project(self):
        _, body = self.bearer("/v1/terrascale/organizations/" + self.org_id + "/projects", "POST",
                              payload={"name": self.options.prefix + "project"})
        self.project_id = json.loads(body)["id"]

    def create_key(self):
        _, body = self.bearer("/v1/terrascale/step-up", "POST", payload={
            "purpose": "create_api_key", "audience": "ts-auth",
            "return_uri": "https://dash.terrascale.tech/auth/step-up/callback"})
        challenge = json.loads(body)
        _, page = self.http.request(self.auth(challenge["step_up_url"]))
        fields = Forms(page).fields(urllib.parse.urlsplit(challenge["step_up_url"]).path)
        code = self.wait_code()
        fields.update(code=code)
        self.http.request(self.auth(challenge["step_up_url"]), "POST", form=fields, expected=(200, 302, 303))
        for _ in range(6):
            try:
                _, body = self.bearer("/v1/terrascale/organizations/" + self.org_id + "/projects/"
                                      + self.project_id + "/api-keys", "POST",
                                      payload={"name": self.options.prefix + "key", "scopes": SCOPES},
                                      extra={"X-TerraScale-Step-Up-Proof": challenge["proof"]})
                created = json.loads(body)
                self.api_key, self.key_id = created["key"], created["api_key"]["id"]
                return
            except JourneyError:
                time.sleep(5)
        raise JourneyError("API key creation was not confirmed by its step-up deadline")

    def exchange_token(self):
        key_id = self.api_key.split("_", 1)[0]
        request = urllib.request.Request(self.auth("/terrascale/oauth/token"),
                                         data=urllib.parse.urlencode({"grant_type": "client_credentials"}).encode(),
                                         method="POST")
        request.add_header("Authorization", "Basic " + base64.b64encode(
            (key_id + ":" + self.api_key).encode()).decode())
        request.add_header("Content-Type", "application/x-www-form-urlencoded")
        try:
            response = self.http.opener.open(request, timeout=30)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            status, body = response.code, response.read().decode()
        if status != 200:
            raise JourneyError("Token exchange failed with status " + str(status))
        self.access_token = json.loads(body)["access_token"]

    def create_database(self):
        payload = {"name": self.options.prefix + "db"}
        if self.options.region:
            payload["homeRegion"] = self.options.region
        _, body = self.key_token_bearer("/v1/projects/" + self.project_id + "/databases", "POST", payload=payload)
        self.database_name = json.loads(body)["name"]

    def sql_round_trip(self):
        _, body = self.key_token_bearer(
            "/v1/projects/" + self.project_id + "/databases/" + self.database_name + "/sql/execute", "POST",
            payload={"statements": [
                {"sql": "CREATE TABLE journey (id INTEGER, label TEXT)"},
                {"sql": "INSERT INTO journey VALUES (1, 'e2e')"},
                {"sql": "SELECT id, label FROM journey"}]})
        results = json.loads(body)["results"]
        if not results or results[-1]["rows"] != [[1, "e2e"]]:
            raise JourneyError("SQL round trip returned an unexpected result")
        self.first_query_ms = int((time.monotonic() - self.started) * 1000)

    def teardown(self):
        failures = []
        plan = [
            ("database", self.database_name is not None, lambda: self.key_token_bearer(
                "/v1/projects/" + self.project_id + "/databases/" + self.database_name, "DELETE",
                expected=(200, 202, 204))),
            ("key", self.key_id is not None, lambda: self.bearer(
                "/v1/terrascale/organizations/" + self.org_id + "/projects/" + self.project_id
                + "/api-keys/" + self.key_id, "DELETE")),
            ("project", self.project_id is not None, lambda: self.bearer(
                "/v1/terrascale/organizations/" + self.org_id + "/projects/" + self.project_id, "DELETE",
                expected=(200, 202, 204))),
            ("org", self.org_id is not None, lambda: self.bearer(
                "/v1/terrascale/organizations/" + self.org_id, "DELETE")),
            ("account", self.user_token is not None, self.delete_account),
        ]
        for name, ready, action in reversed(plan):
            if self.options.dry_run or not ready:
                continue
            mark = time.monotonic()
            try:
                action()
                self.steps.append((name + " (teardown)", True, int((time.monotonic() - mark) * 1000), ""))
            except Exception:
                self.steps.append((name + " (teardown)", False, int((time.monotonic() - mark) * 1000), ""))
                failures.append(name)
        return failures

    def delete_account(self):
        self.deletion_receipt = secrets.token_urlsafe(32)
        status, _ = self.bearer("/v1/terrascale/account", "DELETE",
                                extra={"X-TerraScale-Deletion-Receipt": self.deletion_receipt})
        for _ in range(6):
            if status == 204:
                return
            time.sleep(5)
            try:
                status, _ = self.bearer("/v1/terrascale/account", "DELETE",
                                        extra={"X-TerraScale-Deletion-Receipt": self.deletion_receipt})
            except JourneyError:
                return

    def execute(self):
        plan = [
            ("sign_up", "POST /ui/terrascale/sign-up, POST /ui/terrascale/sign-up/verify", self.sign_up),
            ("user_token", "POST /terrascale/oauth/device, POST /terrascale/oauth/token", self.user_access_token),
            ("create_org", "POST /v1/terrascale/organizations", self.create_org),
            ("create_project", "POST /v1/terrascale/organizations/:org/projects", self.create_project),
            ("create_key", "POST /v1/terrascale/step-up, POST .../projects/:project/api-keys", self.create_key),
            ("exchange_token", "POST /terrascale/oauth/token (client_credentials)", self.exchange_token),
            ("create_database", "POST /v1/projects/:project/databases", self.create_database),
            ("sql_round_trip", "POST /v1/projects/:project/databases/:db/sql/execute", self.sql_round_trip),
        ]
        failure = None
        try:
            for name, _plan_line, action in plan:
                self.run(name, action)
        except JourneyError as error:
            failure = error
        finally:
            if not self.options.keep:
                self.teardown()
        self.report(failure)
        return 1 if failure or any(not ok for _, ok, _, _ in self.steps) else 0

    def report(self, failure):
        width = max(len(step[0]) for step in self.steps) if self.steps else 4
        print("{:<{}}  {:<4}  {:>7}".format("step", width, "ok", "ms"))
        for name, ok, ms, _detail in self.steps:
            print("{:<{}}  {:<4}  {:>7}".format(name, width, "ok" if ok else "FAIL", ms))
        if self.first_query_ms is not None:
            print("time to first query: {} ms".format(self.first_query_ms))
        if failure:
            print("journey failed: " + str(failure))


def parse_args(argv):
    parser = argparse.ArgumentParser(
        description="Re-runnable live customer journey: sign up, create org, project, database and key, "
                    "run one SQL REST round trip, then tear everything down. Creates and deletes real resources.")
    parser.add_argument("--base-url", default="https://api.terrascale.tech")
    parser.add_argument("--auth-url", default="https://auth.terrascale.tech")
    parser.add_argument("--email", required=True,
                        help="plus-tag recipient address; {ts} is replaced with the run timestamp")
    parser.add_argument("--imap-host", required=True)
    parser.add_argument("--imap-user", required=True)
    parser.add_argument("--imap-secret-file", required=True,
                        help="path to a 0600 file holding the IMAP password")
    parser.add_argument("--region", help="home region for the database")
    parser.add_argument("--keep", action="store_true", help="skip teardown")
    parser.add_argument("--dry-run", action="store_true", help="print the planned steps; no network, no secrets")
    return parser.parse_args(argv)


def main(argv):
    options = parse_args(argv)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    options.email = options.email.replace("{ts}", stamp)
    options.prefix = "e2e-" + stamp + "-"
    if options.dry_run:
        steps = [
            ("sign_up", "POST /ui/terrascale/sign-up then POST /ui/terrascale/sign-up/verify (code from IMAP)"),
            ("user_token", "POST /terrascale/oauth/device, approve /ui/terrascale/device, POST /terrascale/oauth/token"),
            ("create_org", "POST /v1/terrascale/organizations"),
            ("create_project", "POST /v1/terrascale/organizations/:org/projects"),
            ("create_key", "POST /v1/terrascale/step-up, POST /v1/terrascale/organizations/:org/projects/:project/api-keys"),
            ("exchange_token", "POST /terrascale/oauth/token grant_type=client_credentials"),
            ("create_database", "POST /v1/projects/:project/databases"),
            ("sql_round_trip", "POST /v1/projects/:project/databases/:db/sql/execute"),
            ("teardown", "DELETE database, key, project, organization, account (reverse order)"),
        ]
        print("dry run for {} (prefix {})".format(options.email, options.prefix))
        for name, line in steps:
            print("{:<15} {}".format(name, line))
        return 0
    mode = oct(os.stat(options.imap_secret_file).st_mode & 0o777)
    if mode != "0o600":
        print("IMAP secret file mode must be 0600, got " + mode)
        return 2
    journey = Journey(options)
    try:
        return journey.execute()
    except KeyboardInterrupt:
        return 130


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
