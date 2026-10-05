package v1

import (
	"net/url"
	"testing"
)

func TestEndpointPrefixEscaping(t *testing.T) {
	for _, base := range []string{"https://tb.example/proxy%2Ftenant/", "https://tb.example/proxy%2F"} {
		for _, resolver := range []bool{false, true} {
			cfg := Config{BaseURL: base}
			if resolver {
				cfg.BaseURL, cfg.Resolver = "", StaticResolver(base)
			}
			c, err := newClient(cfg)
			if err != nil {
				t.Fatal(err)
			}
			got, err := c.url(t.Context(), []string{"v1", "member %"}, url.Values{"limit": {"1"}})
			want := base
			if want[len(want)-1] != '/' {
				want += "/"
			}
			want += "v1/member%20%25?limit=1"
			if err != nil || got != want {
				t.Fatalf("URL = %q, %v; want %q", got, err, want)
			}
		}
	}
}
