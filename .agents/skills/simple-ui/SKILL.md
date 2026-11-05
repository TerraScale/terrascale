---
name: simple-ui
description: TerraScale UI standard — simple purpose, architecture, design and implementation, like Linear or Apple Notes. No taglines, helper text or secondary labels. Use whenever you create, change, review or write copy for any TerraScale page, screen, dialog, email or component (dashboard, ts-auth hosted pages, landing-site, docs-site chrome).
---

# Simple UI

> I want this to be as perfect as Linear's website or Apple Notes: simple purpose, simple architecture, simple design, simple implementation. Cut taglines, helper text and secondary labels. If a screen needs them to make sense, fix the layout. Every decision should elevate the user experience (UX), developer experience (DX) and agent experience (AX) at once, without breaking anything.

## Rules

1. One purpose per screen. A title, the content, one primary action.
2. Cards have a title only. No subtitle or description; rename the title if it is unclear.
3. Fields have a label only. No hint text. Encode format in the placeholder and input constraints (`type`, `min`, `max`, `pattern`, `maxlength`, sensible defaults). Explain only on validation failure.
4. Stats show label and value. No captions; exact metric definitions belong in `docs/`.
5. Empty states: one sentence, one action.
6. Dialogs: title and controls. Irreversible actions keep one short consequence sentence.
7. No eyebrows, taglines, subheadlines, "Recommended", "(optional)", "Visual preview" or similar secondary labels.
8. If removing text makes a screen confusing, change the layout, defaults, grouping or naming — never put the text back.

## Never cut

- Error and validation messages, status announcements (`role="status"`, `aria-live`).
- Security and consent safeguards: secret-shown-once notices, irreversible-action consequences, CLI device-code match checks, recovery-code warnings, legal text.
- Accessibility: `aria-label`, `ts-sr-only` text, `<label>` and `<legend>`. Drop `aria-describedby` that pointed at removed hints.
- Data itself (values, names, timestamps, code snippets the user copies).

## DX and AX

- Delete CSS classes, slice parameters and C# properties that only fed removed text; no dead code.
- Keep stable IDs, names and `data-*` hooks that tests, Datastar or agents use. Machine-readable content (OpenAPI, MCP, SVG map data) stays complete.
- Prefer native elements and existing components over new abstractions.
- Dashboard lint rejects the removed helper classes (`ts-hint`, `ts-card-description`, `ts-empty-detail`, `ts-stat-detail`, `ts-eyebrow`); do not reintroduce them under new names.

## Proof

- Before/after screenshots of every changed page (repo rule).
- Run the area's Fast suite through `ts-ship`; run browser journeys (`make dashboard-test-slow`) through `ts-heavy-any` when dashboard markup changes.
- Update tests that asserted removed copy to assert behavior instead; never weaken them.
- Compare the actual before/after screenshots against these rules before shipping; fix default-styled or unfinished controls first.

## Tabs and segmented controls

- SDK language switchers use the shared `.sdk-tabs` design and keyboard handler documented in [SDK language tabs](../../../docs/dashboard/sdk-language-tabs.md).
- Use a flat row with a self-hosted official language logo and name. Never nest boxed buttons inside another boxed control or rely on browser default button styling.
- Show selection with one blue accent underline. Keep hover, selected and visible keyboard focus distinct in light and dark themes.
- Select the first tab initially so the active language is visible on narrow screens.
- Use tablist/tab/tabpanel semantics, associated IDs, selected state and a single tab stop. Support Left/Right, Home/End and wraparound; selection must switch the snippet and copy target together.
- Verify desktop and narrow layouts, pointer and keyboard switching, loaded icons, and screenshots in every supported theme across dashboard, docs and landing.
- When shared tab sources change, update the importing entry points in docs and landing in the same change so all three sites run their Fast and deployment pipelines. Workflow dependency changes belong to the CI owner.
