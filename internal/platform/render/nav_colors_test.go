package render

import (
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/a-h/templ"
	"golang.org/x/net/html"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

func TestSidebarNavigationTonesDoNotRepeat(t *testing.T) {
	for _, tc := range []struct {
		name   string
		layout string
		count  int
	}{
		{name: "dashboard", layout: "shell", count: 10},
		{name: "admin", layout: "admin", count: 8},
	} {
		t.Run(tc.name, func(t *testing.T) {
			shell := shellFor(core.Account{}, core.Principal{}, tc.layout, httptest.NewRequest("GET", "/", nil))
			page := core.Page{Shell: shell}
			var document templ.Component
			if tc.layout == "admin" {
				document = ui.Admin(page, templ.NopComponent)
			} else {
				document = ui.Shell(page, templ.NopComponent)
			}
			var out strings.Builder
			if err := document.Render(t.Context(), &out); err != nil {
				t.Fatal(err)
			}
			root, err := html.Parse(strings.NewReader(out.String()))
			if err != nil {
				t.Fatal(err)
			}
			var sidebar *html.Node
			var findSidebar func(*html.Node)
			findSidebar = func(n *html.Node) {
				if n.Type == html.ElementNode && attrValue(n, "id") == "ts-navigation" {
					sidebar = n
					return
				}
				for child := n.FirstChild; child != nil; child = child.NextSibling {
					findSidebar(child)
				}
			}
			findSidebar(root)
			if sidebar == nil {
				t.Fatal("missing sidebar")
			}
			seen := make(map[string]bool)
			var walk func(*html.Node, bool)
			walk = func(n *html.Node, inNav bool) {
				if n.Type == html.ElementNode && n.Data == "nav" && strings.Contains(attrValue(n, "class"), "ts-sidebar-content") {
					inNav = true
				}
				if n.Type == html.ElementNode && (inNav || tc.layout == "admin" && n.Data == "a" && strings.Contains(attrValue(n, "class"), "ts-nav-link")) {
					if tone := attrValue(n, "data-tone"); tone != "" {
						if seen[tone] {
							t.Errorf("tone %q repeats in %s sidebar", tone, tc.name)
						}
						seen[tone] = true
					}
				}
				for child := n.FirstChild; child != nil; child = child.NextSibling {
					walk(child, inNav)
				}
			}
			walk(sidebar, false)
			if len(seen) != tc.count {
				t.Errorf("found %d unique nav tones, want %d", len(seen), tc.count)
			}
		})
	}
}

func attrValue(n *html.Node, key string) string {
	for _, attr := range n.Attr {
		if attr.Key == key {
			return attr.Val
		}
	}
	return ""
}
