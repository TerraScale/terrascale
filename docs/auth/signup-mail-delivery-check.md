# Live signup email delivery check

`nice -n 19 make auth-mail-delivery-check` checks production delivery with a fresh free mail.tm inbox. The independent **Live signup email delivery** workflow runs it daily and on manual dispatch; it is not a merge or release gate. Failures create or update an open GitHub issue, including sanitized evidence and the run URL. A disposable provider outage also fails visibly.

The check loads `/ui/terrascale/sign-up`, submits its same-origin form once, checks the pending verification form, then polls the inbox for up to 120 seconds. It fetches `/messages/{id}` and checks the sender, fresh recipient and verification code in the full message. A successful Cloudflare response alone cannot pass it. It never redeems the code, so it creates no permanent ts-auth account; the pending challenge expires normally. Each run spends one authentication email and deletes its disposable inbox in cleanup.

Credentials, cookies, pending tokens, codes, subjects containing codes and message bodies stay in memory. Output contains only status, fixed failure reasons, recipient and message SHA-256 hashes, the sender, receipt/verification times and elapsed time. A missing provider timestamp does not invalidate fetched-message evidence. Cleanup failures fail the check but preserve any delivery evidence. No production credential or repository secret is needed. The script rejects redirects to prevent forwarding credentials.

Manual workflow dispatch must select `main`; other branches are skipped so unreviewed code cannot spend production mail capacity.

The mail.tm API paths are documented in `https://api.mail.tm/docs.jsonopenapi`: `GET /domains`, `POST /accounts`, `POST /token`, `GET /me`, `GET /messages`, `GET /messages/{id}` and `DELETE /accounts/{id}`. If creation's response is lost, cleanup authenticates with the generated credentials and recovers the account ID through `/me`. The script uses Python's standard library and does not install packages.
