package ui

import (
	"fmt"

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
	"/admin":               {icons.Home, "violet"},
	"/admin/users":         {icons.Fingerprint, "pink"},
	"/admin/organizations": {icons.Building2, "indigo"},
	"/admin/projects":      {icons.Boxes, "cyan"},
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
