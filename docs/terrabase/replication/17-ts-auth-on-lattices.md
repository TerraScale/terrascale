# 17. ts-auth on lattices

- **Refresh tokens.** Each token document has `uses`, an add-wins set. A refresh does `add uses <child token id>`.
  - Locally: if the returned set already had another element, it is reuse, and the token family is revoked (a plain
    write).
  - Across regions: two concurrent refreshes each see one element. After the merge the set holds two. ts-auth tails
    `GET …/changes` on its database, sees `uses` with more than 1 element and revokes the family.

  The window is about one replication delay (the plan accepts about 1 s).
- **One-time codes:** the same pattern with `uses` = a set of request ids.
- **Lockout:** `inc failed_logins 1` on each failure, `set failed_logins 0` on success (a new epoch resets it). The
  lockout decision reads the merged value.
- ts-auth's compare-and-set paths are deleted (slice R12).
