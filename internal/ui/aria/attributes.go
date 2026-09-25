// Package aria provides scalar values for ARIA attributes in templ views.
package aria

// CurrentPage marks the active link in a group of page navigation links.
func CurrentPage(active bool) string {
	if active {
		return "page"
	}
	return "false"
}

// Selected marks the active item in a selectable navigation group.
func Selected(active bool) string {
	if active {
		return "true"
	}
	return "false"
}
