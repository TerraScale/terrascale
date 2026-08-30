//! The Replication and topology page.
//!
//! This page is deliberately explicit about what an in-process, single-engine
//! studio can and cannot show. The region catalog, the topology view, the
//! acknowledgement vocabulary, and the cluster crate's standalone durability
//! model are all real in-process value domains and are driven here. The live
//! replication surfaces — acknowledgement reports, convergence, conflict
//! records, member health, replication receipts, and every `ClusterHost`
//! operation — are not exposed in-process without members, and the page says
//! so instead of inventing numbers.

use terrabase_cluster::{
    DurabilityCoordinator, DurabilityLevel, DurabilityReceipt, Membership, MembershipError,
    RegionMembers,
};

use crate::html::{self, ButtonStyle, Tone};
use crate::metrics;
use crate::state::{RegionSpec, StudioState, TopologySpec};

/// The identifier the durability outcome is merged into.
pub const DURABILITY_ID: &str = "durability-outcome";
/// The identifier the topology table is merged into.
pub const TOPOLOGY_ID: &str = "topology-table";

/// Renders the Replication page.
pub fn render(state: &StudioState) -> String {
    let body = format!(
        "{heading}\n\
{scope_note}\n\
<div class=\"grid gap-6 lg:grid-cols-2\">{regions}{levels}</div>\n\
{topology}\n\
{nodes}\n\
{durability}\n\
{not_exposed}",
        heading = html::heading(
            "Replication & topology",
            "The region catalog, the topology view, the acknowledgement vocabulary, and the cluster crate's in-process durability model.",
        ),
        scope_note = html::alert(
            Tone::Warn,
            "This studio drives one engine location in-process",
            "No replication members are running, so there is no live convergence, lag, or acknowledgement to observe. Everything below is a real in-process surface; the panel at the bottom lists what is not exposed without a cluster.",
        ),
        regions = regions_card(state),
        levels = levels_card(),
        topology = topology_card(state),
        nodes = nodes_card(state),
        durability = durability_card(state),
        not_exposed = not_exposed_card(),
    );
    html::page("Cluster", "/cluster", "", &body)
}

/// Shows declared members and the aggregate usage measurement this
/// in-process lane can make safely. Per-member bytes would be fabricated
/// because no ClusterHost is running.
fn nodes_card(state: &StudioState) -> String {
    let spec = state.topology();
    let snapshot = metrics::gather(state);
    let mut rows = String::new();
    for region in &spec.regions {
        for member in &region.members {
            rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
            rows.push_str(&html::cell(member));
            rows.push_str(&html::cell(&region.name));
            rows.push_str(&html::cell("declared (no live host)"));
            rows.push_str(&html::cell("not exposed"));
            rows.push_str("</tr>");
        }
    }
    let body = format!(
        "{summary}\n{table}\n{note}",
        summary = html::definition(&[
            ("engine-location bytes", html::human_bytes(snapshot.location_bytes)),
            ("engine-location files", snapshot.location_files.to_string()),
        ]),
        table = html::table(
            &["node", "region", "membership", "node usage"],
            &[rows],
            "No nodes declared.",
        ),
        note = html::alert(
            Tone::Neutral,
            "Usage scope",
            "The byte and file counts above cover this Studio's one durable engine location. Member-level usage, health, lag, and convergence require a running cluster host and remain unavailable in the in-process lane.",
        ),
    );
    html::card("Cluster nodes & usage", &body)
}

