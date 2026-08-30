//! The Backups and deletion page.
//!
//! The backup half configures the S3 recovery-point repository, drives full
//! and incremental uploads, verifies remote chains, and restores into a fresh
//! location. The deletion half drives the deletion domain's typed workflow
//! states end to end.

use std::collections::BTreeSet;
use std::path::PathBuf;

use terrabase::backup::{BackupKind, BackupResult};
use terrabase::deletion::{
    CopyClass, CopyOutcome, CopyReceipt, CopyTarget, DeletionRequestId, DeletionScope,
    DeletionState, DeletionWorkflow, GlobalDurableAdmission, HoldAuthorityId, HoldKind, HoldRecord,
};

use crate::html::{self, ButtonStyle, Tone};
use crate::s3_backup::{RemoteRecoveryPoint, S3BackupSettings};
use crate::state::StudioState;

/// The identifier the chain listing is merged into.
pub const CHAIN_ID: &str = "backup-chain";
/// The identifier the deletion workflow list is merged into.
pub const DELETION_ID: &str = "deletion-state";

/// Renders the Backups and deletion page.
pub fn render(
    state: &StudioState,
    points: Result<&[RemoteRecoveryPoint], &str>,
    nonce: &str,
) -> String {
    let body = format!(
        "{heading}\n\
{configuration}\n\
<div class=\"grid gap-6 lg:grid-cols-2\">{run_backup}{restore}</div>\n\
{chain}\n\
{deletion}\n\
{limits}",
        heading = html::heading(
            "Backups & deletion",
            "Incremental recovery points stored in S3, one-click restore, and the deletion domain's typed workflow states.",
        ),
        configuration = configuration_card(state),
        run_backup = backup_card(state),
        restore = restore_card(state, points.as_ref().ok().copied()),
        chain = chain_card(points),
        deletion = deletion_card(state),
        limits = limits_card(),
    );
    html::page(nonce, "Backups & deletion", "/backups", "", &body)
}

fn configuration_card(state: &StudioState) -> String {
    let settings = state.backup_settings();
    let checked = |selected: bool| if selected { " checked" } else { "" };
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Use a bucket prefix dedicated to this database. Studio uploads archive members first and the content-bound manifest last. Credentials come from workload identity, an ECS or EC2 role, or standard <code>AWS_*</code> environment variables. Studio never writes access keys to disk.</p>\n\
<form class=\"grid gap-4\" data-on:submit=\"@post('/backup/configure', {{contentType: 'form', retry: 'never'}})\">\n\
<div class=\"grid gap-4 md:grid-cols-3\">{bucket}{region}{prefix}</div>\n\
<div class=\"grid gap-4 md:grid-cols-2\">{endpoint}{kms}</div>\n\
<div class=\"grid gap-4 md:grid-cols-2\">{incremental}{full}</div>\n\
<div class=\"flex flex-wrap gap-5 text-sm\">\n\
<label class=\"flex items-center gap-2\"><input type=\"checkbox\" name=\"enabled\"{enabled}> Run scheduled backups</label>\n\
<label class=\"flex items-center gap-2\"><input type=\"checkbox\" name=\"virtual_hosted_style\"{virtual_hosted}> Virtual-hosted requests</label>\n\
<label class=\"flex items-center gap-2\"><input type=\"checkbox\" name=\"allow_http\"{allow_http}> Allow HTTP for a local S3-compatible store</label>\n\
</div>\n\
{confirmation}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Save and test S3</button>\n\
</form>\n{receipt}",
        bucket = html::text_input("bucket", "Bucket", &settings.bucket, "terrabase-backups"),
        region = html::text_input("region", "Region", &settings.region, "us-east-1"),
        prefix = html::text_input(
            "prefix",
            "Database prefix",
            &settings.prefix,
            "production/database-01",
        ),
        endpoint = html::text_input(
            "endpoint",
            "S3-compatible endpoint (optional)",
            &settings.endpoint,
            "https://s3.example.com",
        ),
        kms = html::text_input(
            "kms_key_id",
            "KMS key id (optional)",
            &settings.kms_key_id,
            "alias/terrabase-backups",
        ),
        incremental = html::number_input(
            "incremental_interval_minutes",
            "Incremental interval (minutes)",
            &settings.incremental_interval_minutes.to_string(),
        ),
        full = html::number_input(
            "full_interval_hours",
            "Full recovery point interval (hours)",
            &settings.full_interval_hours.to_string(),
        ),
        enabled = checked(settings.enabled),
        virtual_hosted = checked(settings.virtual_hosted_style),
        allow_http = checked(settings.allow_http),
        confirmation = html::confirmation_fields("backup settings", "configure backup"),
        primary = ButtonStyle::Primary.classes(),
        receipt = html::receipt_region_with("backup-configuration-receipt"),
    );
    html::card("S3 setup", &body)
}

