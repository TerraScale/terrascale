#!/usr/bin/env python3
"""Check real hosted signup delivery; secrets and message content stay in memory."""
import argparse
import hashlib
import http.cookiejar
import json
import re
import secrets
import signal
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone
from email.utils import parseaddr
from html import unescape
from html.parser import HTMLParser

AUTH = "https://auth.terrascale.tech"
SIGNUP = "/ui/terrascale/sign-up"
MAIL = "https://api.mail.tm"
SENDER = "no-reply@terrascale.tech"


class CheckFailure(Exception):
    """Only fixed, non-secret messages may be passed to this exception."""


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        # Do not forward passwords, mailbox Authorization, or cookies to redirects.
        raise CheckFailure("Unexpected HTTP redirect")


class Forms(HTMLParser):
    def __init__(self, html):
        super().__init__()
        self.forms = []
        self.current = None
        self.feed(html)

    def handle_starttag(self, tag, attributes):
        attrs = dict(attributes)
        if tag == "form":
            self.current = {"action": attrs.get("action"), "method": attrs.get("method", "get"), "fields": {}}
            self.forms.append(self.current)
        elif tag == "input" and self.current is not None and attrs.get("name"):
            self.current["fields"][attrs["name"]] = attrs.get("value", "")

    def handle_endtag(self, tag):
        if tag == "form":
            self.current = None

    def fields(self, action):
        for form in self.forms:
            if form["action"] == action and form["method"].lower() == "post":
                return form["fields"]
        raise CheckFailure("Expected hosted signup form is missing")


def request(opener, url, method="GET", data=None, headers=None, retry=False, deadline=None):
    """Retry safe reads/authentication only; signup and mailbox creation are never retried."""
    attempts = 3 if retry else 1
    for attempt in range(attempts):
        remaining = deadline - time.monotonic() if deadline is not None else 15
        if remaining <= 0:
            raise CheckFailure("Signup email was not received within 120 seconds")
        req = urllib.request.Request(url, data=data, method=method, headers=headers or {})
        try:
            with opener.open(req, timeout=min(15, remaining)) as response:
                return response.read().decode("utf-8")
        except urllib.error.HTTPError as error:
            status = error.code
            error.close()
            if method == "DELETE" and status == 404:
                return ""  # Cleanup succeeded even if a prior DELETE response was lost.
            if attempt + 1 == attempts or status != 429 and status < 500:
                raise CheckFailure(f"HTTP request failed with status {status}") from None
        except (urllib.error.URLError, TimeoutError, OSError):
            if attempt + 1 == attempts:
                raise CheckFailure("HTTP request failed or timed out") from None
        time.sleep(min(attempt + 1, max(0, deadline - time.monotonic())) if deadline is not None else attempt + 1)
    raise CheckFailure("HTTP retries exhausted")


def digest(value):
    return hashlib.sha256(value.encode()).hexdigest()


def has_mx(opener, domain):
    lookup = json.loads(request(opener, "https://dns.google/resolve?" + urllib.parse.urlencode({"name": domain, "type": "MX"}),
        headers={"Accept": "application/dns-json"}, retry=True))
    return lookup.get("Status") == 0 and any(record.get("type") == 15 and record.get("data", "").split()[-1:] not in ([], ["."])
        for record in lookup.get("Answer", []))


class MailText(HTMLParser):
    def __init__(self, body):
        super().__init__()
        self.parts = []
        self.feed(body)

    def handle_data(self, data):
        self.parts.append(data)


