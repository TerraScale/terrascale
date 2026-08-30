//! The Tiers page: the Storage Tier Mover's typed administration handle.

use terrabase::tiers::{TierAdminError, TierDecision, TierMoveRunReceipt, TierPolicy};

use crate::html::{self, ButtonStyle, Tone};
use crate::state::StudioState;

/// The identifier the tier state is merged into.
pub const TIER_ID: &str = "tier-state";

/// Renders the Tiers page.
pub fn render(state: &StudioState) -> String {
    let body = format!(
        "{heading}\n\
{state_card}\n\
<div class=\"grid gap-6 lg:grid-cols-2\">{configure}{control}</div>\n\
{receipt}\n\
{limits}",
        heading = html::heading(
            "Tiers",
            "Configure the Storage Tier Mover, pause and resume it, and run or explain one bounded pass. Every action answers with the mover's typed receipt.",
        ),
        state_card = state_card(state),
        configure = configure_card(),
        control = control_card(),
        receipt = html::receipt_region_with("tier-receipt"),
        limits = limits_card(),
    );
    html::page("Tiers", "/tiers", "", &body)
}

fn state_card(state: &StudioState) -> String {
    let administration = state.engine.tiers();
    let view = administration.inspect();
    let (tone, label) = if view.paused() {
        (Tone::Warn, "paused")
    } else if view.configured() {
        (Tone::Good, "configured and running passes on demand")
    } else {
        (Tone::Neutral, "unconfigured")
    };
    let policy = administration.policy();
    let mut definitions = vec![
        ("state", String::new()),
        (
            "policy",
            policy.as_ref().map(|p| p.policy_id().to_owned()).unwrap_or_else(|| "none".to_owned()),
        ),
        ("manifest epoch", view.manifest_epoch().to_string()),
        ("objects", view.object_count().to_string()),
        ("NVMe resident", html::human_bytes(view.nvme_bytes())),
    ];
    if let Some(policy) = &policy {
        definitions.push(("policy version", policy.version().to_string()));
        definitions.push(("NVMe budget", html::human_bytes(policy.nvme_budget_bytes())));
        definitions.push(("pinned segments", policy.pinned_segment_ids().len().to_string()));
    }
    let body = format!(
        "<div id=\"{TIER_ID}\" class=\"grid gap-4\"><div class=\"flex items-center gap-2\">{}</div>{}</div>",
        html::badge(tone, label),
        html::definition(&definitions),
        TIER_ID = TIER_ID,
    );
    html::card("Mover state", &body)
}

fn configure_card() -> String {
    let body = format!(
        "<form class=\"grid gap-4\" data-on:submit=\"@post('/tiers/configure', {{contentType: 'form', retry: 'never'}})\">\n\
{policy_id}{version}{budget}{residence}{promotion}{pinned}\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary} w-fit\">Configure policy</button>\n\
</form>\n\
<p class=\"mt-3 text-xs text-muted-foreground\">Configuring replaces any prior policy and clears the pause flag: a fresh policy is fresh control. Two of the policy's knobs (minimum residence moves and promotion threshold) have no read accessor on the public type, so they are shown here as configured values rather than read back from the engine.</p>",
        policy_id = html::text_input("policy_id", "Policy id", "default", "default"),
        version = html::number_input("version", "Version (from 1)", "1"),
        budget = html::number_input("budget", "NVMe budget (bytes)", "1073741824"),
        residence = html::number_input("residence", "Minimum residence moves", "1"),
        promotion = html::number_input("promotion", "Promotion threshold", "1"),
        pinned = html::text_input("pinned", "Pinned segment ids (comma-separated)", "", "1, 2"),
        primary = ButtonStyle::Primary.classes(),
    );
    html::card("Configure", &body)
}

fn control_card() -> String {
    let body = format!(
        "<div class=\"flex flex-wrap gap-2\">\n\
<button type=\"button\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\" data-on:click=\"@post('/tiers/pause', {{payload: {{}}, retry: 'never'}})\">Pause</button>\n\
<button type=\"button\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\" data-on:click=\"@post('/tiers/resume', {{payload: {{}}, retry: 'never'}})\">Resume</button>\n\
<button type=\"button\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\" data-on:click=\"@post('/tiers/dry-run', {{payload: {{}}, retry: 'never'}})\">Explain (dry run)</button>\n\
<button type=\"button\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\" data-on:click=\"@post('/tiers/run', {{payload: {{}}, retry: 'never'}})\">Run one pass</button>\n\
</div>\n\
<p class=\"mt-3 text-xs text-muted-foreground\">Pause and resume return the new paused state, not whether a transition happened — both are idempotent. A dry run explains the next pass and still answers while paused; a run refuses while paused.</p>",
        primary = ButtonStyle::Primary.classes(),
        secondary = ButtonStyle::Secondary.classes(),
    );
    html::card("Control", &body)
}