fn backup_card(state: &StudioState) -> String {
    let tenants = state.tenants().join(", ");
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Back up now creates incrementals until the configured full interval expires. A full point is self-contained. Local files exist only in disposable transfer scratch space and are removed after S3 commits the manifest. Leave the tenant list blank to include every tenant.</p>\n\
<form class=\"grid gap-4\" data-on:submit=\"@post('/backup/run', {{contentType: 'form', retry: 'never'}})\">\n{destination}{tenants}{confirmation}\
<div class=\"flex flex-wrap gap-2\">\n\
<button type=\"submit\" name=\"mode\" value=\"automatic\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Back up now</button>\n\
<button type=\"submit\" name=\"mode\" value=\"full\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary} w-fit\">Create full point</button>\n\
</div>\n\
</form>\n{receipt}",
        destination = html::alert(
            if state.backup_settings().enabled { Tone::Good } else { Tone::Neutral },
            "S3 destination",
            &configured_destination(&state.backup_settings()),
        ),
        tenants = html::text_input("tenants", "Tenants (comma-separated)", &tenants, "demo"),
        confirmation = html::confirmation_fields("backup repository", "run backup"),
        primary = ButtonStyle::Primary.classes(),
        secondary = ButtonStyle::Secondary.classes(),
        receipt = html::receipt_region_with("backup-receipt"),
    );
    html::card("Run a recovery point", &body)
}

fn restore_card(state: &StudioState, points: Option<&[RemoteRecoveryPoint]>) -> String {
    let mut restore_target = state.config.data_dir.clone();
    let mut restored_name = restore_target
        .file_name()
        .map(std::ffi::OsStr::to_os_string)
        .unwrap_or_else(|| std::ffi::OsString::from("terrabase"));
    restored_name.push("-restored");
    restore_target.set_file_name(restored_name);
    let restore_target = restore_target.display().to_string();
    let mut recovery_points = html::option("", "Latest committed recovery point", true);
    if let Some(points) = points {
        for point in points.iter().rev() {
            let manifest = point.manifest();
            recovery_points.push_str(&html::option(
                manifest.identity(),
                &format!(
                    "{} · {} · sequence {}",
                    short_identity(manifest.identity()),
                    kind_name(&manifest.kind()),
                    manifest.boundary_sequence().get(),
                ),
                false,
            ));
        }
    }
    let body = format!(
        "<p class=\"mb-4 text-sm text-muted-foreground\">Restore downloads only the selected point and its ancestors from S3, verifies every member, and writes a brand-new engine location. The target must not exist or must be empty.</p>\n\
<form class=\"grid gap-4\" data-on:submit=\"if (!confirm('Restore writes a new engine location. Continue?')) return; @post('/backup/restore', {{contentType: 'form', retry: 'never'}})\">\n{destination}{target}{identity}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Restore latest (or by identity)</button>\n\
</form>\n{receipt}\n{note}",
        destination = html::alert(
            Tone::Neutral,
            "Source",
            &configured_destination(&state.backup_settings()),
        ),
        target = format!(
            "{}{}",
            html::text_input("target", "Restore target", &restore_target, "/path/to/new/location",),
            html::confirmation_fields(&restore_target, "restore recovery point"),
        ),
        identity = html::select("identity", "Recovery point", &recovery_points),
        primary = ButtonStyle::Primary.classes(),
        receipt = html::receipt_region_with("restore-receipt"),
        note = html::alert(
            Tone::Neutral,
            "The studio keeps serving the location it opened",
            "A restore creates a new location; the running studio does not switch to it. Reopen the studio with TERRABASE_STUDIO_DIR pointing at the restored directory to inspect it.",
        ),
    );
    html::card("Restore", &body)
}

