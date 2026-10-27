"""Private helpers for disposable-inbox live checks. Never log response content."""
import hashlib
import http.cookiejar
import json
import re
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from html.parser import HTMLParser

AUTH = "https://auth.terrascale.tech"
DASHBOARD = "https://dash.terrascale.tech"
SENDER = "no-reply@terrascale.tech"


class Failure(Exception):
    """Messages must contain only fixed text, never remote response content."""


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


def now():
    return datetime.now(timezone.utc).isoformat()


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args):
        return None


class Forms(HTMLParser):
    def __init__(self, page):
        super().__init__()
        self.forms, self.links, self.current = [], [], None
        self.feed(page)

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag == "form":
            self.current = {"action": attrs.get("action", ""), "fields": {}}
            self.forms.append(self.current)
        elif tag == "input" and self.current is not None and attrs.get("name"):
            self.current["fields"][attrs["name"]] = attrs.get("value", "")
        elif tag == "a" and attrs.get("href"):
            self.links.append(attrs["href"])

    def handle_endtag(self, tag):
        if tag == "form":
            self.current = None

    def fields(self, action):
        for form in self.forms:
            if form["action"] == action:
                return dict(form["fields"])
        raise Failure("Expected hosted form is missing")


class Http:
    def __init__(self, origins):
        self.origins = origins
        self.events = []
        self.cookies = http.cookiejar.CookieJar()
        self.opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(self.cookies))

    def request(self, url, method="GET", payload=None, form=None, headers=None, expected=(200,), retry=False):
        if urllib.parse.urlsplit(url)._replace(path="", query="", fragment="").geturl() not in self.origins:
            raise Failure("Request origin is outside the check allowlist")
        headers = dict(headers or {})
        headers["Accept-Language"] = "en"
        data = None
        if form is not None:
            data = urllib.parse.urlencode(form).encode()
            headers.update({"Content-Type": "application/x-www-form-urlencoded", "Origin": urllib.parse.urlsplit(url).scheme + "://" + urllib.parse.urlsplit(url).netloc})
        elif payload is not None:
            data = json.dumps(payload).encode()
            headers["Content-Type"] = "application/json"
        for attempt in range(3 if retry else 1):
            started = time.monotonic()
            try:
                response = self.opener.open(urllib.request.Request(url, data, headers, method=method), timeout=20)
            except urllib.error.HTTPError as error:
                response = error
            except (urllib.error.URLError, TimeoutError, OSError):
                if retry and attempt < 2:
                    time.sleep(attempt + 1)
                    continue
                raise Failure("HTTP transport failed") from None
            with response:
                status, body, location = response.code, response.read().decode(), response.headers.get("Location")
                correlation = response.headers.get("X-Request-ID") or response.headers.get("traceparent") or response.headers.get("CF-Ray")
            path = re.sub(r"/[A-Za-z0-9_-]{20,}", "/:id", urllib.parse.urlsplit(url).path)
            event = {"method": method, "path": path, "status": status, "at": now(), "duration_seconds": round(time.monotonic() - started, 2)}
            if correlation and re.fullmatch(r"[A-Za-z0-9:-]{1,120}", correlation):
                event["request_id"] = correlation
            self.events.append(event)
            if status in expected:
                return status, body, location
            if retry and attempt < 2 and (status == 429 or status >= 500):
                time.sleep(attempt + 1)
                continue
            raise Failure(f"HTTP status {status}")
        raise Failure("HTTP retry budget exhausted")

    def follow(self, url):
        for _ in range(8):
            status, page, location = self.request(url, expected=(200, 302, 303))
            if status == 200:
                return url, page
            url = urllib.parse.urljoin(url, location)
        raise Failure("Redirect limit exceeded")


