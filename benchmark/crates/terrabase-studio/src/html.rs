//! Small hand-written HTML render helpers.
//!
//! The studio brief rules out template engines on purpose: `format!` plus a
//! handful of typed component helpers keeps the dependency list to HTTP and
//! JSON crates and keeps every page readable as ordinary Rust. Everything a
//! page renders goes through [`esc`] for text and [`attribute`] for attribute
//! values, so no document field or identifier can break out of the markup.

use std::fmt::Write as _;

/// Escapes text for safe interpolation into HTML body content.
pub fn esc(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for character in text.chars() {
        match character {
            '&' => out.push_str("&amp;"),
            '<' => out.push_str("&lt;"),
            '>' => out.push_str("&gt;"),
            '"' => out.push_str("&quot;"),
            '\'' => out.push_str("&#39;"),
            _ => out.push(character),
        }
    }
    out
}

/// Escapes text for safe interpolation into a double-quoted attribute value.
///
/// Newlines and carriage returns are dropped: attribute values never carry
/// them in this studio, and removing them keeps a pasted payload from being
/// able to end the attribute early.
pub fn attribute(text: &str) -> String {
    esc(&text.replace(['\n', '\r'], " "))
}

/// One entry of the top navigation.
pub struct NavEntry {
    /// Route the entry points at.
    pub path: &'static str,
    /// Label shown to the operator.
    pub label: &'static str,
}

/// The navigation every page shares, in display order.
pub const NAV: &[NavEntry] = &[
    NavEntry { path: "/", label: "Overview" },
    NavEntry { path: "/metrics", label: "Metrics" },
    NavEntry { path: "/cluster", label: "Cluster" },
    NavEntry { path: "/documents", label: "Documents" },
    NavEntry { path: "/query", label: "Query" },
    NavEntry { path: "/indexes", label: "Indexes" },
    NavEntry { path: "/tiers", label: "Tiers" },
    NavEntry { path: "/usage", label: "Usage" },
    NavEntry { path: "/backups", label: "Backups &amp; deletion" },
];

/// Renders the full HTML document shell around one page body.
///
/// `active` names the nav entry to highlight; `extra_head` lets a page add its
/// own signals declaration before the runtime scans the tree.
pub fn page(title: &str, active: &str, extra_head: &str, body: &str) -> String {
    let mut nav = String::new();
    for entry in NAV {
        let current = if entry.path == active { " font-medium bg-muted" } else { "" };
        let _ = write!(
            nav,
            "<a href=\"{path}\" class=\"px-3 py-1.5 rounded-md text-sm text-muted-foreground hover:text-foreground hover:bg-accent transition-colors{current}\">{label}</a>",
            path = entry.path,
            label = entry.label,
            current = current,
        );
    }
    format!(
        "<!DOCTYPE html>\n\
<html lang=\"en\">\n\
<head>\n\
<meta charset=\"utf-8\">\n\
<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n\
<title>{title} · TerraBase Studio</title>\n\
<link rel=\"stylesheet\" href=\"/static/basecoat.css\">\n\
{extra_head}\
<script src=\"/static/studio.js\" defer></script>\n\
</head>\n\
<body class=\"min-h-screen bg-background text-foreground antialiased\">\n\
<div data-signals=\"{{ }}\" class=\"flex min-h-screen\">\n\
<aside class=\"hidden md:flex w-60 shrink-0 flex-col border-r bg-card\">\n\
<div class=\"px-4 py-5 border-b\">\n\
<div class=\"text-sm font-semibold tracking-tight\">TerraBase Studio</div>\n\
<div class=\"text-xs text-muted-foreground\">operator lane</div>\n\
</div>\n\
<nav class=\"flex flex-col gap-1 p-3\">{nav}</nav>\n\
<div class=\"mt-auto p-4 text-xs text-muted-foreground border-t\">\n\
<div>loopback · no authentication</div>\n\
<div><span data-text=\"engine_sequence\"></span></div>\n\
</div>\n\
</aside>\n\
<main class=\"flex-1 min-w-0\">\n\
<header class=\"flex md:hidden items-center gap-2 border-b px-4 py-3 overflow-x-auto\">{nav}</header>\n\
<div class=\"mx-auto max-w-6xl px-4 py-8 md:px-8\">{body}</div>\n\
</main>\n\
</div>\n\
</body>\n</html>\n",
        title = esc(title),
        extra_head = extra_head,
        nav = nav,
        body = body,
    )
}

