package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"reflect"
	"testing"
	"time"
)

func nativeRankedFixture(t *testing.T) nativeDiscoveryDocument {
	t.Helper()
	_, document := nativeDiscoveryFixture(t)
	second := document.Endpoints[0]
	second.Region, second.URL, second.Priority = "FRA", "https://fra.api.terrascale.tech", 2
	document.Endpoints = append(document.Endpoints, second)
	return document
}

func TestHostedPinRefresh(t *testing.T) {
	document := nativeRankedFixture(t)
	now := time.Unix(document.PublishedAt, 0)
	atlas := 0
	var hosts []string
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "atlas.terrascale.tech" {
			atlas++
			body, err := json.Marshal(document)
			if err != nil {
				t.Fatal(err)
			}
			return nativeDiscoveryResponse(200, body), nil
		}
		hosts = append(hosts, r.URL.Host)
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	}))
	client.nativeRouter.now = func() time.Time { return now }
	read := func() {
		t.Helper()
		if _, err := client.Documents.Get(t.Context(), document.Scope.documentScope(), nativeTestCollection, nativeTestDocument); err != nil {
			t.Fatal(err)
		}
	}
	read()
	now = now.Add(7999 * time.Millisecond)
	read()
	if atlas != 1 {
		t.Fatalf("pin refreshed early: %d", atlas)
	}
	now = now.Add(time.Millisecond)
	document.PublishedAt, document.ExpiresAt = now.Unix(), now.Unix()+9
	document.Endpoints[0].Priority, document.Endpoints[1].Priority = 2, 1
	document.Endpoints[0], document.Endpoints[1] = document.Endpoints[1], document.Endpoints[0]
	read()
	if atlas != 2 || !reflect.DeepEqual(hosts, []string{"gru.api.terrascale.tech", "gru.api.terrascale.tech", "fra.api.terrascale.tech"}) {
		t.Fatalf("refresh routing: atlas=%d hosts=%v", atlas, hosts)
	}
}

func TestHostedRegionalFailover(t *testing.T) {
	for _, fault := range []int{0, 421, 500, 503} {
		t.Run(http.StatusText(fault), func(t *testing.T) {
			document := nativeRankedFixture(t)
			now := time.Unix(document.PublishedAt, 0)
			body, _ := json.Marshal(document)
			atlas := 0
			var hosts []string
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				if r.URL.Host == "atlas.terrascale.tech" {
					atlas++
					return nativeDiscoveryResponse(200, body), nil
				}
				hosts = append(hosts, r.URL.Host)
				if r.URL.Host == "gru.api.terrascale.tech" {
					if fault == 0 {
						return nil, errors.New("connection reset")
					}
					return nativeDiscoveryResponse(fault, []byte("regional failure")), nil
				}
				return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
			}))
			client.nativeRouter.now = func() time.Time { return now }
			read := func() {
				t.Helper()
				if _, err := client.Documents.Get(t.Context(), document.Scope.documentScope(), nativeTestCollection, nativeTestDocument); err != nil {
					t.Fatal(err)
				}
			}
			read()
			read()
			if atlas != 2 || !reflect.DeepEqual(hosts, []string{"gru.api.terrascale.tech", "fra.api.terrascale.tech", "fra.api.terrascale.tech"}) {
				t.Fatalf("failover routing: atlas=%d hosts=%v", atlas, hosts)
			}
			now = now.Add(8 * time.Second)
			document.PublishedAt, document.ExpiresAt = now.Unix(), now.Unix()+9
			body, _ = json.Marshal(document)
			read()
			if !reflect.DeepEqual(hosts[len(hosts)-2:], []string{"gru.api.terrascale.tech", "fra.api.terrascale.tech"}) {
				t.Fatalf("new lease did not retry priority one: %v", hosts)
			}
		})
	}
}