class GuerrillaInbox:
    """Independent session inbox; used only before any hosted signup is submitted."""
    def __init__(self):
        self.opener = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        self.address = None

    def api(self, function, method="GET", retry=False, deadline=None, **fields):
        data = urllib.parse.urlencode({"f": function, "lang": "en", **fields}, doseq=True).encode()
        url = "https://www.guerrillamail.com/ajax.php"
        body = request(self.opener, url + "?" + data.decode() if method == "GET" else url, method,
            None if method == "GET" else data, {"Content-Type": "application/x-www-form-urlencoded"}, retry, deadline)
        return json.loads(body)

    def create(self):
        self.address = self.api("get_email_address")["email_addr"].lower()
        user = "ts-mail-check-" + secrets.token_hex(12)
        self.address = self.api("set_email_user", "POST", email_user=user)["email_addr"].lower()
        local, separator, domain = self.address.partition("@")
        if local != user or not separator or not re.fullmatch(r"[a-z0-9.-]+", domain):
            raise CheckFailure("Fallback inbox did not reserve the fresh requested address")
        if not has_mx(self.opener, domain):
            raise CheckFailure("Fallback inbox domain has no working MX records")
        return self.address

    def messages(self, deadline):
        summaries = self.api("get_email_list", offset=0, retry=True, deadline=deadline)
        if summaries["email"].lower() != self.address:
            raise CheckFailure("Fallback inbox session changed unexpectedly")
        for summary in summaries["list"]:
            message = self.api("fetch_email", email_id=summary["mail_id"], retry=True, deadline=deadline)
            body = MailText(message["mail_body"])
            recipient = parseaddr(message["mail_recipient"])[1]
            # This API returns only the local part, unlike mail.tm's full address.
            # Bind it to the checked session; the full address must also be in the body.
            if recipient == self.address.partition("@")[0]:
                recipient = self.address
            yield {"id": str(message["mail_id"]), "from": {"address": parseaddr(message["mail_from"])[1]},
                "to": [{"address": recipient}],
                "text": "\n".join(body.parts), "subject": unescape(message["mail_subject"])}

    def close(self):
        if self.address is None:
            return
        messages = self.api("get_email_list", offset=0, retry=True)
        if messages["email"].lower() != self.address:
            raise CheckFailure("Fallback inbox session changed before cleanup")
        ids = [item["mail_id"] for item in messages["list"]]
        if ids:
            self.api("del_email", "POST", retry=True, **{"email_ids[]": ids})
        if self.api("get_email_list", offset=0, retry=True)["list"]:
            raise CheckFailure("Fallback inbox messages were not deleted")
        if self.api("forget_me", "POST", retry=True, email_addr=self.address) is not True:
            raise CheckFailure("Fallback inbox session was not released")


