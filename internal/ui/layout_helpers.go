package ui

import (
	"fmt"
	"math"
	"strings"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/icons"
)

// navDestination gives every dashboard section its own icon and color tone so
// users can find a section by color as well as by label. Tones are defined in
// web/assets/css/tokens.css.
type navDestination struct {
	icon func(int) templ.Component
	tone string
}

var navDestinations = map[string]navDestination{
	"/":                    {icons.Home, "violet"},
	"/databases":           {icons.Database, "blue"},
	"/data":                {icons.Table, "cyan"},
	"/backups":             {icons.Archive, "teal"},
	"/engine":              {icons.Terminal, "lime"},
	"/api-keys":            {icons.KeyRound, "amber"},
	"/access":              {icons.Users, "pink"},
	"/auth":                {icons.ShieldCheck, "emerald"},
	"/audit":               {icons.ScrollText, "orange"},
	"/usage":               {icons.Gauge, "sky"},
	"/subscriptions":       {icons.Sparkles, "fuchsia"},
	"/billing":             {icons.CreditCard, "rose"},
	"/organization":        {icons.Building, "indigo"},
	"/settings":            {icons.Settings, "slate"},
	"/admin/users":         {icons.Fingerprint, "pink"},
	"/admin/organizations": {icons.Building2, "indigo"},
	"/admin/projects":      {icons.Boxes, "cyan"},
	"/admin/databases":     {icons.Database, "blue"},
	"/admin/estate":        {icons.Globe, "teal"},
	"/admin/usage":         {icons.Gauge, "sky"},
	"/admin/plans":         {icons.CreditCard, "rose"},
}

func navIcon(url string) templ.Component {
	if destination, ok := navDestinations[url]; ok {
		return destination.icon(16)
	}
	return icons.Circle(16)
}

// activeTone colors the page with the tone of its navigation section.
func activeTone(groups []core.NavGroup) string {
	for _, group := range groups {
		for _, item := range group.Items {
			if item.Active {
				return navDestinations[item.URL].tone
			}
		}
	}
	return "violet"
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

func projectTriggerLabel(kind, value string) string {
	if value == "" {
		return fmt.Sprintf("Choose %s, current: none", kind)
	}
	return fmt.Sprintf("Choose %s, current: %s", kind, value)
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

// regionSync copies the codes selected in selectID onto the map's
// data-selected attribute, which the stylesheet uses to light up hubs.
func regionSync(selectID string) string {
	return "el.dataset.selected = [...document.getElementById('" + selectID + "').selectedOptions].map(o => o.value).join(' ')"
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

// navSubmitPending flags native form submissions; Datastar-handled forms
// prevent the default and never show the bar.
const navSubmitPending = "$_navPending = $_navPending || (!evt.defaultPrevented && evt.target.method !== 'dialog' && !evt.target.target)"
