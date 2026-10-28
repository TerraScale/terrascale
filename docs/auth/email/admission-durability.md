# Mail admission budget durability, and where signup mail time goes

## Live breakdown, 2026-10-27 23:15Z (gru, three hosted sign-ups to mail.tm)

| step | run 1 | run 2 | run 3 |
| --- | --- | --- | --- |
| form POST answered | 0.96 s | 0.62 s | 0.70 s |
| `Date` header (Cloudflare accepted the send) | 1.5 s | 0.4 s | 3.8 s |
| received by the inbox's MX (`Received:` from Cloudflare SMTP) | 6 s | 4 s | 7 s |
| listed by the inbox API | 6.3 s | 5.9 s | 8.3 s |

Before Cloudflare had the message (0.4–3.8 s), the email worker ran these steps one after another:
- the account lookup;
- the code write (remote-durable);
- the email settings read;
- the admission budget read and write.

Cloudflare's relay to the recipient's MX then took about 3–4 s, and the inbox listed the message 1–2 s later. An
end-to-end measurement that polls an inbox every few seconds adds its poll interval on top.

## Change

The admission budget (`email_admission`, one shared document) is written `local_durable`, like rate-limit buckets
([rate-limit durability](../rate-limit-durability.md)). Each emailed code no longer waits for another region's
acknowledgement of that write: about 0.7 s per write measured on 2026-10-26, more when concurrent sends conflict and retry.
The code itself is still written `remote_durable`.

Trade-off: if gru is lost before the budget write replicates, those reservations are forgotten and up to that many extra
sends are admitted once. Cloudflare's own daily limit still answers 429, which pauses sending and defers the outbox row.