/// Renders a Basecoat card with a title and an optional right-hand aside.
pub fn card(title: &str, body: &str) -> String {
    format!(
        "<section class=\"rounded-xl border bg-card text-card-foreground shadow-sm\">\n\
<div class=\"flex items-center justify-between border-b px-5 py-3.5\"><h2 class=\"text-sm font-semibold\">{title}</h2></div>\n\
<div class=\"p-5\">{body}</div>\n</section>\n"
    )
}

/// Renders a page heading with a one-line description underneath.
pub fn heading(title: &str, description: &str) -> String {
    format!(
        "<div class=\"mb-6\"><h1 class=\"text-2xl font-semibold tracking-tight\">{title}</h1>\n\
<p class=\"mt-1 text-sm text-muted-foreground\">{description}</p></div>\n",
        title = esc(title),
        description = esc(description),
    )
}

/// The semantic colour of a badge or alert.
#[derive(Clone, Copy)]
pub enum Tone {
    /// Neutral information.
    Neutral,
    /// Successful outcome.
    Good,
    /// Warning or degraded state.
    Warn,
    /// Failed outcome.
    Bad,
}

impl Tone {
    fn badge_classes(self) -> &'static str {
        match self {
            Self::Neutral => "border-transparent bg-secondary text-secondary-foreground",
            Self::Good => "border-transparent bg-emerald-100 text-emerald-800",
            Self::Warn => "border-transparent bg-amber-100 text-amber-800",
            Self::Bad => "border-transparent bg-red-100 text-red-800",
        }
    }

    fn alert_classes(self) -> &'static str {
        match self {
            Self::Neutral => "border-border bg-muted text-foreground",
            Self::Good => "border-emerald-300 bg-emerald-50 text-emerald-900",
            Self::Warn => "border-amber-300 bg-amber-50 text-amber-900",
            Self::Bad => "border-red-300 bg-red-50 text-red-900",
        }
    }
}

/// Renders an inline status badge.
pub fn badge(tone: Tone, text: &str) -> String {
    format!(
        "<span class=\"inline-flex items-center rounded-md border px-2 py-0.5 text-xs font-medium {classes}\">{text}</span>",
        classes = tone.badge_classes(),
        text = esc(text),
    )
}

/// Renders a block-level notice. `title` may be empty for a plain message.
pub fn alert(tone: Tone, title: &str, text: &str) -> String {
    let head = if title.is_empty() {
        String::new()
    } else {
        format!("<div class=\"mb-1 font-medium text-sm\">{}</div>", esc(title))
    };
    format!(
        "<div class=\"rounded-lg border px-4 py-3 text-sm {classes}\">{head}<div class=\"text-sm\">{text}</div></div>",
        classes = tone.alert_classes(),
        head = head,
        text = esc(text),
    )
}

/// Renders a two-column definition table from `(label, value)` pairs.
pub fn definition(pairs: &[(&str, String)]) -> String {
    let mut rows = String::new();
    for (label, value) in pairs {
        let _ = write!(
            rows,
            "<div class=\"flex items-baseline justify-between gap-4 py-1.5 border-b last:border-0\">\n\
<div class=\"text-sm text-muted-foreground\">{label}</div>\n\
<div class=\"text-sm font-medium tabular-nums text-right\">{value}</div>\n</div>\n",
            label = esc(label),
            value = esc(value),
        );
    }
    format!("<div>{rows}</div>", rows = rows)
}

/// Renders a data table.
///
/// `headers` are escaped labels. Each entry of `rows` is one complete `<tr>`
/// element the caller built with [`cell`] and [`cell_raw`]; this helper only
/// adds the wrapper, the head, and the empty-state row, so a caller never ends
/// up with a nested `<tr>`.
pub fn table(headers: &[&str], rows: &[String], empty_note: &str) -> String {
    let mut head = String::new();
    for header in headers {
        let _ = write!(
            head,
            "<th class=\"h-10 px-3 text-left align-middle font-medium text-muted-foreground\">{header}</th>",
            header = esc(header),
        );
    }
    let mut body = String::new();
    for row in rows {
        body.push_str(row);
        body.push('\n');
    }
    if rows.is_empty() {
        let span = headers.len().max(1);
        let _ = write!(
            body,
            "<tr><td colspan=\"{span}\" class=\"px-3 py-6 text-center text-sm text-muted-foreground\">{note}</td></tr>",
            span = span,
            note = esc(empty_note),
        );
    }
    format!(
        "<div class=\"overflow-x-auto\"><table class=\"w-full text-sm\">\n\
<thead><tr class=\"border-b\">{head}</tr></thead>\n<tbody>{body}</tbody>\n</table></div>",
        head = head,
        body = body,
    )
}

