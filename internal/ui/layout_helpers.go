package ui

import "fmt"

func title(value string) string {
	if value == "" {
		return "TerraScale"
	}
	return value + " · TerraScale"
}

func shellClass() string {
	return "ts-shell"
}

func navBadgeClass(active bool) string {
	if active {
		return "ts-nav-count ts-nav-count-active"
	}
	return "ts-nav-count ts-nav-count-neutral"
}

func sidebarClass(admin bool) string {
	if admin {
		return "ts-sidebar ts-admin-sidebar"
	}
	return "ts-sidebar"
}

func sidebarHeaderClass(admin bool) string {
	if admin {
		return "ts-sidebar-header ts-admin-sidebar-header"
	}
	return "ts-sidebar-header"
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
