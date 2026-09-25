package ui

import (
	"bytes"
	"encoding/json"
	"io/fs"
	"strings"
	"testing"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	static "terrascale.tech/dashboard/web/static"
)

func TestSignedInTemplLayoutRendersAccessibleShellAndEscapesData(t *testing.T) {
	page := core.Page{
		Title: "Overview", Nonce: "nonce_test_123", DevMode: true,
		CSRFToken: "csrf_test_123", Principal: core.Principal{PlatformAdmin: true},
		Shell: core.Shell{
			UserName: "Avery <script>alert(1)</script>", UserEmail: "avery@example.test",
			UserInitials: "AE", OrganizationName: "Org <script>", ProjectName: "Production",
			Organizations: []core.OrganizationOption{
				{ID: "org-1", Name: "Org <script>", Selected: true},
				{ID: "org-2", Name: "North \"blue\""},
				{ID: "org-3", Name: "Org 3"},
				{ID: "org-4", Name: "Org 4"},
				{ID: "org-5", Name: "Org 5"},
				{ID: "org-6", Name: "Org 6"},
			},
			Projects: []core.ProjectOption{
				{ID: "project-1", Name: "Production", Selected: true},
				{ID: "project-2", Name: "Staging"},
				{ID: "project-3", Name: "Project 3"},
				{ID: "project-4", Name: "Project 4"},
				{ID: "project-5", Name: "Project 5"},
				{ID: "project-6", Name: "Project 6"},
			},
			Navigation: []core.NavGroup{
				{Label: "Build", Items: []core.NavItem{
					{Label: "Overview", URL: "/", Active: true},
				}},
				{Label: "Manage", Items: []core.NavItem{
					{Label: "Billing", URL: "/billing"},
					{Label: "Organization", URL: "/organization", BadgeCount: 6},
					{Label: "Settings", URL: "/settings"},
				}},
			},
			ReturnURL: "/?tab=overview&sort=name",
		},
	}
	html := renderTempl(t, Shell(page, templ.Raw(`<main class="test-content"><h1>Overview</h1></main>`)))
	for _, want := range []string{
		`<html lang="en" data-theme="dark" data-nonce="nonce_test_123">`,
		`data-init="@get('/__dev/events'`,
		`data-class:ts-nav-pending="$_navPending"`,
		`data-attr:inert="$navOpen"`,
		`aria-label="Open navigation"`,
		`aria-label="Close navigation"`,
		`aria-label="Primary navigation"`,
		`aria-label="Account menu"`,
		`aria-current="page"`,
		`class="ts-nav-group-label">Build</div>`,
		`class="ts-nav-group-label">Manage</div>`,
		`class="ts-nav-link" href="/organization" data-tone="indigo"`,
		`class="ts-nav-link" href="/" data-tone="violet" aria-current="page"`,
		`class="ts-nav-count" aria-hidden="true">6</span>`,
		`rel="preload" href="/fonts/oxanium-latin-wght-normal.woff2" as="font" type="font/woff2" crossorigin`,
		`href="/billing"`,
		`data-active-project-id="project-1"`,
		`data-active-organization-id="org-1"`,
		`id="organizationSearch"`,
		`id="projectSearch"`,
		`data-show="$organizationSearch === '' || el.dataset.organizationName.toLowerCase().includes($organizationSearch.toLowerCase())"`,
		`data-show="$projectSearch === '' || el.dataset.projectName.toLowerCase().includes($projectSearch.toLowerCase())"`,
		`data-organization-name="Org &lt;script&gt;"`,
		`data-project-name="Production"`,
		`href="/organization?newProject=1#projects" data-tone="emerald">`,
		`href="/settings?tab=identity" data-tone="slate">`,
		`<span>Create new project</span></a>`,
		`<span>All workspaces</span></a>`,
		`action="/api/v1/dashboard/switch-project"`,
		`name="returnUrl" value="/?tab=overview&amp;sort=name"`,
		`action="/api/v1/dashboard/switch-organization"`,
		`action="/api/v1/auth/logout"`,
		`name="_csrf" value="csrf_test_123"`,
		`src="/brand/logo-terrascale-principal.svg"`,
		`<span class="ts-account-avatar" aria-hidden="true">AE</span>`,
		`nonce="nonce_test_123"`,
		`<script type="module" src="` + datastarURL + `" integrity="` + datastarIntegrity + `" crossorigin="anonymous" nonce="nonce_test_123"></script>`,
		`<script type="speculationrules" nonce="nonce_test_123">`,
		`href="` + static.URL("css/terrascale.css") + `"`,
		`class="ts-shell" data-signals="{navOpen: false}" data-class:nav-open="$navOpen"`,
		`class="ts-menu ts-context-menu"`,
		`aria-label="Switch organization or project, current: Org &lt;script&gt; / Production"`,
		`<div id="ts-main" class="ts-main" tabindex="-1">`,
		`<h1>Overview</h1>`,
		`Org &lt;script&gt;`,
		`Avery &lt;script&gt;alert(1)&lt;/script&gt;`,
	} {
		if !strings.Contains(html, want) {
			t.Errorf("rendered shell is missing %q", want)
		}
	}
	if strings.Contains(html, `<script>alert(1)</script>`) || strings.Contains(html, `Org <script>`) {
		t.Fatal("untrusted shell data was rendered without HTML escaping")
	}
	if strings.Count(html, `data-dashboard-css`) != 1 {
		t.Fatalf("want one stylesheet link, got %d", strings.Count(html, `data-dashboard-css`))
	}
	if strings.Count(html, "<script") != 2 || strings.Count(html, `src="`+datastarURL+`"`) != 1 {
		t.Fatal("the shell must load the CDN Datastar bundle as its only script, next to the speculation rules")
	}
	if count := strings.Count(html, `data-on:click__outside="el.open = false"`); count != 2 {
		t.Fatalf("want the context and account menus, got %d", count)
	}
	if count := strings.Count(html, "<main"); count != 1 {
		t.Fatalf("want one main landmark with a feature page main, got %d", count)
	}
	if count := strings.Count(html, "<h1"); count != 1 {
		t.Fatalf("want one page heading owned by the feature template, got %d", count)
	}
}

