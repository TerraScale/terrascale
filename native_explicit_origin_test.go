package sdk_test

import (
	"errors"
	"net/http"
	"net/url"
	"strings"
	"testing"

	sdk "terrascale.tech/sdk"
)

func TestNativePublicClientExplicitOriginRejectsServiceAliases(t *testing.T) {
	origins := []string{
		"https://terrascale.tech", "https://terrascale.tech.",
		"https://gru.terrascale.tech.", "https://gru.terrascale.tech..",
		"https://GRU.terrascale.tech", "https://gru.TERRASCALE.TECH",
		"https://gru.api.terrascale.tech", "https://gru.auth.terrascale.tech",
		"https://custom.gru.terrascale.tech", "https://native.example.test.",
		"http://localhost.", "https://api。terrascale。tech",
		"https://K.example.test", "https://İ.example.test",
	}
	for _, service := range []string{"api", "atlas", "auth", "sync", "status", "www", "app", "dash", "docs", "git", "cdn", "admin", "mcp", "mail"} {
		origins = append(origins, "https://"+service+".terrascale.tech", "https://"+strings.ToUpper(service)+".TERRASCALE.TECH.")
	}
	for _, origin := range origins {
		t.Run(origin, func(t *testing.T) {
			calls, credentialCalls := 0, 0
			client, err := sdk.NewClient(sdk.Config{
				DataBaseURL: origin,
				DataTokenProvider: func() string {
					credentialCalls++
					return routingContractKey()
				},
				HTTPClient: &http.Client{Transport: routingContractTransport(func(*http.Request) (*http.Response, error) {
					calls++
					return nil, errors.New("unexpected rejected-origin dispatch")
				})},
			})
			if err == nil || client != nil || calls != 0 || credentialCalls != 0 {
				t.Fatalf("origin=%s client=%v err=%v HTTP calls=%d credential calls=%d", origin, client, err, calls, credentialCalls)
			}
		})
	}
}

func TestNativePublicClientExplicitOriginPreservesIndependentEndpoints(t *testing.T) {
	for _, origin := range []string{
		"https://gru.terrascale.tech", "https://xyz-9.terrascale.tech",
		"https://gru.terrascale.tech:8443", "https://native.example.test",
		"https://api.example.test:8443/", "https://auth.example.test",
		"https://CUSTOM.example.test", "https://terrascale.tech.example.test",
		"http://localhost:8080", "http://127.0.0.1:8080", "http://[::1]:8080",
	} {
		t.Run(origin, func(t *testing.T) {
			parsed, err := url.Parse(origin)
			if err != nil {
				t.Fatal(err)
			}
			calls, credentialCalls := 0, 0
			client, err := sdk.NewClient(sdk.Config{
				DataBaseURL: origin,
				DataTokenProvider: func() string {
					credentialCalls++
					return routingContractKey()
				},
				HTTPClient: &http.Client{Transport: routingContractTransport(func(request *http.Request) (*http.Response, error) {
					calls++
					if request.URL.Scheme != parsed.Scheme || request.URL.Host != parsed.Host || request.Header.Get("Authorization") != "Bearer "+routingContractKey() {
						t.Fatalf("explicit endpoint/credential changed: %s", request.URL)
					}
					if !strings.HasSuffix(request.URL.Path, "/documents/"+string(routingContractDocument)) {
						t.Fatalf("wrong public document path: %s", request.URL)
					}
					return routingContractResponse(200, []byte(`{"version":1,"state":"absent","state_version":null,"context":[],"siblings":[]}`)), nil
				})},
			})
			if err != nil {
				t.Fatal(err)
			}
			point, err := client.Documents.Get(t.Context(), routingContractScope, routingContractCollection, routingContractDocument)
			if err != nil || point == nil || point.State != sdk.PointAbsent || calls != 1 || credentialCalls != 1 {
				t.Fatalf("point=%v err=%v HTTP calls=%d credential calls=%d", point, err, calls, credentialCalls)
			}
		})
	}
}