fn regions_card(state: &StudioState) -> String {
    let catalog = state.regions.lock().unwrap_or_else(|poison| poison.into_inner()).clone();
    let render = |codes: &[terrabase::RegionCode]| {
        if codes.is_empty() {
            "<span class=\"text-sm text-muted-foreground\">none</span>".to_owned()
        } else {
            codes
                .iter()
                .map(|code| html::badge(Tone::Neutral, code.as_str()))
                .collect::<Vec<_>>()
                .join(" ")
        }
    };
    let codes = catalog.active().iter().map(|code| code.as_str()).collect::<Vec<_>>().join(",");
    let reserved =
        catalog.reserved().iter().map(|code| code.as_str()).collect::<Vec<_>>().join(",");
    let body = format!(
        "{definitions}\n\
<div class=\"mt-4 flex flex-wrap gap-2\">\n\
<form data-on:submit=\"@post('/replication/regions/admit', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"active\" value=\"{active}\">\
<input type=\"hidden\" name=\"reserved\" value=\"{reserved}\">\
<input type=\"hidden\" name=\"code\" value=\"AMS\">\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Admit AMS</button>\
</form>\n\
<form data-on:submit=\"if (!confirm('Retire FRA from this Studio-local region catalog?')) return; @post('/replication/regions/retire', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"active\" value=\"{active}\">\
<input type=\"hidden\" name=\"reserved\" value=\"{reserved}\">\
<input type=\"hidden\" name=\"code\" value=\"FRA\">\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\">Retire FRA</button>\
</form>\n\
</div>\n{receipt}\n\
<p class=\"mt-4 text-xs text-muted-foreground\">The catalog is a value domain the platform side owns; the studio edits its own in-memory copy to show the typed admission rules, the three-letter code grammar, and the {ceiling}-region ceiling. It is not a durable engine setting.</p>",
        definitions = html::definition(&[
            ("active regions", render(catalog.active())),
            ("reserved codes", render(catalog.reserved())),
            ("ceiling", terrabase::REGION_CEILING.to_string()),
            ("registered", format!("{}", catalog.active().len() + catalog.reserved().len()),),
        ]),
        primary = ButtonStyle::Primary.classes(),
        secondary = ButtonStyle::Secondary.classes(),
        active = html::attribute(&codes),
        reserved = html::attribute(&reserved),
        receipt = html::receipt_region_with("region-receipt"),
        ceiling = terrabase::REGION_CEILING,
    );
    html::card("Region catalog", &body)
}

fn levels_card() -> String {
    let body = format!(
        "{definitions}\n{notes}",
        definitions = html::definition(&[
            ("local", "one member's own durable commit".to_owned()),
            ("region quorum", "a majority of one region's voters".to_owned()),
            ("majority regions", "a majority of regions".to_owned()),
            ("all regions", "every region acknowledged".to_owned()),
        ]),
        notes = html::alert(
            Tone::Neutral,
            "Degeneracy is contract, not configuration",
            "With one region, region quorum, majority regions, and all regions are the same level; with two regions, majority regions and all regions are the same. The engine defines these equivalences rather than resolving them.",
        ),
    );
    html::card("Acknowledgement levels", &body)
}

fn topology_card(state: &StudioState) -> String {
    let spec = state.topology();
    let validated = state.topology_view();
    let mut rows = String::new();
    for (index, region) in spec.regions.iter().enumerate() {
        let remove = format!(
            "<form data-on:submit=\"if (!confirm('Remove this region from the Studio topology?')) return; @post('/replication/topology/remove-region', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"index\" value=\"{index}\">\
<button type=\"submit\" class=\"text-sm text-muted-foreground hover:text-foreground underline\">remove</button>\
</form>"
        );
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(&region.name));
        rows.push_str(&html::cell(&region.members.join(", ")));
        rows.push_str(&html::cell_raw(&remove));
        rows.push_str("</tr>");
    }
    let table = html::table(&["region", "members", ""], &[rows], "No region in the topology.");
    let verdict = match validated {
        Ok(view) => html::alert(
            Tone::Good,
            "The topology validates",
            &format!(
                "{} region(s), {} member(s), {} owned range(s); the view is servable.",
                view.regions().count(),
                view.members().count(),
                view.owned_ranges().count(),
            ),
        ),
        Err(failure) => html::alert(Tone::Bad, "The topology does not validate", &failure),
    };
    let body = format!(
        "<div id=\"{TOPOLOGY_ID}\">{table}</div>\n{verdict}\n\
<form class=\"mt-4 grid gap-4 sm:grid-cols-3 items-end\" data-on:submit=\"@post('/replication/topology/add-region', {{contentType: 'form', retry: 'never'}})\">\n\
{region}{first}{second}\n\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Add region</button>\n\
</form>",
        TOPOLOGY_ID = TOPOLOGY_ID,
        table = table,
        verdict = verdict,
        region = html::text_input("region", "Region id", "", "ams"),
        first = html::text_input("first", "First member", "", "ams-a"),
        second = html::text_input("second", "Second member", "", "ams-b"),
        primary = ButtonStyle::Primary.classes(),
    );
    html::card("Topology view", &body)
}

