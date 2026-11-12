# Hosted continuations

Successful hosted sign-in, SSO handoff, password reset and logout pages continue
through a meta refresh and expose the same encoded target in a Continue link.
Targets longer than 4096 characters, before or after URI encoding, produce neither
a refresh nor a continuation link. Logout still ends the session and shows the
Signed out message; an oversized state is not included in that response.

Passkey sign-in fetches the completion page without displaying its HTML.
The browser script replaces the current location with that page's continuation
link, or reloads the sign-in page when there is no continuation. The fetched
page's meta refresh never executes, so these paths cannot navigate twice.

Password, email-code, sign-up verification, MFA and password-reset forms preserve
ui_locales through their action URLs, including resend and password-choice steps.
