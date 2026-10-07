package ui

import (
	"encoding/json"
	"fmt"
	"html"
	"math"
	"strconv"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/icons"
)

// navDestination gives every dashboard section its own icon and icon color so
// users can find a section by color as well as by label. Tones are defined in
// web/assets/css/tokens.css; page content itself uses the blue accent.
//
// A tone never repeats within one sidebar (the shell and admin sidebars never
// render together, so they may share tones), and look-alike tones such as
// blue/sky or teal/emerald stay away from each other in the list.
type navDestination struct {
	icon func(int) templ.Component
	tone string
}

var navDestinations = map[string]navDestination{
	"/":                    {icons.LayoutDashboard, "violet"},
	"/notifications":       {icons.Bell, "amber"},
	"/databases":           {icons.Database, "blue"},
	"/api-keys":            {icons.KeyRound, "lime"},
	"/auth":                {icons.Fingerprint, "emerald"},
	"/usage":               {icons.ChartColumnBig, "sky"},
	"/subscriptions":       {icons.RadioTower, "fuchsia"},
	"/billing":             {icons.CreditCard, "rose"},
	"/organization":        {icons.Building2, "teal"},
	"/admin/users":         {icons.Users, "pink"},
	"/admin/organizations": {icons.Building2, "indigo"},
	"/admin/projects":      {icons.Boxes, "lime"},
	"/admin/databases":     {icons.Database, "blue"},
	"/admin/estate":        {icons.Globe, "orange"},
	"/admin/usage":         {icons.ChartColumnBig, "sky"},
	"/admin/plans":         {icons.CreditCard, "rose"},
}

func navIcon(url string) templ.Component {
	if destination, ok := navDestinations[url]; ok {
		return destination.icon(16)
	}
	return icons.Circle(16)
}

// countText shows a navigation count, capped so the pill keeps its width.
func countText(count int) string {
	if count > 99 {
		return "99+"
	}
	return strconv.Itoa(count)
}

// CountLabel names a navigation count for assistive technology as its pill
// shows it, e.g. "1 organization" or "99+ organizations".
func CountLabel(count int, one, many string) string {
	if count == 1 {
		return "1 " + one
	}
	return countText(count) + " " + many
}

// UnreadLabel names an unread notifications count, e.g. "3 unread".
func UnreadLabel(count int) string {
	return CountLabel(count, "unread", "unread")
}

// notificationsItem names the Notifications entry points outside the section
// navigation: the account-menu item and the top-bar bell that open the
// notifications dialog.
func notificationsItem(unread int) core.NavItem {
	return core.NavItem{Label: "Notifications", URL: "/notifications", BadgeCount: unread, BadgeLabel: UnreadLabel(unread)}
}

// navLabel is a navigation link's accessible name: its label, then its count
// as BadgeLabel names it, e.g. "Notifications, 3 unread".
func navLabel(item core.NavItem) string {
	if item.BadgeCount == 0 || item.BadgeLabel == "" {
		return item.Label
	}
	return item.Label + ", " + item.BadgeLabel
}

func title(value string) string {
	if value == "" {
		return "TerraScale"
	}
	return value + " · TerraScale"
}

func sidebarLabel(admin bool) string {
	if admin {
		return "Admin navigation"
	}
	return "Primary navigation"
}

func navSectionsLabel(admin bool) string {
	if admin {
		return "Admin sections"
	}
	return "Dashboard sections"
}

func contextTriggerLabel(shell core.Shell) string {
	return fmt.Sprintf("Switch organization or project, current: %s / %s",
		displayName(shell.OrganizationName, "none"), displayName(shell.ProjectName, "none"))
}

// initialTones color organization and project tiles; a name always maps to
// the same tone so a workspace keeps its color across pages.
var initialTones = []string{"violet", "blue", "cyan", "teal", "amber", "pink", "emerald", "orange", "fuchsia", "indigo"}

func initialTone(name string) string {
	if name == "" {
		return "slate"
	}
	var hash uint32 = 2166136261
	for _, b := range []byte(name) {
		hash = (hash ^ uint32(b)) * 16777619
	}
	return initialTones[hash%uint32(len(initialTones))]
}

func initial(name string) string {
	for _, r := range name {
		return strings.ToUpper(string(r))
	}
	return "?"
}

func displayName(value, fallback string) string {
	if value == "" {
		return fallback
	}
	return value
}

// passwordSignal names a local Datastar signal owned by one password field.
func passwordSignal(id, name string) string {
	return "_" + name + "_" + strings.Map(func(r rune) rune {
		if r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' {
			return r
		}
		return '_'
	}, id)
}

func passwordSignals(id string) string {
	return "{" + passwordSignal(id, "reveal") + ": false, " + passwordSignal(id, "filled") + ": false}"
}

// regionJSString quotes a DOM ID as a JavaScript string literal without
// changing the ID that document.getElementById must find.
func regionJSString(value string) string {
	quoted, _ := json.Marshal(value) // Encoding a string cannot fail.
	return string(quoted)
}

