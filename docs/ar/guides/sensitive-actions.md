---
title: Confirm sensitive actions
---

<!-- i18n:fallback:ar -->
> **الترجمة قيد المراجعة.** لا تتوفر بعد مراجعة بشرية لهذه الصفحة بالعربية، لذلك يظهر المحتوى الأصلي بالإنجليزية.

<div lang="en" dir="ltr">

# Confirm sensitive actions

Deleting your account, creating a personal access token, creating or rotating an API key, and registering an OAuth client require fresh confirmation on `auth.terrascale.tech`. Enter your password, or request one email code if your account has no password. If you use two-factor authentication, also enter a fresh authenticator or recovery code. Your existing sign-in alone does not approve the action.

Confirmation applies to one account and one kind of action, expires within five minutes, and works once. Returning to an old confirmation page, changing accounts, or replaying an approval cannot authorize another action. Start the action again if confirmation expires. Opening the page sends no email; request a code once and use that message.

The server checks confirmation before creating the credential or starting account deletion, including cancelling subscriptions. A confirmed deletion can continue through its status page if cleanup is still pending. Confirmation is not proof that deletion has finished; see [data deletion](/ar/legal/data-deletion).

API and MCP callers receive `403 step_up_required` when fresh confirmation is missing. Use the dashboard to approve the sensitive action. Existing credentials continue to authorize their permitted operations until expiry or revocation; revoking a credential does not require creating another one.

API keys and confidential OAuth-client secrets issued after confirmation also depend on the account's authentication state. Signing out everywhere or resetting the password prevents further exchanges with those secrets. Signing out of only the browser that created them preserves their independent access. Previously issued access tokens remain subject to their normal short expiry. Older API-key and OAuth-client secrets issued before this confirmation flow keep working until revoked, expired or rotated.

For integrations, keep step-up proofs and deletion authorization capabilities on your backend. Never put them in URLs, browser storage or logs. Do not automatically repeat a credential or deletion mutation after a lost response. Use the operation's status or start a new confirmation when the outcome is unknown.

</div>
