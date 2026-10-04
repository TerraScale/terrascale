---
---
<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

# Choosing durability across Regions (draft)

> Draft. The required guarantees still need production qualification.

Choose database Regions according to your users and residency requirements.
The placement is immutable and applies to backups, restores, exports, and
branches as well as ordinary database storage.

RegionalDurable acknowledges durability within the accepting Region;
GlobalDurable requires every selected Region and the write’s causal
dependencies. A stronger acknowledgement may be unavailable during an outage.
The [replication guide](/fr/concepts/replication) explains sessions, uncertainty,
and the limits of cross-Region conflict resolution. Neither option promises
linearizable reads or global uniqueness.

Writes count one operation unit per 4 KiB in every selected Region, and
storage counts in every selected Region, before included allowances. Review [pricing](/fr/pricing/) before choosing placement.
