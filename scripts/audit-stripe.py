#!/usr/bin/env python3
"""Read-only Stripe test/live configuration audit. Prints no credentials or customer data."""

import argparse
import contextlib
import json
import os
import pathlib
import re
import sys
import urllib.error
import urllib.parse
import urllib.request
from collections import Counter


EXPECTED = {
    ("starter", "monthly"): 500,
    ("starter", "annual"): 5000,
    ("pro", "monthly"): 5000,
    ("pro", "annual"): 50000,
    ("business", "monthly"): 9900,
    ("business", "annual"): 99000,
}
EVENTS = {
    "checkout.session.completed",
    "checkout.session.async_payment_succeeded",
    "checkout.session.async_payment_failed",
    "checkout.session.expired",
    "customer.subscription.created",
    "customer.subscription.updated",
    "customer.subscription.deleted",
    "customer.subscription.paused",
    "customer.subscription.resumed",
    "customer.subscription.pending_update_applied",
    "customer.subscription.pending_update_expired",
    "customer.subscription.trial_will_end",
    "invoice.finalized",
    "invoice.paid",
    "invoice.payment_succeeded",
    "invoice.payment_failed",
    "invoice.payment_action_required",
    "invoice.marked_uncollectible",
    "invoice.voided",
}


def library_api_version():
    """The API version the vendored stripe-go decodes; webhook events must match it."""
    root = pathlib.Path(__file__).resolve().parent.parent
    for source in root.glob("vendor/github.com/stripe/stripe-go/v*/api_version.go"):
        match = re.search(r'APIVersion\s+string\s*=\s*"([^"]+)"', source.read_text(encoding="utf-8"))
        if match:
            return match.group(1)
    return ""


