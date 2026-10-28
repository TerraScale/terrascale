package main

import (
	"context"
	"encoding/json"
	"net"
	"net/http"
	"net/http/httptest"
	"testing"

	"terrascale.tech/sdk/v1"
)

// Transport fixtures test measurement rejection, not TerraBase qualification.
func TestParityRejectsWrongStatusRegionProtocolAndDocument(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name, region, lane string
		status             int
		rejected           bool
	}{
		{"valid", "gru", "raw-h2", 200, false}, {"wrong-region", "fra", "raw-h2", 200, true},
		{"wrong-protocol", "gru", "raw-h1", 200, true}, {"hidden-retry-response", "gru", "sdk", 503, true},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Header.Get("X-TerraScale-Region-Pin") != "gru" {
					t.Error("missing request pin")
				}
				w.Header().Set("X-TerraBase-Region", test.region)
				w.WriteHeader(test.status)
				_, _ = w.Write([]byte(`{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","version":"opaque","state":"value","document":{"payload":"x"}}`))
			}))
			server.EnableHTTP2 = true
			server.StartTLS()
			defer server.Close()
			base := server.Client().Transport.(*http.Transport).Clone()
			base.TLSClientConfig.ServerName = server.Certificate().DNSNames[0]
			base.DialContext = func(ctx context.Context, network, _ string) (net.Conn, error) {
				return (&net.Dialer{}).DialContext(ctx, network, server.Listener.Addr().String())
			}
			defer base.CloseIdleConnections()
			observed := &parityTransport{base: base, region: "gru", protocol: test.lane}
			request, err := http.NewRequest(http.MethodGet, parityOrigin+"/v1/projects/p/databases/d/collections/c/documents/by-key?id=x", nil)
			if err != nil {
				t.Fatal(err)
			}
			response, err := observed.RoundTrip(request)
			if (err != nil) != test.rejected {
				t.Fatalf("rejection=%v, want %v", err, test.rejected)
			}
			if test.rejected {
				if observed.bad.Load() != 1 {
					t.Fatal("failed attempt hidden")
				}
				return
			}
			defer response.Body.Close()
			var document v1.Document
			if err := json.NewDecoder(response.Body).Decode(&document); err != nil {
				t.Fatal(err)
			}
			expected := parityDocument{id: document.ID, version: document.Version, body: json.RawMessage(`{"payload":"x"}`)}
			if expected.check(&document) != nil {
				t.Fatal("valid document rejected")
			}
			document.Version = "different"
			if expected.check(&document) == nil {
				t.Fatal("wrong version accepted")
			}
		})
	}
}
