---
title: Data deletion and erasure timelines
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Data deletion and erasure timelines

**Implementation reviewed:** 2026-10-23

Export any data you need before deleting it. For an erasure request, contact [privacy@terrascale.tech](mailto:privacy@terrascale.tech).

TerraScale provides the Service under Brazilian law, including the Lei Geral de Proteção de Dados (LGPD), and the GDPR where applicable. TerraScale is the controller for account data and the processor for personal data you store in your databases. See the [Privacy Policy](/ru/legal/privacy) and [Data Processing Agreement](/ru/legal/dpa).

Removing data from normal reads, removing retained history and erasing every physical copy are separate steps. A successful delete or a missing record is not proof that all copies have been erased.

## Data and backups

SQL deletion and backup-retention details are being updated. Contact
[privacy@terrascale.tech](mailto:privacy@terrascale.tech) for confirmation of
erasure covering live data, retained metadata and backups.

## Existing legal commitments

The [Terms of Service, section 6](/ru/legal/terms#_6-data-export-and-deletion) currently state that Customer Data is deleted from active systems within **30 days** of account closure or database deletion, and from backups within the plan's PITR window plus **30 days**. The [Privacy Policy](/ru/legal/privacy#retention) states that account data is deleted within **30 days** of closure.

Those are the published commitments. The implementation reviewed above does not prove a bounded completion time for all organization cleanup or physical copies. This page does not replace those commitments or certify that their deadlines have been met. An erasure request needs confirmation of the affected copies and any outstanding cleanup. Retention required by applicable law remains subject to the DPA and Privacy Policy.

## Request deletion or confirmation

1. Email [privacy@terrascale.tech](mailto:privacy@terrascale.tech) with the account or organization concerned and the scope of the request: rows, databases, organization records or account closure. Include resource identifiers where available; do not send passwords, tokens or row contents.
2. If the personal data is held in another customer's database, contact that customer as controller. TerraScale assists the controller under the [DPA](/ru/legal/dpa#_8-deletion-and-return).
3. Request confirmation covering live data, retained metadata, backups and any remaining copies or legally required retention. A response to the request is separate from confirmation of complete erasure; the response period in the [Privacy Policy](/ru/legal/privacy#your-rights) is not a physical purge deadline.

</div>