def run():
    started = time.monotonic()
    stage = "mailbox_setup"
    token = None
    account_id = None
    credentials = None
    creation_attempted = False
    evidence = None
    failure = None
    cleanup_failure = None
    fallback = None
    mail = urllib.request.build_opener(NoRedirect())
    hosted = urllib.request.build_opener(NoRedirect(), urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))

    def api(path, method="GET", payload=None, retry=False, deadline=None):
        headers = {"Accept": "application/ld+json"}
        if token:
            headers["Authorization"] = "Bearer " + token
        data = None
        if payload is not None:
            headers["Content-Type"] = "application/json"
            data = json.dumps(payload).encode()
        body = request(mail, MAIL + path, method, data, headers, retry, deadline)
        return json.loads(body) if body else None

    try:
        domain = None
        try:
            domains = api("/domains", retry=True)["hydra:member"]
            stage = "mailbox_dns"
            for item in domains:
                candidate = item.get("domain", "")
                if item.get("isActive") and not item.get("isPrivate") and re.fullmatch(r"[a-z0-9.-]+", candidate) and has_mx(mail, candidate):
                    domain = candidate
                    break
        except CheckFailure:
            # No mailbox or auth mail exists yet: an independent provider is safe.
            domain = None
        if domain is None:
            stage = "fallback_mailbox_setup"
            fallback = GuerrillaInbox()
            address = fallback.create()
        else:
            stage = "mailbox_setup"
            address = "ts-mail-check-" + secrets.token_hex(12) + "@" + domain
            credentials = {"address": address, "password": secrets.token_urlsafe(32)}
            creation_attempted = True
            account_id = api("/accounts", "POST", credentials)["id"]
            token = api("/token", "POST", credentials, retry=True)["token"]

        stage = "hosted_signup"
        page = request(hosted, AUTH + SIGNUP, headers={"Accept-Language": "en"}, retry=True)
        fields = Forms(page).fields(SIGNUP)
        fields.update(email=address, password=secrets.token_urlsafe(32))
        # Exactly one mail-start request: a failed/ambiguous response must not spend another send.
        page = request(hosted, AUTH + SIGNUP, "POST", urllib.parse.urlencode(fields).encode(), {
            "Content-Type": "application/x-www-form-urlencoded",
            "Origin": AUTH, "Referer": AUTH + SIGNUP, "Accept-Language": "en",
        })
        if not Forms(page).fields(SIGNUP + "/verify").get("pending_token"):
            raise CheckFailure("Hosted signup did not return a pending verification form")

        stage = "inbox_delivery"
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            messages = fallback.messages(deadline) if fallback else api("/messages", retry=True, deadline=deadline)["hydra:member"]
            for summary in messages:
                # Fetch the full message, not merely provider acceptance or an inbox summary.
                message_id = summary["id"]
                message = summary if fallback else api("/messages/" + urllib.parse.quote(message_id, safe=""), retry=True, deadline=deadline)
                recipients = [recipient.get("address", "").lower() for recipient in message.get("to", [])]
                if message.get("from", {}).get("address", "").lower() != SENDER or address not in recipients:
                    continue
                body = message.get("text", "")
                code = re.search(r"(?m)^\s*([0-9]{6})\s*$", body)
                if not code or code.group(1) not in message.get("subject", "") or address not in body:
                    raise CheckFailure("Received email does not contain the signup verification code")
                try:
                    received_at = datetime.fromisoformat(message["createdAt"].replace("Z", "+00:00")).isoformat()
                except (KeyError, AttributeError, TypeError, ValueError):
                    received_at = None
                evidence = {
                    "status": "delivered", "recipient_sha256": digest(address),
                    "inbox_provider": "guerrillamail.com" if fallback else "mail.tm",
                    "message_sha256": digest(message_id), "sender": SENDER,
                    "received_at": received_at,
                    "verified_at": datetime.now(timezone.utc).isoformat(),
                    "elapsed_seconds": round(time.monotonic() - started, 2),
                }
                break
            if evidence:
                break
            time.sleep(min(3, max(0, deadline - time.monotonic())))
        if not evidence:
            raise CheckFailure("Signup email was not received within 120 seconds")
    except CheckFailure as error:
        failure = {"status": "failed", "stage": stage, "reason": str(error)}
    except Exception as error:
        # Exception messages can contain credentials, URLs or a provider response body.
        failure = {"status": "failed", "stage": stage, "reason": type(error).__name__}
    finally:
        if fallback:
            try:
                fallback.close()
            except Exception:
                cleanup_failure = {"status": "failed", "stage": "mailbox_cleanup", "reason": "Fallback inbox message deletion and session release could not be confirmed"}
        if creation_attempted:
            try:
                # Creation may have succeeded even if its response was lost. Recover its identity
                # with our in-memory credentials, without issuing another create request.
                if not token:
                    token = api("/token", "POST", credentials, retry=True)["token"]
                if not account_id:
                    account_id = api("/me", retry=True)["id"]
                api("/accounts/" + urllib.parse.quote(account_id, safe=""), "DELETE", retry=True)
            except Exception:
                cleanup_failure = {"status": "failed", "stage": "mailbox_cleanup", "reason": "Disposable inbox deletion could not be confirmed"}
    if evidence:
        evidence["mailbox_cleanup"] = "failed" if cleanup_failure else "messages_deleted_session_released" if fallback else "deleted"
        print(json.dumps(evidence), flush=True)
    if cleanup_failure:
        print(json.dumps(cleanup_failure), flush=True)
    if failure:
        print(json.dumps(failure), flush=True)
    return 1 if failure or cleanup_failure else 0


if __name__ == "__main__":
    argparse.ArgumentParser(description=__doc__).parse_args()

    def interrupted(signum, frame):
        raise CheckFailure("Check interrupted")

    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    sys.exit(run())