func TestHostedProxyFallbackRefresh(t *testing.T) {
	for _, fault := range []string{"404", "500", "503", "legacy", "network"} {
		t.Run(fault, func(t *testing.T) {
			document := nativeRankedFixture(t)
			now := time.Unix(document.PublishedAt, 0)
			atlas := 0
			var hosts []string
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				if r.Header.Get("Authorization") != "Bearer "+nativeDiscoveryTestKey(5) {
					t.Error("native credential changed")
				}
				if r.URL.Host == "atlas.terrascale.tech" {
					atlas++
					if atlas > 1 {
						body, _ := json.Marshal(document)
						return nativeDiscoveryResponse(200, body), nil
					}
					switch fault {
					case "network":
						return nil, errors.New("Atlas unavailable")
					case "legacy":
						return nativeDiscoveryResponse(200, []byte(`{"version":1,"database":"old","endpoints":[]}`)), nil
					case "404":
						return nativeDiscoveryResponse(404, nil), nil
					case "503":
						return nativeDiscoveryResponse(503, nil), nil
					default:
						return nativeDiscoveryResponse(500, nil), nil
					}
				}
				hosts = append(hosts, r.URL.Host)
				return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
			}))
			client.nativeRouter.now = func() time.Time { return now }
			read := func() {
				t.Helper()
				if _, err := client.Documents.Get(t.Context(), document.Scope.documentScope(), nativeTestCollection, nativeTestDocument); err != nil {
					t.Fatal(err)
				}
			}
			read()
			now = now.Add(7999 * time.Millisecond)
			read()
			if atlas != 1 {
				t.Fatalf("proxy refreshed early: %d", atlas)
			}
			now = now.Add(time.Millisecond)
			document.PublishedAt, document.ExpiresAt = now.Unix(), now.Unix()+9
			read()
			if atlas != 2 || !reflect.DeepEqual(hosts, []string{"api.terrascale.tech", "api.terrascale.tech", "gru.api.terrascale.tech"}) {
				t.Fatalf("fallback routing: atlas=%d hosts=%v", atlas, hosts)
			}
		})
	}
}

func TestHostedUncertainCommitDoesNotReplay(t *testing.T) {
	document := nativeRankedFixture(t)
	body, _ := json.Marshal(document)
	atlas := 0
	var hosts []string
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "atlas.terrascale.tech" {
			atlas++
			return nativeDiscoveryResponse(200, body), nil
		}
		hosts = append(hosts, r.URL.Host)
		if r.Method == http.MethodPost {
			return nativeDiscoveryResponse(503, []byte(`{"error":{"code":"commit_uncertain"}}`)), nil
		}
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	}))
	write := nativeTestWrite()
	write.Condition.Kind = ConditionNone
	request, err := NewCommitRequest(document.Scope.documentScope(), nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{write})
	if err != nil {
		t.Fatal(err)
	}
	_, err = client.Documents.Commit(t.Context(), document.Scope.documentScope(), request)
	var uncertain *CommitError
	if !errors.As(err, &uncertain) || !uncertain.Uncertain || !reflect.DeepEqual(hosts, []string{"gru.api.terrascale.tech"}) {
		t.Fatalf("commit replayed or uncertainty lost: %v %v", hosts, err)
	}
	if _, err := client.Documents.Get(t.Context(), document.Scope.documentScope(), nativeTestCollection, nativeTestDocument); err != nil {
		t.Fatal(err)
	}
	if atlas != 2 || !reflect.DeepEqual(hosts, []string{"gru.api.terrascale.tech", "fra.api.terrascale.tech"}) {
		t.Fatalf("failed commit did not change pin: %v", hosts)
	}
}

func TestHostedCustomEndpointDisablesDiscovery(t *testing.T) {
	var hosts []string
	client, err := NewClient(Config{DataBaseURL: "https://native.example.test", DataAccessToken: "custom-key", HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		hosts = append(hosts, r.URL.Host)
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.Documents.Get(t.Context(), nativeTestScope, nativeTestCollection, nativeTestDocument); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(hosts, []string{"native.example.test"}) {
		t.Fatalf("custom endpoint discovered: %v", hosts)
	}
	for _, endpoint := range []string{"https://gru.api.terrascale.tech", "https://xyz-9.api.terrascale.tech"} {
		if _, err := NewClient(Config{DataBaseURL: endpoint}); err != nil {
			t.Errorf("canonical origin rejected: %v", err)
		}
	}
}

func TestHostedDiscoveryTimeoutUsesProxy(t *testing.T) {
	var hosts []string
	client, err := NewClient(Config{DataAccessToken: nativeDiscoveryTestKey(5), DataDiscoveryTimeout: 10 * time.Millisecond, RequestTimeout: 500 * time.Millisecond, HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		hosts = append(hosts, r.URL.Host)
		if r.URL.Host == "atlas.terrascale.tech" {
			<-r.Context().Done()
			return nil, r.Context().Err()
		}
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.Documents.Get(t.Context(), nativeTestScope, nativeTestCollection, nativeTestDocument); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(hosts, []string{"atlas.terrascale.tech", "api.terrascale.tech"}) {
		t.Fatalf("discovery timeout routing: %v", hosts)
	}
}

type nativeBrokenBody struct{}

func (nativeBrokenBody) Read([]byte) (int, error) { return 0, errors.New("connection reset") }
func (nativeBrokenBody) Close() error             { return nil }

func TestHostedResponseStreamFailover(t *testing.T) {
	document := nativeRankedFixture(t)
	body, _ := json.Marshal(document)
	var hosts []string
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "atlas.terrascale.tech" {
			return nativeDiscoveryResponse(200, body), nil
		}
		hosts = append(hosts, r.URL.Host)
		if r.URL.Host == "gru.api.terrascale.tech" {
			response := nativeDiscoveryResponse(200, nil)
			response.Body = nativeBrokenBody{}
			return response, nil
		}
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	}))
	if _, err := client.Documents.Get(t.Context(), document.Scope.documentScope(), nativeTestCollection, nativeTestDocument); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(hosts, []string{"gru.api.terrascale.tech", "fra.api.terrascale.tech"}) {
		t.Fatalf("stream failover: %v", hosts)
	}
}

func TestHostedRetryAfterPreservesDeadline(t *testing.T) {
	body, fixture := nativeDiscoveryFixture(t)
	var hosts []string
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "atlas.terrascale.tech" {
			return nativeDiscoveryResponse(200, body), nil
		}
		hosts = append(hosts, r.URL.Host)
		response := nativeDiscoveryResponse(503, nil)
		response.Header.Set("Retry-After", "60")
		return response, nil
	}))
	client.cfg.RequestTimeout = 30 * time.Millisecond
	_, err := client.Documents.Get(t.Context(), fixture.Scope.documentScope(), nativeTestCollection, nativeTestDocument)
	if !errors.Is(err, context.DeadlineExceeded) || !reflect.DeepEqual(hosts, []string{"gru.api.terrascale.tech"}) {
		t.Fatalf("Retry-After deadline: %v %v", hosts, err)
	}
}

