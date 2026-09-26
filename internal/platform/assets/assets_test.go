package assets

import (
	"net/http"
	"net/http/httptest"
	"testing"
	"testing/fstest"
)

func TestPrecompressedServesBrotliSibling(t *testing.T) {
	files := fstest.MapFS{
		"editor.ABCD1234.js":    {Data: []byte("plain")},
		"editor.ABCD1234.js.br": {Data: []byte("brotli")},
		"only-plain.js":         {Data: []byte("plain")},
	}
	handler := precompressed(files, http.FileServerFS(files))
	for _, tc := range []struct {
		name, path, accept, body, encoding string
	}{
		{"brotli client", "/editor.ABCD1234.js", "gzip, deflate, br, zstd", "brotli", "br"},
		{"refuses brotli", "/editor.ABCD1234.js", "gzip, br;q=0", "plain", ""},
		{"no brotli", "/editor.ABCD1234.js", "gzip", "plain", ""},
		{"no sibling", "/only-plain.js", "br", "plain", ""},
	} {
		t.Run(tc.name, func(t *testing.T) {
			request := httptest.NewRequest(http.MethodGet, tc.path, nil)
			request.Header.Set("Accept-Encoding", tc.accept)
			recorder := httptest.NewRecorder()
			handler.ServeHTTP(recorder, request)
			if got := recorder.Body.String(); got != tc.body {
				t.Fatalf("body = %q, want %q", got, tc.body)
			}
			if got := recorder.Header().Get("Content-Encoding"); got != tc.encoding {
				t.Fatalf("Content-Encoding = %q, want %q", got, tc.encoding)
			}
			if got := recorder.Header().Get("Content-Type"); got != "text/javascript; charset=utf-8" {
				t.Fatalf("Content-Type = %q", got)
			}
			if got := recorder.Header().Get("Vary"); got != "Accept-Encoding" {
				t.Fatalf("Vary = %q", got)
			}
		})
	}
}

func TestHashedNameMatchesBuildOutputs(t *testing.T) {
	for name, want := range map[string]bool{
		"js/editor-core.P4OPRQN4.js": true,
		"js/codicon.9D25513C.ttf":    true,
		"js/code-editor.js":          false,
		"css/terrascale.css":         false,
		"img/404-number.webp":        false,
	} {
		if got := hashedName.MatchString(name); got != want {
			t.Errorf("hashedName(%q) = %v, want %v", name, got, want)
		}
	}
}
