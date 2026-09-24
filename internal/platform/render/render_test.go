package render

import (
	"io/fs"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"testing/fstest"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui"
)

type fragmentCSRF struct{}

func (fragmentCSRF) Token(http.ResponseWriter, *http.Request) string { return "token&<" }
func (fragmentCSRF) Verify(*http.Request) error                      { return nil }
func (fragmentCSRF) Protect(handler http.Handler) http.Handler       { return handler }

func TestFragmentPassesEscapedCSRFTokenToTemplate(t *testing.T) {
	layouts := fstest.MapFS{"base.html": &fstest.MapFile{Data: []byte(`{{define "shell"}}{{end}}`)}}
	renderer, err := New(fs.FS(layouts), nil, fragmentCSRF{})
	if err != nil {
		t.Fatal(err)
	}
	feature := fstest.MapFS{"access.html": &fstest.MapFile{Data: []byte(`{{define "content"}}<form><input value="{{.CSRFToken}}"><p>{{.Data}}</p></form>{{end}}`)}}
	if err := renderer.Register("access", feature, "access.html"); err != nil {
		t.Fatal(err)
	}
	response := httptest.NewRecorder()
	request := httptest.NewRequest(http.MethodGet, "/access", nil)
	if err := renderer.Fragment(response, request, core.Fragment{Template: "access", Data: `<script>alert(1)</script>`}); err != nil {
		t.Fatal(err)
	}
	got := response.Body.String()
	if !strings.Contains(got, `value="token&amp;&lt;"`) || !strings.Contains(got, `&lt;script&gt;alert(1)&lt;/script&gt;`) || strings.Contains(got, "<script>") {
		t.Fatalf("fragment token or data was not escaped: %s", got)
	}
}

func TestPageRendersBrandSuffixOnce(t *testing.T) {
	layouts, err := fs.Sub(ui.Assets, "layouts")
	if err != nil {
		t.Fatal(err)
	}
	renderer, err := New(layouts, nil, nil)
	if err != nil {
		t.Fatal(err)
	}
	feature := fstest.MapFS{"title.html": &fstest.MapFile{Data: []byte(`{{define "content"}}title{{end}}`)}}
	if err := renderer.Register("title", feature, "title.html"); err != nil {
		t.Fatal(err)
	}
	for _, example := range []struct{ input, want string }{
		{"Databases · TerraScale", "Databases · TerraScale"},
		{"Subscriptions | TerraScale", "Subscriptions · TerraScale"},
		{"TerraScale", "TerraScale"},
		{"Activate your TerraScale account", "Activate your TerraScale account · TerraScale"},
	} {
		response := httptest.NewRecorder()
		request := httptest.NewRequest(http.MethodGet, "/title", nil)
		if err := renderer.Page(response, request, core.Page{Template: "title", Layout: "auth", Title: example.input}); err != nil {
			t.Fatal(err)
		}
		if !strings.Contains(response.Body.String(), "<title>"+example.want+"</title>") {
			t.Fatalf("title %q rendered unexpected document title", example.input)
		}
	}
}