fn limits_card() -> String {
    html::card(
        "Honest limits of this surface",
        &format!(
            "<div class=\"grid gap-3\">{scheduler}{backend}{explain}</div>",
            scheduler = html::alert(
                Tone::Neutral,
                "No continuous scheduler",
                "There is no background mover loop and no cancel control: `run` executes one bounded pass on the caller's thread, and the continuous scheduler that will own `cancel` is not part of the engine yet.",
            ),
            backend = html::alert(
                Tone::Neutral,
                "Tierless backend",
                "The default LSM backend reports tier state through `inspect`, but a backend without tiered storage answers `dry-run` and `run` with `UnsupportedBackend`. That typed refusal is shown, not hidden.",
            ),
            explain = html::alert(
                Tone::Neutral,
                "`dry_run` is the explain control",
                "The handle has no separate `explain` method: the dry pass is the explanation, and its receipt carries the decision and the ordered reasons the policy weighed.",
            ),
        ),
    )
}

/// Builds a policy from the submitted form.
pub fn policy_from_parts(
    policy_id: &str,
    version: &str,
    budget: &str,
    residence: &str,
    promotion: &str,
    pinned: &str,
) -> Result<TierPolicy, String> {
    let pinned = pinned
        .split(',')
        .map(str::trim)
        .filter(|text| !text.is_empty())
        .map(|text| {
            text.parse::<u64>()
                .map_err(|error| format!("pinned segment `{text}` is not a number: {error}"))
        })
        .collect::<Result<Vec<u64>, String>>()?;
    TierPolicy::try_new(
        policy_id.trim(),
        version.trim().parse().map_err(|error| format!("version: {error}"))?,
        budget.trim().parse().map_err(|error| format!("NVMe budget: {error}"))?,
        residence.trim().parse().map_err(|error| format!("minimum residence moves: {error}"))?,
        promotion.trim().parse().map_err(|error| format!("promotion threshold: {error}"))?,
        pinned,
    )
    .map_err(|error| format!("the policy is invalid: {error:?}"))
}

/// Renders one mover outcome as operator-readable markup.
pub fn render_receipt(receipt: &TierMoveRunReceipt) -> String {
    let decision = match receipt.decision() {
        TierDecision::Promote => (Tone::Good, "promote"),
        TierDecision::Demote => (Tone::Good, "demote"),
        TierDecision::Split => (Tone::Neutral, "split"),
        TierDecision::None => (Tone::Neutral, "nothing to move"),
        TierDecision::Refused => (Tone::Warn, "refused by policy"),
    };
    let mut reasons = String::new();
    for reason in receipt.reasons() {
        reasons.push_str(&format!(
            "<div class=\"text-xs text-muted-foreground\">{} = {}</div>",
            html::esc(reason.fact()),
            html::esc(reason.observed())
        ));
    }
    format!(
        "<div class=\"grid gap-3\"><div class=\"flex items-center gap-2 flex-wrap\">{decision}{dry}</div>{definition}{reasons}</div>",
        decision = html::badge(decision.0, decision.1),
        dry = if receipt.dry_run() {
            html::badge(Tone::Neutral, "dry run")
        } else {
            html::badge(Tone::Neutral, "real pass")
        },
        definition = html::definition(&[
            ("policy", format!("{} v{}", receipt.policy_id(), receipt.policy_version())),
            ("segment", receipt.segment_id().to_string()),
            ("bytes", html::human_bytes(receipt.bytes())),
            (
                "manifest epoch",
                format!("{} → {}", receipt.manifest_epoch_before(), receipt.manifest_epoch_after())
            ),
            ("manifest accepted", receipt.manifest_accepted().to_string()),
        ]),
        reasons = reasons,
    )
}

/// Renders a tier administration failure, including the receipt a
/// `MoveFailed` carries about the stage that failed.
pub fn render_failure(error: TierAdminError) -> String {
    match error {
        TierAdminError::MoveFailed(receipt) => format!(
            "the pass failed at segment {} ({} bytes, manifest epoch {} unchanged): decision {:?}",
            receipt.segment_id(),
            receipt.bytes(),
            receipt.manifest_epoch_before(),
            receipt.decision(),
        ),
        other => format!("refused: {other:?}"),
    }
}