fn chain_card(points: Result<&[RemoteRecoveryPoint], &str>) -> String {
    let body = format!(
        "<div class=\"mb-4 flex flex-wrap gap-2\">\n\
<button type=\"button\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\" data-on:click=\"@post('/backup/verify', {{payload: {{}}, retry: 'never'}})\">Verify the chain</button>\n\
</div>\n<div id=\"{CHAIN_ID}\">{chain}</div>",
        secondary = ButtonStyle::Secondary.classes(),
        CHAIN_ID = CHAIN_ID,
        chain = chain_fragment(points),
    );
    html::card("Recovery points", &body)
}

/// Renders the manifests committed in the configured S3 prefix.
pub fn chain_fragment(points: Result<&[RemoteRecoveryPoint], &str>) -> String {
    match points {
        Ok([]) => html::alert(
            Tone::Neutral,
            "",
            "The S3 prefix holds no recovery point. Run a full point first.",
        ),
        Ok(points) => {
            let mut rows = String::new();
            for point in points.iter().rev() {
                let manifest = point.manifest();
                rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
                rows.push_str(&html::cell(&short_identity(manifest.identity())));
                rows.push_str(&html::cell(&kind_name(&manifest.kind())));
                rows.push_str(&html::cell(&manifest.boundary_sequence().get().to_string()));
                rows.push_str(&html::cell(point.modified_at()));
                rows.push_str(&html::cell(&manifest.documents().to_string()));
                rows.push_str(&html::cell(&manifest.deletes().to_string()));
                rows.push_str(&html::cell(&html::human_bytes(manifest.total_bytes())));
                rows.push_str(&html::cell(&manifest.tenants().len().to_string()));
                rows.push_str(&html::cell(&manifest.members().len().to_string()));
                rows.push_str("</tr>");
            }
            html::table(
                &[
                    "identity",
                    "kind",
                    "boundary",
                    "committed at",
                    "documents",
                    "deletes",
                    "bytes",
                    "tenants",
                    "members",
                ],
                &[rows],
                "No recovery point.",
            )
        }
        Err(failure) => html::alert(Tone::Bad, "Cannot read the S3 recovery points", failure),
    }
}

fn short_identity(identity: &str) -> String {
    if identity.len() <= 12 {
        identity.to_owned()
    } else {
        format!("{}…{}", &identity[..6], &identity[identity.len() - 6..])
    }
}

fn kind_name(kind: &BackupKind) -> String {
    match kind {
        BackupKind::Full => "full".to_owned(),
        BackupKind::Incremental { base } => format!("incremental from {}", base.get()),
        BackupKind::Snapshot => "snapshot".to_owned(),
    }
}

/// Renders one backup result's typed receipt.
pub fn backup_receipt(result: &BackupResult, destination: &str, uploaded_objects: usize) -> String {
    let manifest = result.manifest();
    html::definition(&[
        ("recovery point", manifest.identity().to_owned()),
        ("kind", kind_name(&manifest.kind())),
        ("boundary sequence", manifest.boundary_sequence().get().to_string()),
        ("documents", manifest.documents().to_string()),
        ("deletes", manifest.deletes().to_string()),
        ("family records", manifest.family_records().to_string()),
        ("disclosures", manifest.disclosures().to_string()),
        ("bytes", html::human_bytes(manifest.total_bytes())),
        ("tenants", manifest.tenants().len().to_string()),
        ("members", manifest.members().len().to_string()),
        ("source reads", result.source_reads().to_string()),
        ("skipped", result.skipped_total().to_string()),
        ("objects uploaded", uploaded_objects.to_string()),
        ("destination", destination.to_owned()),
    ])
}

fn configured_destination(settings: &S3BackupSettings) -> String {
    if settings.bucket.is_empty() {
        "Not configured. Save and test an S3 bucket first.".to_owned()
    } else {
        format!("s3://{}/{}", settings.bucket, settings.prefix)
    }
}

