#!/usr/bin/env python3
"""Reject undefined design-system classes in Razor markup using the shipped CSS."""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src/TerraScale.Dashboard"
# Behavior/automation hooks only; never add a missing visual style here.
HOOKS = {
    "ts-search-results": "ShellSidebar keyboard navigation querySelectorAll",
    "ts-chart-tip-name": "AnalyticsPageAuditJourneyTests SVG series-name extraction",
    "ts-apikeys-date": "ApiKeyNetworkJourneyTests date-cell browser locators",
    "ts-notifications-summary": "NotificationBrowserJourneyTests live-update locators",
    "ts-data-sql": "DataBrowserJourneyTests SQL-error browser locators",
}


def styles(entry, seen=None):
    """Follow imports, so an unshipped stylesheet cannot satisfy the check."""
    seen = set() if seen is None else seen
    entry = entry.resolve()
    if entry in seen:
        return ""
    seen.add(entry)
    css = re.sub(r"/\*.*?\*/", "", entry.read_text(), flags=re.S)
    return css + "\n" + "\n".join(
        styles(entry.parent / name, seen)
        for name in re.findall(r'@import\s+[\"\']([^\"\']+)[\"\']', css)
    )


def attributes(markup):
    """Read class attributes, including quotes inside Razor @(expressions)."""
    markup = re.sub(r"@\*.*?\*@|<!--.*?-->", "", markup, flags=re.S)
    for match in re.finditer(r"\bclass\s*=\s*([\"'])", markup):
        quote, start = match[1], match.end()
        pos, depth, inner = start, 0, None
        while pos < len(markup):
            char = markup[pos]
            if depth:
                if inner:
                    if char == "\\":
                        pos += 2
                        continue
                    if char == inner:
                        inner = None
                elif char in "\"'":
                    inner = char
                elif char == "(":
                    depth += 1
                elif char == ")":
                    depth -= 1
            elif markup.startswith("@(", pos):
                depth = 1
                pos += 2
                continue
            elif char == quote:
                yield markup[start:pos], markup[:start].count("\n") + 1
                break
            pos += 1


def missing(markup, defined):
    return [(name, line) for value, line in attributes(markup)
            for name in re.findall(r"\bts-[a-zA-Z0-9_-]+", value)
            # Runtime modifiers such as ts-badge-@severity have no literal class.
            if not name.endswith("-") and name not in defined and name not in HOOKS]


def main():
    defined = set(re.findall(r"\.(ts-[a-zA-Z0-9_-]+)", styles(APP / "web/assets/css/terrascale.css")))
    # Exercise regressions that simple HTML regexes miss, and reject unknown hooks.
    assert missing('<div id="ts-id" class="ts-real ts-missing">', {"ts-real"}) == [("ts-missing", 1)]
    assert missing('<p class="@(ok ? "ts-real" : "ts-typo")">', {"ts-real"}) == [("ts-typo", 1)]
    assert not missing('<p class="ts-badge-@severity ts-real">', {"ts-real"})
    failures = [f"{path.relative_to(ROOT)}:{line}: undefined .{name}"
                for path in sorted(APP.rglob("*.cshtml"))
                for name, line in missing(path.read_text(), defined)]
    if failures:
        raise SystemExit("\n".join(failures))
    print(f"Design classes: all Razor classes defined ({len(defined)} shipped classes; {len(HOOKS)} behavior hooks)")


if __name__ == "__main__":
    main()