/// Builds a region specification from submitted names.
pub fn region_from_parts(name: &str, first: &str, second: &str) -> Result<RegionSpec, String> {
    if name.trim().is_empty() || first.trim().is_empty() || second.trim().is_empty() {
        return Err("a region needs an id and two member names".to_owned());
    }
    Ok(RegionSpec {
        name: name.trim().to_owned(),
        members: [first.trim().to_owned(), second.trim().to_owned()],
    })
}

/// Removes one region by index, refusing an out-of-range index.
pub fn without_region(spec: &TopologySpec, index: usize) -> Result<TopologySpec, String> {
    if index >= spec.regions.len() {
        return Err(format!("region {index} does not exist"));
    }
    let mut next = spec.clone();
    next.regions.remove(index);
    Ok(next)
}

fn durability_card(state: &StudioState) -> String {
    let spec = state.topology();
    let body = match build_membership(&spec) {
        Ok(membership) => {
            let mut rows = String::new();
            for region in membership.regions() {
                let members = membership
                    .members_of(&region)
                    .map(|members| {
                        members.iter().map(|member| member.as_str().to_owned()).collect::<Vec<_>>()
                    })
                    .unwrap_or_default()
                    .join(", ");
                rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
                rows.push_str(&html::cell(region.as_str()));
                rows.push_str(&html::cell(&members));
                rows.push_str(&html::cell("2 of 2 voters"));
                rows.push_str("</tr>");
            }
            let table =
                html::table(&["region", "voters", "shape"], &[rows], "The membership is empty.");
            format!(
                "{table}\n\
<p class=\"mt-4 text-sm text-muted-foreground\">The cluster crate's durability model is deterministic and in-process: it holds one membership epoch and answers whether a submitted operation reached the level you asked for. This is a model of the rule, not a live cluster.</p>\n\
<div class=\"mt-4 flex flex-wrap gap-2\">\n\
<form data-on:submit=\"@post('/replication/durability/submit', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"level\" value=\"regional\">\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Submit at regional durability</button>\
</form>\n\
<form data-on:submit=\"@post('/replication/durability/submit', {{contentType: 'form', retry: 'never'}})\">\
<input type=\"hidden\" name=\"level\" value=\"global\">\
<button type=\"submit\" class=\"inline-flex items-center justify-center rounded-md px-3 h-9 text-sm font-medium transition-colors {primary}\">Submit at global durability</button>\
</form>\n\
<button type=\"button\" class=\"inline-flex items-center justify-center rounded-md border px-3 h-9 text-sm font-medium transition-colors {secondary}\" data-on:click=\"@post('/replication/durability/toggle-member', {{payload: {{}}, retry: 'never'}})\">Toggle one voter inactive</button>\n\
</div>\n<div id=\"{DURABILITY_ID}\" class=\"mt-4\"></div>",
                table = table,
                primary = ButtonStyle::Primary.classes(),
                secondary = ButtonStyle::Secondary.classes(),
                DURABILITY_ID = DURABILITY_ID,
            )
        }
        Err(failure) => {
            html::alert(Tone::Bad, "No membership can be built from this topology", &failure)
        }
    };
    html::card("Durability model (terrabase-cluster)", &body)
}