/// Renders one escaped table cell.
pub fn cell(text: &str) -> String {
    format!("<td class=\"px-3 py-2 align-top\">{}</td>", esc(text))
}

/// Renders one pre-rendered (already escaped or already markup) table cell.
pub fn cell_raw(markup: &str) -> String {
    format!("<td class=\"px-3 py-2 align-top\">{markup}</td>")
}

/// Renders a labeled text input.
pub fn text_input(name: &str, label: &str, value: &str, placeholder: &str) -> String {
    format!(
        "<label class=\"grid gap-2 text-sm\">\n<span class=\"font-medium\">{label}</span>\n\
<input class=\"flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm placeholder:text-muted-foreground\" name=\"{name}\" value=\"{value}\" placeholder=\"{placeholder}\">\n\
</label>\n",
        label = esc(label),
        name = attribute(name),
        value = attribute(value),
        placeholder = attribute(placeholder),
    )
}

/// Renders a labeled number input.
pub fn number_input(name: &str, label: &str, value: &str) -> String {
    format!(
        "<label class=\"grid gap-2 text-sm\">\n<span class=\"font-medium\">{label}</span>\n\
<input class=\"flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm\" type=\"number\" name=\"{name}\" value=\"{value}\">\n\
</label>\n",
        label = esc(label),
        name = attribute(name),
        value = attribute(value),
    )
}

/// Renders a labeled plain-text textarea.
pub fn textarea(name: &str, label: &str, value: &str, rows: usize) -> String {
    format!(
        "<label class=\"grid gap-2 text-sm\">\n<span class=\"font-medium\">{label}</span>\n\
<textarea class=\"flex min-h-24 w-full rounded-md border border-input bg-transparent px-3 py-2 font-mono text-sm shadow-sm placeholder:text-muted-foreground\" name=\"{name}\" rows=\"{rows}\" spellcheck=\"false\">{value}</textarea>\n\
</label>\n",
        label = esc(label),
        name = attribute(name),
        rows = rows.max(1),
        value = esc(value),
    )
}

/// Renders a labeled select box from pre-rendered `<option>` elements.
pub fn select(name: &str, label: &str, options: &str) -> String {
    format!(
        "<label class=\"grid gap-2 text-sm\">\n<span class=\"font-medium\">{label}</span>\n\
<select class=\"flex h-9 w-full rounded-md border border-input bg-transparent px-3 py-1 text-sm shadow-sm\" name=\"{name}\">{options}</select>\n\
</label>\n",
        label = esc(label),
        name = attribute(name),
        options = options,
    )
}

/// Renders one `<option>`, selected when `chosen`.
pub fn option(value: &str, label: &str, chosen: bool) -> String {
    let selected = if chosen { " selected" } else { "" };
    format!(
        "<option value=\"{value}\"{selected}>{label}</option>",
        value = attribute(value),
        selected = selected,
        label = esc(label),
    )
}

/// Renders a progressively enhanced Monaco JSON editor.
///
/// The textarea remains the submitted form field and the no-network fallback.
/// After Monaco mounts, the runtime enables the hidden submitted field,
/// removes the textarea's name, and mirrors every model change into the
/// hidden value.
pub fn json_editor(name: &str, label: &str, value: &str) -> String {
    format!(
        "<label class=\"grid gap-1.5 text-sm\">\n\
<span class=\"font-medium\">{label}</span>\n\
<div data-monaco-json class=\"hidden h-80 overflow-hidden rounded-lg border border-input bg-[#0b1020] shadow-sm\"></div>\n\
<input data-monaco-value type=\"hidden\" name=\"{name}\" value=\"{value}\" disabled>\n\
<textarea data-monaco-fallback class=\"flex min-h-80 w-full rounded-lg border border-input bg-[#0b1020] px-4 py-3 font-mono text-sm text-[#d8dee9] shadow-sm\" name=\"{name}\" rows=\"16\" spellcheck=\"false\">{value}</textarea>\n\
<span data-monaco-status class=\"text-xs text-muted-foreground\">JSON syntax is checked in the browser.</span>\n\
</label>\n",
        name = attribute(name),
        label = esc(label),
        value = esc(value),
    )
}