class Inbox:
    def __init__(self, provider):
        self.provider, self.token, self.account_id = provider, None, None
        self.address, self.password = None, secrets.token_urlsafe(32)
        self.closed = False
        self.http, self.seen = Http({provider}), set()
        self.seen_hashes = set()
        self.checkpoint = lambda: None

    def api(self, path, method="GET", payload=None):
        headers = {"Authorization": "Bearer " + self.token} if self.token else {}
        _, body, _ = self.http.request(self.provider + path, method, payload=payload, headers=headers,
                                      expected=(200, 201, 204, 404) if method == "DELETE" else (200, 201), retry=method == "GET")
        return json.loads(body) if body else None

    def create(self):
        domain = next((d["domain"] for d in self.api("/domains")["hydra:member"] if d.get("isActive") and not d.get("isPrivate")), None)
        if not domain or not re.fullmatch(r"[a-z0-9.-]+", domain):
            raise Failure("No public disposable domain")
        self.address = "ts-email-audit-" + secrets.token_hex(12) + "@" + domain
        self.checkpoint()
        credentials = {"address": self.address, "password": self.password}
        self.account_id = self.api("/accounts", "POST", credentials)["id"]
        self.checkpoint()
        self.token = self.api("/token", "POST", credentials)["token"]
        self.checkpoint()
        return self

    def receive(self, flow, predicate, timeout=120, sender=SENDER):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for summary in self.api("/messages")["hydra:member"]:
                if summary["id"] in self.seen or digest(summary["id"]) in self.seen_hashes:
                    continue
                message = self.api("/messages/" + urllib.parse.quote(summary["id"], safe=""))
                if message.get("from", {}).get("address", "").lower() != sender or self.address not in [to.get("address", "").lower() for to in message.get("to", [])]:
                    continue
                value = predicate(message)
                if value is None:
                    continue
                self.seen.add(message["id"])
                return value, {"flow": flow, "status": "delivered", "sender": sender, "recipient_sha256": digest(self.address),
                               "message_sha256": digest(json.dumps(message, sort_keys=True)), "message_id_sha256": digest(message["id"]),
                               "received_at": message.get("createdAt"), "verified_at": now(), "shape_valid": True, "full_message_fetched": True}
            time.sleep(3)
        raise Failure("No matching full inbox message within delivery deadline")

    def close(self):
        if self.address and not self.closed:
            if not self.token:
                self.token = self.api("/token", "POST", {"address": self.address, "password": self.password})["token"]
            if not self.account_id:
                self.account_id = self.api("/me")["id"]
            self.api("/accounts/" + urllib.parse.quote(self.account_id, safe=""), "DELETE")
            self.closed = True


def dashboard_flows(audit):
    """Use the normal dashboard session and its CSRF-protected product forms."""
    def context():
        _, body, _ = audit.http.request(DASHBOARD + "/api/v1/dashboard/context")
        value = json.loads(body)
        if value["user"]["email"] != audit.owner.address:
            raise Failure("Dashboard identity changed")
        return value

    def csrf(path):
        _, page = audit.http.follow(DASHBOARD + path)
        for form in Forms(page).forms:
            if form["fields"].get("_csrf"):
                return form["fields"]["_csrf"]
        raise Failure("Dashboard CSRF token is unavailable")

    def post(path, fields, token, expected=(200, 201, 302, 303, 204)):
        return audit.form(DASHBOARD + path, {"_csrf": token, **fields}, expected=expected)

    def invitation_token(page):
        found = re.findall(r"/invitations/([A-Za-z0-9_-]{20,})", page)
        if not found:
            raise Failure("Product invitation link is missing")
        return found[0]

    selected = context()
    organization = next(org for org in selected["organizations"] if org["id"] == selected["selection"]["organizationId"])
    if organization["ownerUserId"] != selected["user"]["id"] or organization["role"] != "owner":
        raise Failure("Selected organization is not owned by the synthetic user")
    audit.org = organization["id"]
    audit.project = selected["selection"]["projectId"]
    audit.checkpoint()

    def invite_and_resend():
        inbox = audit.mailbox()
        token = csrf("/organization?tab=invitations")
        pending_id = None
        try:
            _, page, _ = post("/api/v1/access/invitations", {"email": inbox.address, "role": "viewer"}, token)
            original_token = invitation_token(page)
            ids = re.findall(r"/api/v1/access/invitations/([0-9A-Z]{26})/resend", page)
            if len(set(ids)) != 1:
                raise Failure("Synthetic invitation identity is ambiguous")
            pending_id = ids[0]
            def receive(flow, expected_token):
                def shape(message):
                    text = message.get("text", "") + " ".join(message.get("html", []))
                    return True if DASHBOARD + "/invitations/" + expected_token in text else None
                _, evidence = inbox.receive(flow, shape)
                evidence["evidence"] = "Full message contains exact new product-generated dashboard invitation link"
                audit.records.append(evidence)
                audit.save()
            receive("organization_invitation", original_token)
            _, page, _ = post("/api/v1/access/invitations/" + pending_id + "/resend", {}, token)
            replacement = invitation_token(page)
            ids = re.findall(r"/api/v1/access/invitations/([0-9A-Z]{26})/resend", page)
            if len(set(ids)) != 1 or replacement == original_token:
                raise Failure("Resend did not provide a distinct replacement invitation")
            pending_id = ids[0]
            receive("organization_invitation_resend", replacement)
            audit.http.request(DASHBOARD + "/invitations/" + original_token, expected=(404,))
            audit.records[-1]["previous_token_revoked"] = True
        finally:
            if pending_id:
                post("/api/v1/access/invitations/" + pending_id + "/revoke", {}, token)

    audit.step("organization_invitation_and_resend", invite_and_resend)
    if not audit.token:
        for flow in ("project_invitation", "project_invitation_resend", "project_tenant_signup_verification", "project_tenant_sign_in_code", "project_tenant_password_reset"):
            audit.records.append({"flow": flow, "status": "uncovered", "reason": "Auth ownership API needs a platform sign-in bearer. Current dashboard forms do not create project-scoped invitations or end-user identities. Protected proofs remain pending until sign-in is confirmed healthy."})

    def alerts():
        if organization["kind"] != "personal" or organization["paymentMethodValidated"]:
            raise Failure("Tiny alert proof requires this new unpaid personal workspace")
        project = organization["projects"].get(audit.project)
        if not project or project["databases"]:
            raise Failure("Alert proof requires an empty isolated synthetic project")
        token = csrf("/databases")
        name, database_id, rule_id = "email-audit-" + secrets.token_hex(6), None, None
        try:
            _, body, _ = post("/api/v1/dashboard/databases", {"name": name, "region": project["region"] or "GRU"}, token, expected=(201,))
            database_id = json.loads(body)["id"]
            post("/api/v1/dashboard/data/collections", {"databaseId": database_id, "name": "email_audit"}, token)
            # Storage evaluation rounds GB to three decimals. One bounded 512 KiB
            # record produces a nonzero measurement while staying within the free plan.
            document = json.dumps({"purpose": "isolated email delivery audit", "padding": "x" * (512 * 1024)})
            post("/api/v1/dashboard/data/documents", {"databaseId": database_id, "collection": "email_audit", "operation": "create", "document": document}, token)
            token = csrf("/notifications/alerts")
            rule_name = "Email audit " + secrets.token_hex(6)
            _, page, _ = post("/api/v1/notifications/alerts", {"name": rule_name, "metric": "storage_gb", "threshold": "0.000000001", "database": database_id, "email": "on"}, token, expected=(201,))
            ids = re.findall(r"/api/v1/notifications/alerts/([0-9A-Z]{26})/threshold", page)
            if len(set(ids)) != 1:
                raise Failure("Synthetic alert identity is ambiguous")
            rule_id = ids[0]
            for flow, marker in (("dashboard_alert_firing", "firing"), ("dashboard_alert_resolved", "resolved")):
                def shape(message):
                    text = message.get("text", "")
                    return True if rule_name in text and marker in text.lower() else None
                _, evidence = audit.owner.receive(flow, shape, timeout=180)
                evidence["evidence"] = "Owner-created storage rule, one bounded 512 KiB synthetic record, normal analytics worker, exact rule name and transition in full message"
                audit.records.append(evidence)
                audit.save()
                if flow == "dashboard_alert_firing":
                    post("/api/v1/notifications/alerts/" + rule_id + "/threshold", {"threshold": "1000"}, token)
        finally:
            if rule_id:
                post("/api/v1/notifications/alerts/" + rule_id + "/delete", {}, token)
            if database_id:
                post("/api/v1/dashboard/databases/delete", {"databaseId": database_id, "confirmName": name}, token)
    audit.step("dashboard_alerts", alerts)


