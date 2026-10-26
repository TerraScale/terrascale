# Hosted MFA recovery-code entry

The hosted MFA sign-in and two-factor removal forms accept either the current
six-digit authenticator code or one unused 24-character recovery code. Enter or
paste the complete recovery code into the same authentication-code field.

The segmented numeric input is reserved for fields that explicitly declare
both a six-digit numeric pattern and a maximum length of six. Fields accepting
recovery codes remain text inputs, preserving letters and the complete value.
Their labels and one-time-code autocomplete remain available to the browser.

This presentation change does not alter the authentication API, stored
credentials, recovery-code lifetime or single-use checks. Reusing a consumed
recovery code remains invalid. Turning off two-factor authentication still
requires recent authentication and signs out existing sessions.

Fast browser integration coverage exercises email-code segmentation, full
recovery-code sign-in and full recovery-code removal against the real hosted
HTTP application with isolated browser contexts and TerraBase test storage.