/// Splits a comma-separated tenant list into validated tenant identifiers.
pub fn parse_tenants(text: &str) -> Result<Vec<terrabase::TenantId>, String> {
    let mut tenants = Vec::new();
    for name in text.split(',').map(str::trim).filter(|text| !text.is_empty()) {
        tenants.push(
            terrabase::TenantId::try_new(name)
                .map_err(|error| format!("tenant `{name}`: {error:?}"))?,
        );
    }
    Ok(tenants)
}

/// The restore target as an absolute path, refusing an empty value.
pub fn restore_target(text: &str) -> Result<PathBuf, String> {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return Err("the restore target cannot be empty".to_owned());
    }
    Ok(PathBuf::from(trimmed))
}

fn deletion_card(state: &StudioState) -> String {
    let body = format!(
        "<form class=\"grid gap-4 sm:grid-cols-4 items-end\" data-on:submit=\"@post('/deletion/request', {{contentType: 'form', retry: 'never'}})\">\n\
{request_id}{tenant}{subject}{regions}{confirmation}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Open a deletion request</button>\n\
</form>\n\
<div id=\"{DELETION_ID}\" class=\"mt-4\">{state_fragment}</div>",
        request_id = html::text_input("request_id", "Request id", "del-1", "del-1"),
        tenant = html::text_input(
            "tenant",
            "Tenant",
            &state.tenants().first().cloned().unwrap_or_else(|| "demo".to_owned()),
            "demo"
        ),
        subject = html::text_input("subject", "Subject id", "", "subject-7"),
        regions = html::text_input("regions", "Regions (comma-separated)", "gru, sfo", "gru, sfo"),
        confirmation = html::confirmation_fields("del-1", "request deletion"),
        primary = ButtonStyle::Primary.classes(),
        DELETION_ID = DELETION_ID,
        state_fragment = deletion_fragment(state),
    );
    html::card("Deletion workflows", &body)
}

/// Renders every workflow the studio holds, with its state and next steps.
pub fn deletion_fragment(state: &StudioState) -> String {
    let ledger = state.deletions.lock().unwrap_or_else(|poison| poison.into_inner());
    let workflows: Vec<&DeletionWorkflow> = ledger.workflows().collect();
    if workflows.is_empty() {
        return html::alert(
            Tone::Neutral,
            "",
            "No deletion workflow open. The domain's states run Requested → Admitted → Executing → Verified → Complete, with legal holds able to return a verified workflow to Executing.",
        );
    }
    let mut cards = String::new();
    for workflow in workflows {
        cards.push_str(&render_workflow(workflow));
    }
    format!("<div class=\"grid gap-6\">{cards}</div>", cards = cards)
}

fn render_workflow(workflow: &DeletionWorkflow) -> String {
    let id = workflow.request_id().as_str();
    let (tone, label) = state_badge(workflow.state());
    let mut rows = String::new();
    for target in workflow.plan() {
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&format!("{:?}", target.class())));
        rows.push_str(&html::cell(
            &target.region().map(|region| region.as_str().to_owned()).unwrap_or_default(),
        ));
        rows.push_str(&html::cell(target.name().as_str()));
        rows.push_str(&html::cell_raw(&format!(
            "<form data-on:submit=\"@post('/deletion/receipt', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"request_id\" value=\"{id}\">\
{confirmation}\
<button type=\"submit\" class=\"text-sm text-muted-foreground hover:text-foreground underline\">record receipt</button>\
</form>",
            id = html::attribute(id),
            confirmation = html::confirmation_fields(id, "record deletion receipt"),
        )));
        rows.push_str("</tr>");
    }
    let plan = html::table(&["class", "region", "name", ""], &[rows], "No copy target planned.");

    let mut definitions = vec![
        ("request", id.to_owned()),
        ("tenant", workflow.scope().tenant_id().as_str().to_owned()),
        ("subject", workflow.scope().subject_id().as_str().to_owned()),
        (
            "regions",
            workflow
                .selected_regions()
                .map(|region| region.as_str().to_owned())
                .collect::<Vec<_>>()
                .join(", "),
        ),
        ("receipts recorded", workflow.receipts().count().to_string()),
        ("holds", workflow.holds().count().to_string()),
        ("missing targets", workflow.missing_targets().count().to_string()),
    ];
    if workflow.state().is_terminal() {
        definitions.push(("terminal", "yes".to_owned()));
    }

    let controls = format!(
        "<div class=\"mt-4 flex flex-wrap gap-2\">\n{admit}{verify}{complete}{hold}{end_hold}</div>",
        admit = action_button(id, "Admit", "/deletion/admit", ButtonStyle::Primary),
        verify = action_button(id, "Verify", "/deletion/verify", ButtonStyle::Primary),
        complete = action_button(id, "Complete", "/deletion/complete", ButtonStyle::Destructive),
        hold = action_button(id, "Add a legal hold", "/deletion/hold", ButtonStyle::Secondary),
        end_hold = action_button(id, "End the hold", "/deletion/end-hold", ButtonStyle::Secondary),
    );

    format!(
        "<div class=\"rounded-lg border p-4 grid gap-4\"><div class=\"flex items-center gap-2 flex-wrap\">{badge}<span class=\"text-xs text-muted-foreground\">state</span></div>{definition}{plan}{controls}</div>",
        badge = html::badge(tone, label),
        definition = html::definition(&definitions),
        plan = plan,
        controls = controls,
    )
}