/// Builds the cluster crate's membership from the studio's topology spec.
///
/// Every region must carry exactly two distinct members, which is the shape
/// the model enforces, so a spec that violates it reports the typed
/// membership error here.
pub fn build_membership(spec: &TopologySpec) -> Result<Membership, String> {
    let mut regions = Vec::new();
    for region in &spec.regions {
        let members =
            RegionMembers::try_new(&region.name, [&region.members[0], &region.members[1]])
                .map_err(membership_failure)?;
        regions.push(members);
    }
    Membership::new(1, 1, regions).map_err(membership_failure)
}

fn membership_failure(error: MembershipError) -> String {
    format!("membership refused: {error:?}")
}

/// Submits one operation at the requested level against a fresh coordinator.
///
/// The coordinator is rebuilt per request from the current topology, so the
/// panel always answers for the membership the page is showing. `inactive`
/// names a voter the model treats as down, which is how the failure shapes
/// become observable.
pub fn submit_durability(
    spec: &TopologySpec,
    level: &str,
    inactive: Option<&str>,
    operation: &str,
) -> Result<String, String> {
    let mut coordinator = DurabilityCoordinator::new(build_membership(spec)?);
    if let Some(inactive) = inactive {
        let member = terrabase::MemberId::try_new(inactive)
            .map_err(|error| format!("unusable member id: {error:?}"))?;
        coordinator
            .set_member_active(&member, false)
            .map_err(|error| format!("cannot mark the member inactive: {error:?}"))?;
    }
    let level = match level {
        "global" => DurabilityLevel::GlobalDurable,
        _ => DurabilityLevel::RegionalDurable,
    };
    let coordinator_member = coordinator
        .membership()
        .all_members()
        .first()
        .cloned()
        .ok_or_else(|| "the membership has no member to submit through".to_owned())?;
    match coordinator.submit(operation, &coordinator_member, level) {
        Ok(receipt) => Ok(render_receipt(&receipt)),
        Err(failure) => Err(format!("durability failure: {failure:?}")),
    }
}

fn render_receipt(receipt: &DurabilityReceipt) -> String {
    format!(
        "{} at {:?}: {} contributor(s), generation {} epoch {}",
        receipt.operation(),
        receipt.level(),
        receipt.contributors().len(),
        receipt.generation(),
        receipt.epoch(),
    )
}

fn not_exposed_card() -> String {
    html::card(
        "Not exposed in-process",
        &format!(
            "<p class=\"mb-3 text-sm text-muted-foreground\">These surfaces exist in the engine's contract but are not reachable from a single in-process engine location. The studio renders this list instead of reaching into private modules or inventing values.</p>\n{}",
            html::table(&["surface", "why it is absent"], &[omissions()], "Nothing omitted.",)
        ),
    )
}

fn omissions() -> String {
    let entries: &[(&str, &str)] = &[
        (
            "AcknowledgementReport / Convergence / ConflictRecord",
            "the constructors are crate-private; only a running replication member produces them",
        ),
        (
            "ReplicationStatus / member health",
            "member_health and the observation producers are crate-private and need live members",
        ),
        (
            "ReplicationReceipt",
            "the replication lane's receipt builders are crate-private; it is a qualification artifact, not a serving surface",
        ),
        (
            "ClusterHost / ClusterMember / BackupSet",
            "they need real member locations on disk; the studio drives one engine location only",
        ),
        (
            "BackupLastRouter",
            "needs a live cluster host and a backup set, and blocks while waiting for a frontier",
        ),
        (
            "coordinate_write / ReplicaEndpoint",
            "the leaderless coordinator needs one opened engine per replica",
        ),
    ];
    let mut rows = String::new();
    for (surface, reason) in entries {
        rows.push_str("<tr class=\"border-b transition-colors hover:bg-muted/50\">");
        rows.push_str(&html::cell(surface));
        rows.push_str(&html::cell(reason));
        rows.push_str("</tr>");
    }
    rows
}
