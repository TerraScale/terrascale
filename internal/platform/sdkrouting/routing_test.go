package sdkrouting_test

import (
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"

	sdk "terrascale.tech/sdk"
)

type transportFunc func(*http.Request) (*http.Response, error)

func (f transportFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func response(body string) *http.Response {
	return &http.Response{StatusCode: http.StatusOK, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(strings.NewReader(body))}
}

func TestPinnedSDKUsesCanonicalRegionalOrigin(t *testing.T) {
	for _, tc := range []struct{ base, want string }{
		{sdk.DefaultAPIBaseURL, "https://gru.api.terrascale.tech"},
		{"https://private.example.test", "https://private.example.test"},
	} {
		t.Run(tc.base, func(t *testing.T) {
			calls := 0
			client, err := sdk.NewClient(sdk.Config{
				APIBaseURL: tc.base, Affinity: sdk.AffinityStrict, PreferredRegion: sdk.RegionCode("GRU"), AccessToken: "scoped-key",
				HTTPClient: &http.Client{Transport: transportFunc(func(r *http.Request) (*http.Response, error) {
					calls++
					if r.URL.Scheme+"://"+r.URL.Host != tc.want {
						t.Errorf("origin = %s, want %s", r.URL, tc.want)
					}
					if r.Header.Get("Authorization") != "Bearer scoped-key" {
						t.Error("missing data credential")
					}
					return response(`{"value":"ok"}`), nil
				})},
			})
			if err != nil {
				t.Fatal(err)
			}
			if _, err := client.Documents.Get(t.Context(), "dashboard-billing", "customers", "org_a"); err != nil {
				t.Fatal(err)
			}
			if calls != 1 {
				t.Fatalf("requests = %d, want 1", calls)
			}
		})
	}
}

func TestPinnedSDKDiscoveryAcceptsAPIOriginAndRejectsRetiredHost(t *testing.T) {
	for _, endpoint := range []string{"https://gru.api.terrascale.tech", "https://gru.terrascale.tech"} {
		t.Run(endpoint, func(t *testing.T) {
			dataCalls := 0
			client, err := sdk.NewClient(sdk.Config{
				AccessToken: "scoped-key",
				HTTPClient: &http.Client{Transport: transportFunc(func(r *http.Request) (*http.Response, error) {
					if r.URL.Host == "atlas.terrascale.tech" {
						if r.Header.Get("Authorization") != "" {
							t.Error("data credential sent to public discovery")
						}
						if r.URL.Path != "/api/v1/discovery" {
							return nil, fmt.Errorf("unexpected catalog fallback: %s", r.URL)
						}
						return response(fmt.Sprintf(`{"v":1,"database":"dashboard-billing","branch":"main","revision":1,"residency":null,"refreshSeconds":60,"endpoints":[{"region":"GRU","url":%q,"priority":1,"capabilities":["read","write"]}]}`, endpoint)), nil
					}
					dataCalls++
					if r.URL.Host != "gru.api.terrascale.tech" {
						t.Errorf("data origin = %s", r.URL)
					}
					if r.Header.Get("Authorization") != "Bearer scoped-key" {
						t.Error("missing data credential")
					}
					return response(`{"value":"ok"}`), nil
				})},
			})
			if err != nil {
				t.Fatal(err)
			}
			_, err = client.Documents.Get(t.Context(), "dashboard-billing", "customers", "org_a")
			if endpoint == "https://gru.api.terrascale.tech" {
				if err != nil {
					t.Fatal(err)
				}
				if dataCalls != 1 {
					t.Fatalf("data requests = %d, want 1", dataCalls)
				}
			} else if err == nil || dataCalls != 0 {
				t.Fatalf("retired host: error = %v, data requests = %d", err, dataCalls)
			}
		})
	}
}