fn action_button(id: &str, label: &str, path: &str, style: ButtonStyle) -> String {
    let typed_confirmation = match path {
        "/deletion/admit" => html::confirmation_fields(id, "admit deletion"),
        "/deletion/verify" => html::confirmation_fields(id, "verify deletion"),
        "/deletion/end-hold" => html::confirmation_fields(id, "end deletion hold"),
        "/deletion/complete" => html::confirmation_fields(id, "complete deletion"),
        _ => String::new(),
    };
    format!(
        "<form data-on:submit=\"@post('{path}', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"request_id\" value=\"{id}\">\
{typed_confirmation}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {classes}\">{label}</button>\
</form>",
        path = path,
        id = html::attribute(id),
        classes = style.classes(),
        label = html::esc(label),
        typed_confirmation = typed_confirmation,
    )
}

fn state_badge(state: DeletionState) -> (Tone, &'static str) {
    match state {
        DeletionState::Requested => (Tone::Neutral, "requested"),
        DeletionState::Admitted => (Tone::Neutral, "admitted"),
        DeletionState::Executing => (Tone::Warn, "executing"),
        DeletionState::Verified => (Tone::Good, "verified"),
        DeletionState::Complete => (Tone::Good, "complete"),
        DeletionState::Failed(_) => (Tone::Bad, "failed"),
    }
}

/// Opens a deletion workflow from the submitted form.
pub fn workflow_from_parts(
    request_id: &str,
    tenant: &str,
    subject: &str,
    regions: &str,
    now_millis: u64,
) -> Result<DeletionWorkflow, String> {
    let request = DeletionRequestId::try_new(request_id.trim())
        .map_err(|error| format!("request id: {error:?}"))?;
    let tenant = terrabase::TenantId::try_new(tenant.trim())
        .map_err(|error| format!("tenant: {error:?}"))?;
    let scope = DeletionScope::try_new(tenant, subject.trim())
        .map_err(|error| format!("deletion scope: {error:?}"))?;
    let mut selected = BTreeSet::new();
    for region in regions.split(',').map(str::trim).filter(|text| !text.is_empty()) {
        selected.insert(
            terrabase::RegionId::try_new(region)
                .map_err(|error| format!("region `{region}`: {error:?}"))?,
        );
    }
    DeletionWorkflow::request(request, scope, selected, now_millis, now_millis)
        .map_err(|error| format!("the request was refused: {error:?}"))
}

/// Admits a workflow, planning one live copy target per selected region.
pub fn admit_workflow(workflow: &mut DeletionWorkflow, now_millis: u64) -> Result<String, String> {
    let regions: BTreeSet<terrabase::RegionId> = workflow.selected_regions().cloned().collect();
    let mut plan = BTreeSet::new();
    for region in &regions {
        plan.insert(
            CopyTarget::try_new_live(region.clone(), &format!("copy-{}", region.as_str()))
                .map_err(|error| format!("copy target: {error:?}"))?,
        );
    }
    workflow
        .admit(GlobalDurableAdmission::new(regions, now_millis), plan)
        .map_err(|error| format!("the admission was refused: {error:?}"))?;
    Ok(format!(
        "admitted with {} planned copy target(s); the workflow is now Executing",
        workflow.plan().count()
    ))
}