def status_confirmation(audit):
    """Subscribe only the new synthetic address, then remove it without opting into updates."""
    origin = "https://status.terrascale.tech"
    audit.http.origins.add(origin)
    inbox, manage, submitted = audit.mailbox(), None, False
    try:
        _, page, _ = audit.http.request(origin + "/subscribe")
        fields = Forms(page).fields("/subscribe")
        fields.update(channel="email", target=inbox.address, website="")
        submitted = True
        audit.form(origin + "/subscribe", fields)
        def shape(message):
            text = message.get("text", "")
            confirm = re.search(r"https://status\.terrascale\.tech/subscribe/confirm\?[^\s<>]+", text)
            remove = re.search(r"https://status\.terrascale\.tech/subscribe/manage\?[^\s<>]+", text)
            return remove[0] if confirm and remove else None
        manage, evidence = inbox.receive("status_subscription_confirmation", shape, sender="noreply@terrascale.tech")
        evidence["evidence"] = "Full message contains signed confirmation and management links to the status product; updates were not activated"
        audit.records.append(evidence)
        audit.save()
    finally:
        if manage:
            _, page, _ = audit.http.request(manage)
            fields = Forms(page).fields("/subscribe/unsubscribe")
            audit.form(origin + "/subscribe/unsubscribe", fields)
            audit.http.request(manage, expected=(404,))
            evidence["subscription_cleanup"] = "deleted"
            audit.cleanup.append({"resource": "synthetic_status_subscription", "status": "deleted"})
        elif submitted:
            audit.cleanup.append({"resource": "synthetic_status_subscription", "status": "unconfirmed", "reason": "Management capability was not received; no subscription was confirmed"})
        else:
            audit.cleanup.append({"resource": "synthetic_status_subscription", "status": "not_created", "reason": "Subscription form was refused before any subscribe POST"})
