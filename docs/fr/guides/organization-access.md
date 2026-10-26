---
title: Organization access and ownership
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Organization access and ownership

An organization groups its projects, members and billing. Choose the organization in the dashboard before changing access; an organization role applies across its projects. For one-project access, use a project collaborator role instead of an organization invitation.

## Choose a role

The dashboard uses these permissions. Resource operations also require the relevant plan allowance and project selection.

| Dashboard action | Owner | Admin | Member | Viewer |
| --- | --- | --- | --- | --- |
| View accessible projects and organization members | Yes | Yes | Yes | Yes |
| Read selected project data | Yes | Yes | Yes | Yes |
| Write selected project data | Yes | Yes | Yes | No |
| Create or change projects and databases | Yes | Yes | No | No |
| Manage project API keys | Yes | Yes | No | No |
| Rename organization; invite, change or remove other non-owner members | Yes | Yes | No | No |
| View invitations and organization audit log | Yes | Yes | No | No |
| Change billing, transfer ownership or delete a business organization | Yes | No | No | No |

Use **Viewer** for read access, **Member** for application data work, and **Admin** for organization management. Keep ownership with someone responsible for billing and account recovery. Admins cannot appoint an owner by changing a role.

### Dashboard policy and identity API policy

The identity API permits an organization **Member** to create a project identity. Dashboard and MCP project creation require **Owner** or **Admin**, plus provisioning eligibility and the plan's project allowance. A project identity alone does not establish database capacity or authorize billing changes. Use the dashboard's [Projects page](https://dash.terrascale.tech/organization?tab=projects) for a project with its region and provisioning settings.

Identity API project roles are separate from organization roles. A project-only collaborator can access the assigned project, not sibling projects or organization member, invitation, audit or billing management. A project **Member** can create a service key through the identity API; dashboard key management requires an organization owner or admin. Key permissions remain independent of the person's role.

Project-only access currently works through the identity API; the organization dashboard may not list a project-only collaborator's organization. Do not grant organization-wide membership merely to make a project appear. Use the existing project-scoped API access and ask support if dashboard access is required. [MCP scope selection](/fr/guides/mcp#3-choose-the-organization-and-project) does not grant additional permissions.

## Invite and manage teammates

1. Select the intended organization and open [Members](https://dash.terrascale.tech/organization?tab=members).
2. As an owner or admin, enter the teammate's email under **Invite a teammate**, choose the least privileged role and select **Create invitation**. The native form creates organization-wide membership, not project-only access.
3. Share the generated link privately with that teammate. Invitations are emailed too; the link is single-use and expires after **seven days**. Treat it as a credential.
4. The recipient opens the link, signs in with the **invited email**, reviews the organization and role, then selects **Accept invitation** or **Decline**. A new customer creates an account with that email and reopens the link. If the wrong account is signed in, use **Sign out and switch account**.
5. Confirm the accepted teammate appears in **Organization members** with the intended role. Acceptance is required; creating an invitation alone grants no membership.

Use the Invitations list to review status and expiry. **Resend** replaces the link and invalidates the previous one while preserving its role and project scope. Send the replacement, not a saved old link. **Cancel** revokes an open invitation. For an expired, canceled, declined or already-used link, ask an owner or admin for a new invitation; do not keep retrying acceptance.

An owner or admin can change another non-owner member's role or remove them from the member's action menu. The dashboard prevents changing yourself or the owner this way. Recheck access after any role change; a previously issued personal token is not a permanent grant of the old role.

## Plan capacity

Allowances apply per organization, shared by its projects. Pending invitations reserve member capacity; revoke invitations you no longer need. If a resend fails, check the invitation status before issuing another link.

| Plan | Projects | Members, including the owner |
| --- | --- | --- |
| Free | 1 | 1 |
| Starter | 5 | 5 |
| Pro | 25 | 25 |
| Team | 100 | 100 |
| Enterprise | Contact sales | Contact sales |

Free's one-member allowance is already occupied by its owner. Review [current pricing](/fr/pricing/) before inviting a teammate or creating another project. Business organizations require active billing to provision projects; the personal Free onboarding path does not require a payment card.

## Transfer ownership and leave

Ownership transfer is a two-step operation for a business organization:

1. The current owner opens [Danger zone](https://dash.terrascale.tech/organization?tab=danger), selects **Transfer ownership**, chooses another active member and selects **Request transfer**. Invite and onboard the nominee first if necessary.
2. The nominated member opens the same organization's **Danger zone** and selects **Accept ownership** before the displayed expiry. Only that member can accept. The original owner remains owner until acceptance; after acceptance, the nominee is owner and the previous owner becomes an admin.

The owner can **Cancel transfer**, or the nominee can **Decline**. Verify the new owner and billing responsibility before the previous owner leaves. A last owner cannot be removed, demoted or leave without a replacement; an invitation or pending transfer alone is not a replacement owner.

A non-owner can leave a business organization from **Danger zone** when another accessible organization remains. Personal organizations are tied to their account and cannot be left, transferred or deleted through the business-organization lifecycle. Use the [profile](https://dash.terrascale.tech/profile?tab=danger) account-deletion flow when closing a personal account.

## Remove access safely

Removing a member or collaborator removes their membership access. It does **not** revoke independent organization/project service keys they created or copied. As an owner or admin:

1. Review the departing person's responsibilities and application secrets before removal.
2. Remove the membership and verify the person no longer has access to the affected organization or project.
3. Review [API keys](https://dash.terrascale.tech/api-keys), rotate keys they could know, update application secret stores and revoke the old keys. Choose a sequence that preserves required application availability.
4. Review the [audit log](https://dash.terrascale.tech/organization?tab=audit) for membership, role and key changes, and verify the applications work with the replacement credentials.

Keep human membership, [personal MCP credentials](/fr/guides/mcp) and [application API keys](/fr/guides/authentication) separate when reviewing access. Deleting a person is not a substitute for credential rotation.

</div>