def stripe_get(key, path, params=None):
    query = urllib.parse.urlencode(params or {})
    url = "https://api.stripe.com/v1/" + path + ("?" + query if query else "")
    request = urllib.request.Request(url, headers={"Authorization": "Bearer " + key, "Stripe-Version": library_api_version()})
    with urllib.request.urlopen(request, timeout=15) as response:
        return json.load(response)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("test", "live"), default="test", help="expected mode (default: test); never changes the running app")
    parser.add_argument("--online", action="store_true", help="query Stripe API read-only")
    parser.add_argument("--env-file", help="read KEY=VALUE settings without printing them; - reads stdin from OCI Vault")
    args = parser.parse_args()
    values = {} if args.env_file else dict(os.environ)
    if args.env_file:
        source = contextlib.nullcontext(sys.stdin) if args.env_file == "-" else open(args.env_file, encoding="utf-8")
        with source as source:
            for raw in source:
                raw = raw.strip()
                if raw and not raw.startswith("#") and "=" in raw:
                    name, value = raw.split("=", 1)
                    values[name.strip()] = value.strip().strip('"').strip("'")
    def setting(name):
        return values.get(name, "").strip()
    key = setting("STRIPE_SECRET_KEY")
    origin = setting("DASHBOARD_PUBLIC_ORIGIN").rstrip("/")
    prices = {(plan, interval): setting(f"STRIPE_PRICE_{plan.upper()}_{interval.upper()}") for plan, interval in EXPECTED}
    api_version = library_api_version()
    live = args.mode == "live"
    parsed_origin = urllib.parse.urlsplit(origin)
    checks = {
        "key_matches_mode": key.startswith((f"sk_{args.mode}_", f"rk_{args.mode}_")),
        "configured_mode": setting("STRIPE_MODE") == args.mode,
        "webhook_secret_present": setting("STRIPE_WEBHOOK_SECRET").startswith("whsec_"),
        "public_https_origin": parsed_origin.scheme == "https" and bool(parsed_origin.hostname) and not any((parsed_origin.username, parsed_origin.password, parsed_origin.path, parsed_origin.query, parsed_origin.fragment)),
        "six_price_ids_present": all(value.startswith("price_") for value in prices.values()) and len(set(prices.values())) == 6,
    }
    details = {"mode": args.mode}
    if args.online and all(checks.values()):
        for (plan, interval), expected_amount in EXPECTED.items():
            price_id = prices[(plan, interval)]
            label = f"{plan}_{interval}"
            if not price_id:
                checks[label] = False
                continue
            price = stripe_get(key, "prices/" + urllib.parse.quote(price_id, safe=""), {"expand[]": "product"})
            product = price.get("product") or {}
            if isinstance(product, str):
                product = stripe_get(key, "products/" + urllib.parse.quote(product, safe=""))
            actual_interval = "month" if interval == "monthly" else "year"
            checks[label] = all((
                price.get("active") is True,
                price.get("livemode") is live,
                price.get("currency") == "usd",
                price.get("unit_amount") == expected_amount,
                (price.get("recurring") or {}).get("interval") == actual_interval,
                (price.get("recurring") or {}).get("interval_count", 1) == 1,
                product.get("active") is True,
                product.get("livemode") is live,
                plan in product.get("name", "").lower(),
            ))
        endpoints = stripe_get(key, "webhook_endpoints", {"limit": 100}).get("data", [])
        webhook_urls = {origin + "/api/v1/billing/stripe/webhook", origin + "/api/auth/stripe/webhook"}
        matches = [item for item in endpoints if item.get("url") in webhook_urls and item.get("livemode") is live]
        details["enabled_invoice_created_endpoints"] = sum(
            item.get("status") == "enabled" and item.get("livemode") is live
            and ("invoice.created" in item.get("enabled_events", []) or "*" in item.get("enabled_events", []))
            for item in endpoints
        )
        checks["webhook_endpoint"] = len(matches) == 1 and matches[0].get("status") == "enabled" if matches else False
        checks["webhook_api_version"] = bool(matches) and bool(api_version) and matches[0].get("api_version") == api_version
        details["expected_webhook_api_version"] = api_version
        checks["webhook_events"] = bool(matches) and ("*" in matches[0].get("enabled_events", []) or EVENTS <= set(matches[0].get("enabled_events", [])))
        details["matching_webhook_endpoints"] = len(matches)
        if matches and "*" not in matches[0].get("enabled_events", []):
            details["missing_webhook_events"] = sorted(EVENTS - set(matches[0].get("enabled_events", [])))
        details["delivery_verification"] = "Check endpoint delivery attempts in Stripe Workbench; the public API event list cannot prove per-endpoint HTTP success."
        recent_invoices = stripe_get(key, "events", {"type": "invoice.created", "limit": 10}).get("data", [])
        details["recent_invoice_created_with_pending_webhooks"] = sum(item.get("pending_webhooks", 0) > 0 for item in recent_invoices)
        portals = stripe_get(key, "billing_portal/configurations", {"limit": 100}).get("data", [])
        checks["portal_payment_and_invoices"] = any(
            item.get("is_default") is True and item.get("active") and item.get("livemode") is live
            and (item.get("features") or {}).get("payment_method_update", {}).get("enabled")
            and (item.get("features") or {}).get("invoice_history", {}).get("enabled")
            and (item.get("features") or {}).get("subscription_cancel", {}).get("enabled")
            and not (item.get("features") or {}).get("subscription_update", {}).get("enabled")
            for item in portals
        )
        counts = Counter()
        customer_counts = Counter()
        starting_after = None
        while True:
            params = {"limit": 100, "status": "all"}
            if starting_after:
                params["starting_after"] = starting_after
            page = stripe_get(key, "subscriptions", params)
            subscriptions = page.get("data", [])
            for subscription in subscriptions:
                status = subscription.get("status", "unknown")
                counts[status] += 1
                if status in ("active", "trialing", "past_due", "unpaid"):
                    customer = subscription.get("customer", "")
                    customer_counts[customer] += 1
            if not page.get("has_more") or not subscriptions:
                break
            starting_after = subscriptions[-1]["id"]
        details["subscription_status_counts"] = dict(counts)
        details["customers_with_multiple_current_subscriptions"] = sum(count > 1 for count in customer_counts.values())
        details["subscription_attribution"] = "Customer counts are an inventory only; organization links require a trusted local account record."
    elif args.online:
        details["online"] = "Skipped because local configuration checks failed."
    report = {"checks": checks, "details": details, "passed": all(checks.values())}
    print(json.dumps(report, indent=2, sort_keys=True))
    return 0 if report["passed"] else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError) as exc:
        print(json.dumps({"passed": False, "error": type(exc).__name__}), file=sys.stderr)
        sys.exit(2)