func TestHostedDeterministicErrorsDoNotFailover(t *testing.T) {
	for _, status := range []int{400, 401, 403, 409, 429} {
		t.Run(http.StatusText(status), func(t *testing.T) {
			body, fixture := nativeDiscoveryFixture(t)
			atlas := 0
			var hosts []string
			client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
				if r.URL.Host == "atlas.terrascale.tech" {
					atlas++
					return nativeDiscoveryResponse(200, body), nil
				}
				hosts = append(hosts, r.URL.Host)
				return nativeDiscoveryResponse(status, []byte(`{"error":{"code":"denied"}}`)), nil
			}))
			for range 2 {
				_, err := client.Documents.Get(t.Context(), fixture.Scope.documentScope(), nativeTestCollection, nativeTestDocument)
				var apiError *APIError
				if !errors.As(err, &apiError) || apiError.Status != status {
					t.Fatalf("status lost: %v", err)
				}
			}
			if atlas != 1 || !reflect.DeepEqual(hosts, []string{"gru.api.terrascale.tech", "gru.api.terrascale.tech"}) {
				t.Fatalf("deterministic failure changed pin: %d %v", atlas, hosts)
			}
		})
	}
}

func TestHostedExplicitProxyDisablesDiscovery(t *testing.T) {
	var hosts []string
	client, err := NewClient(Config{DataBaseURL: nativeProxyOrigin, DataAccessToken: "custom-key", HTTPClient: &http.Client{Transport: pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		hosts = append(hosts, r.URL.Host)
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	})}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.Documents.Get(t.Context(), nativeTestScope, nativeTestCollection, nativeTestDocument); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(hosts, []string{"api.terrascale.tech"}) {
		t.Fatalf("explicit proxy discovery: %v", hosts)
	}
}

func TestHostedFallbackKeepsNativeCadenceAcrossKeyRotation(t *testing.T) {
	document := nativeRankedFixture(t)
	document.RefreshSeconds = 2
	now := time.Unix(document.PublishedAt, 0)
	atlas := 0
	client := nativeDiscoveryClient(t, pinningRoundTripperFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Host == "atlas.terrascale.tech" {
			atlas++
			if atlas == 1 {
				body, _ := json.Marshal(document)
				return nativeDiscoveryResponse(200, body), nil
			}
			return nativeDiscoveryResponse(404, nil), nil
		}
		return nativeDiscoveryResponse(200, []byte(nativeAbsentPoint)), nil
	}))
	client.nativeRouter.now = func() time.Time { return now }
	read := func() {
		t.Helper()
		if _, err := client.Documents.Get(t.Context(), document.Scope.documentScope(), nativeTestCollection, nativeTestDocument); err != nil {
			t.Fatal(err)
		}
	}
	read()
	client.cfg.DataAccessToken = nativeDiscoveryTestKey(6)
	read()
	now = now.Add(1999 * time.Millisecond)
	read()
	if atlas != 2 {
		t.Fatalf("fallback refreshed early: %d", atlas)
	}
	now = now.Add(time.Millisecond)
	read()
	if atlas != 3 {
		t.Fatalf("key rotation lost native refresh cadence: %d", atlas)
	}
	now = now.Add(2 * time.Second)
	read()
	if atlas != 4 {
		t.Fatalf("repeated fallback lost native refresh cadence: %d", atlas)
	}
}