func TestAdminTemplLayoutUsesAdminNavigationSemantics(t *testing.T) {
	page := core.Page{
		Title: "Users", Nonce: "admin_nonce_123",
		Shell: core.Shell{Navigation: []core.NavGroup{{Items: []core.NavItem{{Label: "Users", URL: "/admin/users", Active: true}}}}},
	}
	html := renderTempl(t, Admin(page, templ.Raw(`<section>Admin page</section>`)))
	for _, want := range []string{
		`class="ts-sidebar ts-admin-sidebar"`,
		`aria-label="Admin navigation"`,
		`aria-label="Admin sections"`,
		`class="ts-badge ts-badge-violet">Admin</span>`,
		`Back to dashboard</span>`,
		`data-tone="pink" aria-current="page"`,
		`aria-current="page"`,
	} {
		if !strings.Contains(html, want) {
			t.Errorf("rendered admin shell is missing %q", want)
		}
	}
	if strings.Count(html, `src="/brand/logo-terrascale-principal.svg"`) != 2 {
		t.Fatal("admin shell should render the file-backed brand logo in the sidebar and mobile top bar")
	}
}

func TestAuthTemplLayoutUsesCSPNonceAndFileBackedAssets(t *testing.T) {
	page := core.Page{Title: "Sign in", Nonce: "auth_nonce_456"}
	html := renderTempl(t, Auth(page, templ.Raw(`<main>Sign in</main>`)))
	if strings.Contains(html, "__dev/events") {
		t.Error("production pages must not open the development event stream")
	}
	for _, want := range []string{
		`data-nonce="auth_nonce_456"`,
		`href="/brand/logo-icon-blue.svg"`,
		`href="/css/terrascale.css?v=`,
		`type="module" src="` + datastarURL + `" integrity="sha384-`,
		`href="#ts-auth-main"`,
		`<title>Sign in · TerraScale</title>`,
	} {
		if !strings.Contains(html, want) {
			t.Errorf("rendered auth layout is missing %q", want)
		}
	}
}

