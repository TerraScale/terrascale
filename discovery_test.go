package sdk

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestManagementRoutingRequiresExplicitOrigin(t *testing.T) {
	for _, test := range []struct {
		name      string
		discovery string
		data      string
	}{
		{name: "no origin"},
		{name: "canonical metadata", discovery: DefaultDiscoveryBaseURL},
		{name: "custom metadata", discovery: "https://metadata.example.test"},
		{name: "native origin", data: "https://gru.api.terrascale.tech"},
	} {
		t.Run(test.name, func(t *testing.T) {
			calls, nativeCredentialCalls := 0, 0
			client, err := NewClient(Config{
				DiscoveryBaseURL: test.discovery,
				DataBaseURL:      test.data,
				AccessToken:      "management-credential",
				DataTokenProvider: func() string {
					nativeCredentialCalls++
					return nativeDiscoveryTestKey(5)
				},
				HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(*http.Request) (*http.Response, error) {
					calls++
					return nil, errors.New("management authority must be resolved before HTTP")
				})},
			})
			if err != nil {
				t.Fatal(err)
			}
			for _, call := range []func(context.Context) error{
				func(ctx context.Context) error {
					_, err := client.Databases.ListDatabases(ctx, PageRequest{})
					return err
				},
				func(ctx context.Context) error {
					_, err := client.Databases.GetDatabase(ctx, "orders")
					return err
				},
			} {
				err := call(t.Context())
				var routing *RoutingError
				if !errors.As(err, &routing) || !errors.Is(err, ErrManagementDiscoveryUnavailable) {
					t.Fatalf("management endpoint authority error = %v", err)
				}
			}
			if calls != 0 || nativeCredentialCalls != 0 {
				t.Fatalf("management borrowed metadata/native authority: HTTP=%d native credential=%d", calls, nativeCredentialCalls)
			}
		})
	}
}

func TestManagementRoutingDoesNotAdoptCatalogAuthority(t *testing.T) {
	for _, test := range []struct {
		name    string
		catalog string
	}{
		{
			name:    "available GRU",
			catalog: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5,"longitude":-46.6,"available":true}],"hosts":{"GRU":"https://gru.api.terrascale.tech"}}`,
		},
		{
			name:    "unavailable GRU before FRA",
			catalog: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5,"longitude":-46.6,"available":false},{"code":"FRA","name":"Frankfurt","country":"DE","latitude":50,"longitude":8,"available":true}],"hosts":{"GRU":"https://gru.api.terrascale.tech","FRA":"https://fra.api.terrascale.tech"}}`,
		},
		{
			name:    "all unavailable",
			catalog: `{"regions":[{"code":"GRU","name":"Sao Paulo","country":"BR","latitude":-23.5,"longitude":-46.6,"available":false}],"hosts":{"GRU":"https://gru.api.terrascale.tech"}}`,
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			metadataCalls, productCalls, nativeCredentialCalls := 0, 0, 0
			client, err := NewClient(Config{
				APIBaseURL:       "https://product.example.test",
				DiscoveryBaseURL: "https://metadata.example.test",
				AccessToken:      "management-credential",
				DataTokenProvider: func() string {
					nativeCredentialCalls++
					return nativeDiscoveryTestKey(5)
				},
				HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(request *http.Request) (*http.Response, error) {
					if request.URL.Host == "metadata.example.test" {
						metadataCalls++
						return &http.Response{
							StatusCode: http.StatusOK,
							Header:     http.Header{"Content-Type": {"application/json"}},
							Body:       io.NopCloser(strings.NewReader(test.catalog)),
						}, nil
					}
					if request.URL.Host != "product.example.test" || request.Header.Get("Authorization") != "Bearer management-credential" {
						t.Fatalf("management moved origin or credential: host=%q", request.URL.Host)
					}
					productCalls++
					return &http.Response{
						StatusCode: http.StatusForbidden,
						Header:     http.Header{"Content-Type": {"application/json"}},
						Body:       io.NopCloser(strings.NewReader(`{"error":{"code":"forbidden","message":"management fixture denial"}}`)),
					}, nil
				})},
			})
			if err != nil {
				t.Fatal(err)
			}
			for _, call := range []func(context.Context) error{
				func(ctx context.Context) error {
					_, err := client.Databases.ListDatabases(ctx, PageRequest{})
					return err
				},
				func(ctx context.Context) error {
					_, err := client.Databases.GetDatabase(ctx, "orders")
					return err
				},
			} {
				var response *APIError
				if err := call(t.Context()); !errors.As(err, &response) || response.Status != http.StatusForbidden {
					t.Fatalf("explicit management producer response = %v", err)
				}
			}
			if metadataCalls != 0 || productCalls != 2 || nativeCredentialCalls != 0 {
				t.Fatalf("management authority boundary: metadata=%d product=%d native credential=%d", metadataCalls, productCalls, nativeCredentialCalls)
			}
		})
	}
}
