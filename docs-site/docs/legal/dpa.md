---
title: Data Processing Agreement
---

# Data Processing Agreement

**Effective date:** 2026-10-23

This Data Processing Agreement ("DPA") is part of the [Terms of Service](/legal/terms) between TerraScale ("Processor") and the customer ("Controller"). It applies when TerraScale processes personal data on the customer's behalf in providing the Service. It takes effect automatically when the customer accepts the Terms. To receive a countersigned copy, email [legal@terrascale.tech](mailto:legal@terrascale.tech) with your organization's legal name and address.

## 1. Definitions

"GDPR" means Regulation (EU) 2016/679 and the UK GDPR. "LGPD" means Brazilian Law No. 13,709/2018. "Data Protection Law" means GDPR, LGPD and any other law that applies to the processing. "Personal data", "processing", "controller", "processor", "data subject" and "personal data breach" have the meanings given in Data Protection Law. "Customer Personal Data" means personal data in Customer Data processed by Processor under the Terms.

## 2. Scope and roles

Controller determines the purposes and means of processing Customer Personal Data. Processor processes it only to provide the Service. Annex 1 describes the processing.

## 3. Processor obligations

Processor will:

1. process Customer Personal Data only on Controller's documented instructions, including those in the Terms and given through the dashboard and APIs, unless the law requires otherwise, in which case Processor informs Controller first unless the law forbids it;
2. tell Controller if it believes an instruction breaks Data Protection Law;
3. ensure personnel who access Customer Personal Data are bound by confidentiality;
4. implement the security measures in Annex 2;
5. assist Controller, taking into account the nature of processing, with data subject requests, security, breach notification, data protection impact assessments and consultations with authorities;
6. keep Customer Personal Data in the regions Controller selects for each database, except encrypted backups as listed in Annex 3.

## 4. Subprocessors

Controller authorizes the subprocessors listed in Annex 3. Processor will impose data protection obligations on each subprocessor that are at least as protective as this DPA and remains liable for their performance. Processor will give at least 30 days' notice of a new or replacement subprocessor on the [Privacy Policy](/legal/privacy#subprocessors) and by email to account owners. Controller may object on reasonable data protection grounds within that period; if the parties cannot resolve the objection, Controller may terminate the affected Service and receive a pro-rata refund of prepaid fees for it.

## 5. Personal data breaches

Processor will notify Controller without undue delay, and in any case within 48 hours, after becoming aware of a personal data breach affecting Customer Personal Data. The notice will describe the breach, the categories and approximate number of data subjects and records concerned, likely consequences and the measures taken or proposed.

## 6. International transfers

Where Customer Personal Data is transferred outside the EEA, the UK or Brazil to a country without an adequacy decision, the parties agree to the European Commission's Standard Contractual Clauses (Decision 2021/914), Module 2 (controller to processor) and, where applicable, Module 3 (processor to processor), with the UK Addendum, and to the standard contractual clauses approved by the ANPD for LGPD transfers. The clauses are incorporated by reference, with Annexes 1 to 3 completing their appendices. Option 2 of Clause 9 (general authorization) and the optional Clause 7 apply; Clause 11's option does not; the governing law and courts are those of Ireland for EU transfers.

## 7. Audits

Processor will make available information reasonably needed to demonstrate compliance with this DPA, including summaries of security assessments. If that is not enough, Controller may audit Processor once per year with 30 days' notice, during business hours, at its own cost, and under confidentiality, or more often when a supervisory authority requires it or after a personal data breach.

## 8. Deletion and return

During the term, Controller can export and delete Customer Personal Data through the Service. After termination, Processor deletes Customer Personal Data as described in the Terms, unless the law requires it to keep a copy.

## 9. Liability and precedence

Each party's liability under this DPA is subject to the limits in the Terms, except where Data Protection Law forbids them. If this DPA conflicts with the Terms, this DPA prevails; if it conflicts with the Standard Contractual Clauses, the clauses prevail.

## Annex 1: Description of processing

- **Subject matter and duration:** providing the Service for the term of the Terms, plus the deletion period.
- **Nature and purpose:** storage, replication, backup, querying, retrieval and deletion of Customer Data as directed by Controller.
- **Data subjects:** determined by Controller; typically Controller's end users, employees and customers.
- **Categories of personal data:** determined by Controller; any personal data Controller stores in the Service.
- **Special categories:** only if Controller chooses to store them; Controller is responsible for any additional safeguards required.
- **Frequency:** continuous.

## Annex 2: Security measures

- Encryption in transit with TLS 1.2 or later and encryption of stored data and backups at rest.
- Tenant isolation, scoped API credentials with expiry and revocation, and least-privilege staff access with multi-factor authentication.
- Logging of administrative access and security monitoring.
- Point-in-time recovery backups retained per plan, with regular restore testing.
- Vulnerability management and prompt patching of the Service and its dependencies.
- Incident response procedures with the breach notice in section 5.

## Annex 3: Authorized subprocessors

| Subprocessor | Purpose | Location |
| --- | --- | --- |
| Oracle Cloud Infrastructure | database and application hosting | regions selected by Controller: Brazil (São Paulo), United States (San Francisco Bay Area), Germany (Frankfurt) |
| Cloudflare | content delivery, DNS, DDoS protection | global edge network |
| Tigris Data | encrypted backups and object storage | United States and the region nearest the database |
| Stripe | payments and invoicing (account billing data only) | United States, Ireland |