func TestEmbeddedStaticAssetsContainOneBundledStylesheetAndBrand(t *testing.T) {
	cssFiles, err := fs.Glob(static.Assets, "css/*.css")
	if err != nil {
		t.Fatal(err)
	}
	if len(cssFiles) != 1 || cssFiles[0] != "css/terrascale.css" {
		t.Fatalf("want one public CSS bundle, got %v", cssFiles)
	}
	css, err := fs.ReadFile(static.Assets, cssFiles[0])
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Contains(css, []byte("@import")) {
		t.Fatal("public CSS bundle still contains unbundled imports")
	}
	for _, selector := range [][]byte{
		[]byte("ts-nav-pending"),
		[]byte("ts-menu"),
		[]byte("ts-tabs"),
		[]byte("ts-region-map"),
		[]byte("ts-region-map-image"),
		[]byte("ts-region-overlay"),
		[]byte("ts-region-arc"),
		[]byte("ts-region-node-ring"),
		[]byte("ts-region-node-core"),
	} {
		if !bytes.Contains(css, selector) {
			t.Errorf("public CSS bundle is missing style %q", selector)
		}
	}
	for _, path := range []string{
		"brand/logo-terrascale-principal.svg",
		"brand/logo-terrascale-auth.svg",
		"brand/logo-icon-blue.svg",
		"brand/region-map.svg",
		"fonts/oxanium-latin-wght-normal.woff2",
		"fonts/jetbrains-mono-latin-wght-normal.woff2",
	} {
		if _, err := fs.Stat(static.Assets, path); err != nil {
			t.Errorf("public asset %q is missing: %v", path, err)
		}
	}
	if scripts, _ := fs.Glob(static.Assets, "*/*.js"); len(scripts) != 0 {
		t.Fatalf("scripts must not be served from the dashboard, got %v", scripts)
	}
	if !strings.Contains(datastarURL, "@v1.0.4/bundles/datastar-rocket.js") {
		t.Fatalf("Datastar CDN bundle is not the pinned v1.0.4 Rocket build: %s", datastarURL)
	}
	regionMap, err := fs.ReadFile(static.Assets, "brand/region-map.svg")
	if err != nil {
		t.Fatal(err)
	}
	for _, want := range []string{
		`id="terrascale-region-map"`,
		`data-projection="vanDerGrinten"`,
		`data-grid="diagonal"`,
		`<title id="terrascale-region-map-title">TerraScale region map</title>`,
		`<desc id="terrascale-region-map-description">`,
	} {
		if !bytes.Contains(regionMap, []byte(want)) {
			t.Errorf("region map SVG is missing %q", want)
		}
	}
}

func renderTempl(t *testing.T, component templ.Component) string {
	t.Helper()
	var rendered strings.Builder
	if err := component.Render(t.Context(), &rendered); err != nil {
		t.Fatal(err)
	}
	return rendered.String()
}

func TestSpeculationRulesAreValidAndSkipSideEffectRoutes(t *testing.T) {
	var rules struct {
		Prefetch []struct {
			Eagerness string `json:"eagerness"`
		} `json:"prefetch"`
	}
	if err := json.Unmarshal([]byte(speculationRules), &rules); err != nil || len(rules.Prefetch) != 1 || rules.Prefetch[0].Eagerness != "moderate" {
		t.Fatalf("speculation rules = %v, %+v", err, rules)
	}
	for _, route := range []string{"/api/*", "/billing/*", "/checkout/*", "/audit/export*", "/invitations/*"} {
		if !strings.Contains(speculationRules, `"`+route+`"`) {
			t.Errorf("speculation rules prefetch side-effect route %s", route)
		}
	}
}