// regionSync copies the codes selected in a select or checkbox group onto the map's
// data-selected attribute, which the stylesheet uses to light up hubs.
func regionSync(selectID string) string {
	return "(() => { const control = document.getElementById(" + regionJSString(selectID) + "); const selected = control.tagName === 'SELECT' ? [...control.selectedOptions].map(o => o.value) : [...control.querySelectorAll('input[type=checkbox]:checked')].map(i => i.value); el.dataset.selected = selected.join(' '); el.querySelectorAll('button[data-region]').forEach(node => node.setAttribute('aria-pressed', String(selected.includes(node.dataset.region)))) })()"
}

// regionPick changes the matching native control and dispatches its change
// event so the map and any other listeners observe the same value.
func regionPick(selectID string) string {
	return "(() => { const control = document.getElementById(" + regionJSString(selectID) + "); if (control.tagName === 'SELECT') { const option = [...control.options].find(o => o.value === el.dataset.region); if (!option || option.disabled) return; if (control.multiple) option.selected = !option.selected; else control.value = option.value; control.dispatchEvent(new Event('change', { bubbles: true })); } else { const input = [...control.querySelectorAll('input[type=checkbox]')].find(i => i.value === el.dataset.region); if (!input || input.disabled) return; input.checked = !input.checked; input.dispatchEvent(new Event('change', { bubbles: true })); } })()"
}

// regionArc is a quadratic curve lifted above the straight line between hubs.
func regionArc(from, to regionPin) string {
	midX := (from.x + to.x) / 2
	lift := max(3, math.Hypot(to.x-from.x, to.y-from.y)*0.18)
	controlY := min(from.y, to.y) - lift - math.Abs(to.x-from.x)*0.04
	return fmt.Sprintf("M %.2f %.2f Q %.2f %.2f %.2f %.2f", from.x, from.y, midX, controlY, to.x, to.y)
}

// navClickPending flags a primary-button click on a same-origin link that
// leaves the current document, after other handlers had a chance to cancel it.
const navClickPending = "$_navPending = $_navPending || !!(!evt.defaultPrevented && evt.button === 0 && !evt.metaKey && !evt.ctrlKey && !evt.shiftKey && !evt.altKey && evt.target.closest && evt.target.closest('a[href]:not([target]):not([download])') && evt.target.closest('a[href]').origin === location.origin && (evt.target.closest('a[href]').pathname !== location.pathname || evt.target.closest('a[href]').search !== location.search))"

// Sidebar scroll is handed off only by a same-tab navigation inside the menu.
// The destination consumes it once; forms and links elsewhere keep native behavior.
const navScrollKey = "ts-dashboard:nav-scroll"

const navScrollSave = `(() => {
	const link = evt.target.closest?.('a[href]');
	if (evt.defaultPrevented || evt.button !== 0 || evt.metaKey || evt.ctrlKey || evt.shiftKey || evt.altKey || !link || !el.contains(link) || link.hasAttribute('download') || (link.target && link.target !== '_self') || link.origin !== location.origin || (link.pathname === location.pathname && link.search === location.search)) return;
	try { sessionStorage.setItem('` + navScrollKey + `', JSON.stringify({href: link.href, top: el.scrollTop})); } catch {}
})()`

const navScrollRestore = `(() => {
	try {
		const saved = sessionStorage.getItem('` + navScrollKey + `');
		sessionStorage.removeItem('` + navScrollKey + `');
		const position = JSON.parse(saved);
		if (position?.href === location.href && Number.isFinite(position.top) && position.top >= 0) el.scrollTop = position.top;
	} catch {}
})()`

// navSubmitPending flags native form submissions; Datastar-handled forms
// prevent the default and never show the bar. A form marked data-submit-once
// is not sent again while a navigation is loading, so a double click cannot
// send it twice.
const navSubmitPending = "evt.target.hasAttribute('data-submit-once') && $_navPending ? evt.preventDefault() : ($_navPending = $_navPending || (!evt.defaultPrevented && evt.target.method !== 'dialog' && !evt.target.target))"

// speculationRules prefetches page links on hover. Routes with side effects
// (API calls, exports, payment sessions, one-shot links) are never prefetched.
const speculationRules = `{"prefetch":[{"where":{"and":[{"href_matches":"/*"},` +
	`{"not":{"href_matches":["/api/*","/__dev/*","/audit/export*","/billing/*","/checkout/*","/invitations/*","/cli/*","/agent/*","/oauth/*","/login*"]}},` +
	`{"not":{"selector_matches":"[download], [data-no-prefetch]"}}]},"eagerness":"moderate"}]}`

// speculationScript renders the rules as JSON inside a nonced script tag;
// templ would otherwise treat the script body as literal text.
func speculationScript(nonce string) templ.Component {
	attr := ""
	if nonce != "" {
		attr = ` nonce="` + html.EscapeString(nonce) + `"`
	}
	return templ.Raw(`<script type="speculationrules"` + attr + `>` + speculationRules + `</script>`)
}

// FormatBytes formats a size in bytes with binary units, B to PiB, rounding
// up a unit rather than showing 1024.0 of the one below.
func FormatBytes(n int64) string {
	const unit = 1024
	if n < unit {
		return strconv.FormatInt(n, 10) + " B"
	}
	value, exponent := float64(n)/unit, 0
	for value >= unit-0.05 && exponent < 4 {
		value /= unit
		exponent++
	}
	return strconv.FormatFloat(value, 'f', 1, 64) + " " + [...]string{"KiB", "MiB", "GiB", "TiB", "PiB"}[exponent]
}
