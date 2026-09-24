package ui

import (
	"bytes"
	"html/template"
	"io/fs"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	static "terrascale.tech/dashboard/web/static"
)

func TestSignedInLayoutsRenderAccessibleShellAndEscapeData(t *testing.T) {
	layouts, err := template.ParseFS(Assets, "layouts/*.html")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := layouts.New("content").Parse(`<main class="test-content"><h1>Overview</h1>{{.Data.Message}}</main>`); err != nil {
		t.Fatal(err)
	}

	page := core.Page{
		Template: "home", Layout: "shell", Title: "Overview", Nonce: "nonce_test_123",
		CSRFToken: "csrf_test_123", DevMode: true,
		Principal: core.Principal{PlatformAdmin: true},
		Data:      struct{ Message string }{Message: `<script>alert("x")</script>`},
		Shell: core.Shell{
			UserName: "Avery Example", UserEmail: "avery@example.test", UserInitials: "AE",
			OrganizationName: "Org <script>", ProjectName: "Production",
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
			Navigation: []core.NavItem{{Label: "Overview", URL: "/", Active: true}},
			ReturnURL:  "/?tab=overview&sort=name",
		},
	}

	var rendered bytes.Buffer
	if err := layouts.ExecuteTemplate(&rendered, "shell", page); err != nil {
		t.Fatal(err)
	}
	html := rendered.String()
	for _, want := range []string{
		`<html lang="en" data-theme="dark" data-nonce="nonce_test_123" data-dev-reload>`,
		`aria-label="Open navigation"`,
		`aria-label="Close navigation"`,
		`aria-label="Primary navigation"`,
		`aria-label="Account menu"`,
		`aria-current="page"`,
		`data-active-project-id="project-1"`,
		`data-active-organization-id="org-1"`,
		`id="organizationSearch"`,
		`id="projectSearch"`,
		`data-show=`,
		`action="/api/v1/dashboard/switch-project"`,
		`name="returnUrl" value="/?tab=overview&amp;sort=name"`,
		`action="/api/v1/dashboard/switch-organization"`,
		`action="/api/v1/auth/logout"`,
		`name="_csrf" value="csrf_test_123"`,
		`src="/brand/logo-terrascale-blue.svg"`,
		`<span class="ts-account-avatar" aria-hidden="true">AE</span>`,
		`nonce="nonce_test_123"`,
		`<script type="module" src="/js/datastar.js" defer nonce="nonce_test_123"></script>`,
		`<div id="ts-main" class="ts-main" tabindex="-1">`,
		`<h1>Overview</h1>`,
		`&lt;script&gt;alert(&#34;x&#34;)&lt;/script&gt;`,
		`Org &lt;script&gt;`,
	} {
		if !strings.Contains(html, want) {
			t.Errorf("rendered shell is missing %q", want)
		}
	}
	if strings.Contains(html, `<script>alert("x")</script>`) || strings.Contains(html, `Org <script>`) {
		t.Fatal("untrusted page data was rendered without HTML escaping")
	}
	if strings.Count(html, `data-dashboard-css`) != 1 {
		t.Fatalf("want one stylesheet link, got %d", strings.Count(html, `data-dashboard-css`))
	}
	if count := strings.Count(html, "<main"); count != 1 {
		t.Fatalf("want one main landmark with a feature page main, got %d", count)
	}
	if count := strings.Count(html, "<h1"); count != 1 {
		t.Fatalf("want one page heading owned by the feature template, got %d", count)
	}
}

func TestAdminLayoutUsesAdminNavigationSemantics(t *testing.T) {
	layouts, err := template.ParseFS(Assets, "layouts/*.html")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := layouts.New("content").Parse(`<section>Admin page</section>`); err != nil {
		t.Fatal(err)
	}
	page := core.Page{
		Layout: "admin", Title: "Users",
		Shell: core.Shell{Navigation: []core.NavItem{{Label: "Users", URL: "/admin/users", Active: true}}},
	}
	var rendered bytes.Buffer
	if err := layouts.ExecuteTemplate(&rendered, "admin", page); err != nil {
		t.Fatal(err)
	}
	html := rendered.String()
	for _, want := range []string{
		`class="ts-sidebar ts-admin-sidebar"`,
		`aria-label="Admin navigation"`,
		`aria-label="Admin sections"`,
		`Administration</div>`,
		`href="/">Back to dashboard</a>`,
		`aria-current="page"`,
	} {
		if !strings.Contains(html, want) {
			t.Errorf("rendered admin shell is missing %q", want)
		}
	}
	if strings.Count(html, `src="/brand/logo-terrascale-blue.svg"`) != 1 {
		t.Fatal("admin sidebar should render one file-backed brand logo")
	}
}

func TestAuthLayoutUsesCSPNonceAndFileBackedAssets(t *testing.T) {
	layouts, err := template.ParseFS(Assets, "layouts/*.html")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := layouts.New("content").Parse(`<main>Sign in</main>`); err != nil {
		t.Fatal(err)
	}
	page := core.Page{Layout: "auth", Title: "Sign in", Nonce: "auth_nonce_456"}
	var rendered bytes.Buffer
	if err := layouts.ExecuteTemplate(&rendered, "auth", page); err != nil {
		t.Fatal(err)
	}
	html := rendered.String()
	for _, want := range []string{
		`data-nonce="auth_nonce_456"`,
		`href="/brand/logo-icon-blue.svg"`,
		`href="/css/terrascale.css"`,
		`type="module" src="/js/datastar.js" defer nonce="auth_nonce_456"`,
		`src="/js/dashboard.js" defer nonce="auth_nonce_456"`,
		`href="#ts-auth-main"`,
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
	for _, path := range []string{
		"brand/logo-terrascale-blue.svg",
		"brand/logo-terrascale-auth.svg",
		"brand/logo-icon-blue.svg",
		"brand/region-map.svg",
		"fonts/oxanium-latin-wght-normal.woff2",
		"fonts/jetbrains-mono-latin-wght-normal.woff2",
		"js/datastar.js",
		"js/dashboard.js",
	} {
		if _, err := fs.Stat(static.Assets, path); err != nil {
			t.Errorf("public asset %q is missing: %v", path, err)
		}
	}
	datastar, err := fs.ReadFile(static.Assets, "js/datastar.js")
	if err != nil {
		t.Fatal(err)
	}
	if !bytes.Contains(datastar, []byte("Datastar v1.0.4")) {
		t.Fatal("vendored browser bundle is not Datastar v1.0.4")
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