/// Records one receipt for the first target still missing one.
pub fn record_receipt(workflow: &mut DeletionWorkflow, now_millis: u64) -> Result<String, String> {
    let Some(target) = workflow.missing_targets().next() else {
        return Ok("every planned target already has a receipt".to_owned());
    };
    let outcome = if target.class() == CopyClass::BackupRotation {
        CopyOutcome::RotatedOut
    } else {
        CopyOutcome::Removed
    };
    let name = target.name().as_str().to_owned();
    workflow
        .record_receipt(CopyReceipt::new(target.clone(), outcome, now_millis))
        .map_err(|error| format!("the receipt was refused: {error:?}"))?;
    Ok(format!(
        "receipt recorded for `{name}` ({outcome:?}); {} target(s) still missing",
        workflow.missing_targets().count()
    ))
}

/// Adds a legal hold that blocks completion until it ends.
pub fn add_hold(workflow: &mut DeletionWorkflow, now_millis: u64) -> Result<String, String> {
    let ends = now_millis.saturating_add(3_600_000);
    workflow
        .record_hold(
            HoldRecord::try_new(HoldKind::Legal, "studio-hold", now_millis, ends, &["tenant_id"])
                .map_err(|error| format!("the hold is invalid: {error:?}"))?,
        )
        .map_err(|error| format!("the hold was refused: {error:?}"))?;
    Ok(format!(
        "legal hold recorded by `studio-hold`, retaining tenant_id until it ends; {} unexpired hold(s)",
        workflow.unexpired_holds(now_millis).len()
    ))
}

/// Ends the hold the studio recorded.
pub fn end_hold(workflow: &mut DeletionWorkflow, now_millis: u64) -> Result<String, String> {
    let authority = HoldAuthorityId::try_new("studio-hold")
        .map_err(|error| format!("hold authority: {error:?}"))?;
    let outcome = workflow
        .end_hold(&authority, now_millis)
        .map_err(|error| format!("the hold could not end: {error:?}"))?;
    Ok(format!("hold outcome: {outcome:?}"))
}

/// Verifies a workflow, reporting the deadline facts it checked.
pub fn verify_workflow(workflow: &mut DeletionWorkflow, now_millis: u64) -> Result<String, String> {
    let verification =
        workflow.verify(now_millis).map_err(|error| format!("verification refused: {error:?}"))?;
    Ok(format!(
        "verified: {} receipt(s), live deadline {} ms, backup deadline {} ms",
        verification.receipt_count(),
        verification.live_deadline_at_millis(),
        verification.backup_deadline_at_millis(),
    ))
}

/// Completes a workflow terminally.
pub fn complete_workflow(
    workflow: &mut DeletionWorkflow,
    now_millis: u64,
) -> Result<String, String> {
    let completion =
        workflow.complete(now_millis).map_err(|error| format!("completion refused: {error:?}"))?;
    Ok(format!(
        "complete: request `{}` finished at {} with {} receipt(s)",
        completion.request_id().as_str(),
        completion.completed_at_millis(),
        completion.receipt_count(),
    ))
}

fn limits_card() -> String {
    html::card(
        "Honest limits of this surface",
        &format!(
            "<div class=\"grid gap-3\">{schedule}{retention}{restore_note}</div>",
            schedule = html::alert(
                Tone::Neutral,
                "Scheduler lifetime",
                "Scheduled uploads run while this Studio process is alive. A production host should call the same S3 adapter in its engine process so backups read the live engine instead of opening its data directory a second time.",
            ),
            retention = html::alert(
                Tone::Neutral,
                "Retention belongs to the bucket",
                "Configure S3 lifecycle rules for this dedicated prefix. Expire a complete full point and its following incrementals together. Bucket versioning and object lock protect against accidental or hostile replacement.",
            ),
            restore_note = html::alert(
                Tone::Neutral,
                "Deletion workflows are studio-held",
                "The deletion domain is a pure state machine with no engine-attached store, so the workflows on this page live in the studio's memory. Each one can be encoded and decoded with the domain's own checksum, but nothing durable holds them here.",
            ),
        ),
    )
}
