// Package aria provides scalar values for ARIA attributes in templ views.
package aria

// CurrentPage marks the active link in a group of page navigation links.
func CurrentPage(active bool) string {
	if active {
		return "page"
	}
	return "false"
}
