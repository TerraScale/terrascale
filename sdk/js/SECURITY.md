# Security policy

## Reporting a vulnerability

Report suspected vulnerabilities privately using GitHub's advisory form:
https://github.com/TerraScale/terrascale/security/advisories/new

Do not disclose vulnerabilities in public issues, pull requests, or discussions.
Include the affected component, potential impact, and safe steps to reproduce.
Use redacted examples; do not include secrets, credentials, or private data.
For suspected exposed secrets, report their location without copying the value.

## Browser credentials

BrowserAuth keeps access, refresh and ID tokens only in memory and clears them on sign-out or failed refresh.
Do not persist these tokens in local storage, session storage, URLs or application logs. Reloading starts a new
sign-in. Session storage holds only the one-use PKCE verifier and state for up to ten minutes across the hosted
redirect. Never bundle a project key, client secret or management credential in a browser app.