/// The visual style of a button.
#[derive(Clone, Copy)]
pub enum ButtonStyle {
    /// The primary action.
    Primary,
    /// A secondary action.
    Secondary,
    /// A destructive action.
    Destructive,
}

impl ButtonStyle {
    /// The Basecoat utility classes this style renders.
    pub fn classes(self) -> &'static str {
        match self {
            Self::Primary => "bg-primary text-primary-foreground hover:bg-primary/90",
            Self::Secondary => {
                "border border-input bg-background hover:bg-accent hover:text-accent-foreground"
            }
            Self::Destructive => "bg-destructive text-white hover:bg-destructive/90",
        }
    }
}

/// The container every action receipt is merged into.
///
/// Actions return `text/event-stream` and merge their receipt here, so no
/// action can fail silently: the typed outcome always lands on the page.
pub const RECEIPT_TARGET: &str = "receipt";

/// Renders the initially empty receipt region.
pub fn receipt_region() -> String {
    format!("<div id=\"{RECEIPT_TARGET}\"></div>", RECEIPT_TARGET = RECEIPT_TARGET)
}

/// Formats a byte count with a binary unit, for readable metrics.
pub fn human_bytes(bytes: u64) -> String {
    const UNITS: [&str; 6] = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
    if bytes == 0 {
        return "0 B".to_owned();
    }
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }
    if unit == 0 {
        format!("{bytes} B")
    } else {
        format!("{value:.1} {unit}", value = value, unit = UNITS[unit])
    }
}

/// Renders a receipt region with an explicit identifier, so several action
/// cards on one page each get their own target.
pub fn receipt_region_with(id: &str) -> String {
    format!("<div id=\"{id}\"></div>", id = attribute(id))
}

/// Renders `text` as a single-quoted JavaScript string literal.
///
/// Used for the inline object literals the Datastar actions carry, so a
/// document or tenant identifier can never terminate the expression.
pub fn js_string(text: &str) -> String {
    let mut out = String::with_capacity(text.len() + 2);
    out.push('\'');
    for character in text.chars() {
        match character {
            '\\' => out.push_str("\\\\"),
            '\'' => out.push_str("\\'"),
            '\n' => out.push_str("\\n"),
            '\r' => out.push_str("\\r"),
            '\u{2028}' => out.push_str("\\u2028"),
            '\u{2029}' => out.push_str("\\u2029"),
            character if character.is_control() => {
                let _ = write!(out, "\\u{:04x}", character as u32);
            }
            _ => out.push(character),
        }
    }
    out.push('\'');
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn table_rows_are_never_nested() {
        let row =
            format!("<tr class=\"x\">{}{}</tr>", cell("a"), cell_raw(&badge(Tone::Good, "ok")));
        let markup = table(&["one", "two"], &[row.clone()], "nothing");
        assert_eq!(markup.matches("<tr").count(), 1, "exactly one <tr> per row");
        assert!(markup.contains(row.as_str()));
        assert!(!markup.contains("<tr class=\"border-b transition-colors hover:bg-muted/50\"><tr"));
    }

    #[test]
    fn an_empty_table_renders_one_spanning_note_row() {
        let markup = table(&["a", "b", "c"], &[], "nothing here");
        assert!(markup.contains("colspan=\"3\""));
        assert!(markup.contains("nothing here"));
        assert_eq!(markup.matches("<tr").count(), 1);
    }

    #[test]
    fn js_string_literals_are_quoted_safely() {
        assert_eq!(js_string("demo"), "'demo'");
        assert_eq!(js_string("a'b\\c"), "'a\'b\\\\c'");
        assert_eq!(js_string("line\nbreak"), "'line\\nbreak'");
    }

    #[test]
    fn definition_values_are_html_escaped() {
        let rendered = definition(&[("value", "<script>alert('x')</script>".to_owned())]);
        assert!(!rendered.contains("<script>"));
        assert!(rendered.contains("&lt;script&gt;"));
    }

    #[test]
    fn javascript_strings_escape_line_separators_and_controls() {
        let rendered = js_string("a\u{2028}b\u{0000}c");
        assert_eq!(rendered, "'a\\u2028b\\u0000c'");
    }
}
