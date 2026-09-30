package sdk_test

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"math"
	"net/http"
	"os"
	"slices"
	"strings"
	"testing"
	"time"

	sdk "terrascale.tech/sdk"
)

type routingContractTransport func(*http.Request) (*http.Response, error)

func (transport routingContractTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	return transport(request)
}

var routingContractScope = sdk.DocumentScope{
	Tenant: "10000000-0000-0000-0000-000000000001", Database: "20000000-0000-0000-0000-000000000001", Shard: sdk.DecimalU64(math.MaxUint64),
}

const (
	routingContractCollection sdk.UUID = "33333333-3333-3333-3333-333333333333"
	routingContractDocument   sdk.UUID = "44444444-4444-4444-4444-444444444444"
)

func routingContractKey() string {
	return "tbk1." + base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{5}, 16)) + "." + base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{6}, 32))
}

func routingContractResponse(status int, body []byte) *http.Response {
	return &http.Response{StatusCode: status, Header: http.Header{"Content-Type": {"application/json"}}, Body: io.NopCloser(bytes.NewReader(body))}
}

// These finite caller fixtures derive from the retained receiver capture. They
// update publication time without claiming a current hosted Atlas observation.
func routingContractDiscovery(t *testing.T) map[string]any {
	t.Helper()
	body, err := os.ReadFile("contracts/native/atlas/native-discovery-success.json")
	if err != nil {
		t.Fatal(err)
	}
	var document map[string]any
	if err := json.Unmarshal(body, &document); err != nil {
		t.Fatal(err)
	}
	now := time.Now().Unix()
	document["published_at"], document["expires_at"] = now-1, now+8
	return document
}

func routingContractJSON(t *testing.T, value any) []byte {
	t.Helper()
	body, err := json.Marshal(value)
	if err != nil {
		t.Fatal(err)
	}
	return body
}

func routingContractCommit(t *testing.T) sdk.CommitRequest {
	t.Helper()
	request, err := sdk.NewCommitRequest(routingContractScope,
		sdk.TransactionID{ExpiresAtUnixMS: sdk.DecimalU64(math.MaxUint64), Nonce: sdk.Hex16{1}},
		sdk.MutationAuthority{Mode: sdk.AuthorityLeaderless}, []sdk.DocumentWrite{{
			Collection: routingContractCollection, Document: routingContractDocument, SchemaGeneration: 1, SchemaDigest: sdk.Hex32{2},
			Condition: sdk.DocumentCondition{Kind: sdk.ConditionNone}, Observed: []sdk.Observed{},
			Value: sdk.WriteValue{Kind: "live", Fields: []sdk.DocumentField{{ID: 1, Value: sdk.I64Value(math.MinInt64)}}},
		}})
	if err != nil {
		t.Fatal(err)
	}
	return request
}

type routingContractOperation struct {
	name string
	call func(context.Context, *sdk.Client) error
}

func routingContractOperations(t *testing.T) []routingContractOperation {
	t.Helper()
	commit := routingContractCommit(t)
	predicate := sdk.QueryPredicate{Operation: sdk.QueryIsMissing, Path: []uint32{1}}
	return []routingContractOperation{
		{"read", func(ctx context.Context, client *sdk.Client) error {
			_, err := client.Documents.Get(ctx, routingContractScope, routingContractCollection, routingContractDocument)
			return err
		}},
		{"query", func(ctx context.Context, client *sdk.Client) error {
			_, err := client.Documents.Query(ctx, routingContractScope, routingContractCollection, sdk.QueryRequest{Version: 1, Predicate: predicate, Limit: 1})
			return err
		}},
		{"snapshot", func(ctx context.Context, client *sdk.Client) error {
			_, err := client.Documents.SubscriptionSnapshot(ctx, routingContractScope, routingContractCollection, sdk.SubscriptionSnapshotRequest{Version: 1, Phase: "snapshot", Predicate: predicate, Limit: 1})
			return err
		}},
		{"poll", func(ctx context.Context, client *sdk.Client) error {
			_, err := client.Documents.SubscriptionPoll(ctx, routingContractScope, routingContractCollection, sdk.SubscriptionPollRequest{Version: 1, Phase: "poll", Predicate: predicate, PollCursor: "AQ"})
			return err
		}},
		{"status", func(ctx context.Context, client *sdk.Client) error {
			_, err := client.Documents.TransactionStatus(ctx, routingContractScope, commit.StatusRequest())
			return err
		}},
		{"commit", func(ctx context.Context, client *sdk.Client) error {
			operation, err := client.Documents.PrepareTransaction(routingContractScope, commit)
			if err != nil {
				return err
			}
			_, err = operation.Commit(ctx)
			return err
		}},
	}
}

func TestNativePublicClientDiscoveryFailureStopsDispatch(t *testing.T) {
	forbidden, err := os.ReadFile("contracts/native/atlas/native-discovery-forbidden.json")
	if err != nil {
		t.Fatal(err)
	}
	for _, failure := range []struct {
		name   string
		status int
		fetch  func(*testing.T, *http.Request) (*http.Response, error)
	}{
		{"forbidden", 403, func(*testing.T, *http.Request) (*http.Response, error) {
			return routingContractResponse(403, forbidden), nil
		}},
		{"not found", 404, func(*testing.T, *http.Request) (*http.Response, error) {
			return routingContractResponse(404, []byte(`{"error":{"code":"discovery_unavailable"}}`)), nil
		}},
		{"unavailable", 503, func(*testing.T, *http.Request) (*http.Response, error) {
			return routingContractResponse(503, []byte(`{"error":{"code":"discovery_unavailable"}}`)), nil
		}},
		{"network", 0, func(*testing.T, *http.Request) (*http.Response, error) { return nil, errors.New("Atlas unavailable") }},
		{"timeout", 0, func(_ *testing.T, request *http.Request) (*http.Response, error) {
			<-request.Context().Done()
			return nil, request.Context().Err()
		}},
		{"catalog", 0, func(*testing.T, *http.Request) (*http.Response, error) {
			return routingContractResponse(200, []byte(`{"regions":[{"code":"GRU","available":true}],"hosts":{"GRU":"https://gru.terrascale.tech"}}`)), nil
		}},
		{"API alias", 0, func(t *testing.T, _ *http.Request) (*http.Response, error) {
			document := routingContractDiscovery(t)
			document["endpoints"].([]any)[0].(map[string]any)["url"] = "https://gru.api.terrascale.tech"
			return routingContractResponse(200, routingContractJSON(t, document)), nil
		}},
		{"no eligible capability", 0, func(t *testing.T, _ *http.Request) (*http.Response, error) {
			document := routingContractDiscovery(t)
			capabilities := document["endpoints"].([]any)[0].(map[string]any)["capabilities"].(map[string]any)
			for _, capability := range []string{"read", "query", "subscriptions", "write"} {
				capabilities[capability] = false
			}
			return routingContractResponse(200, routingContractJSON(t, document)), nil
		}},
	} {
		for _, operation := range routingContractOperations(t) {
			t.Run(failure.name+"/"+operation.name, func(t *testing.T) {
				atlasCalls, dataCalls := 0, 0
				client, err := sdk.NewClient(sdk.Config{
					DataAccessToken: routingContractKey(), PreferredRegion: sdk.RegionFRA,
					APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: "https://catalog.example.test", RequestTimeout: 20 * time.Millisecond,
					HTTPClient: &http.Client{Transport: routingContractTransport(func(request *http.Request) (*http.Response, error) {
						if request.URL.Host != "atlas.terrascale.tech" {
							dataCalls++
							return nil, errors.New("unexpected data dispatch")
						}
						atlasCalls++
						if request.Header.Get("Authorization") != "Bearer "+routingContractKey() || request.Header.Get("X-TerraScale-Preferred-Region") != "" {
							t.Fatal("discovery changed credentials or fabricated affinity")
						}
						return failure.fetch(t, request)
					})},
				})
				if err != nil {
					t.Fatal(err)
				}
				err = operation.call(t.Context(), client)
				var routingError *sdk.RoutingError
				if !errors.As(err, &routingError) || atlasCalls != 1 || dataCalls != 0 {
					t.Fatalf("failure=%v, Atlas calls=%d, data calls=%d", err, atlasCalls, dataCalls)
				}
				if failure.status != 0 {
					var apiError *sdk.APIError
					if !errors.As(err, &apiError) || apiError.Status != failure.status {
						t.Fatalf("actual Atlas status lost: %v", err)
					}
					if failure.status == 403 && apiError.Code != "discovery_forbidden" {
						t.Fatalf("actual Atlas denial code lost: %v", err)
					}
				}
				if failure.name == "timeout" && !errors.Is(err, context.DeadlineExceeded) {
					t.Fatalf("discovery deadline lost: %v", err)
				}
			})
		}
	}
}

func TestNativePublicClientSelectsClosestEligibleForEachOperation(t *testing.T) {
	document := routingContractDiscovery(t)
	first := document["endpoints"].([]any)[0].(map[string]any)
	endpoints := make([]any, 0, 3)
	for i, region := range []string{"GRU", "FRA", "SFO"} {
		var endpoint map[string]any
		if err := json.Unmarshal(routingContractJSON(t, first), &endpoint); err != nil {
			t.Fatal(err)
		}
		endpoint["region"], endpoint["url"], endpoint["priority"] = region, "https://"+strings.ToLower(region)+".terrascale.tech", i+1
		capabilities := endpoint["capabilities"].(map[string]any)
		capabilities["query"], capabilities["subscriptions"], capabilities["write"] = i >= 1, i >= 1, i == 2
		endpoints = append(endpoints, endpoint)
	}
	document["endpoints"] = endpoints
	commit := routingContractCommit(t)
	atlasCalls := 0
	var dispatched []string
	client, err := sdk.NewClient(sdk.Config{
		DataAccessToken: routingContractKey(), PreferredRegion: sdk.RegionSFO,
		APIBaseURL: "https://api.terrascale.tech", DiscoveryBaseURL: "https://catalog.example.test",
		HTTPClient: &http.Client{Transport: routingContractTransport(func(request *http.Request) (*http.Response, error) {
			if request.Header.Get("Authorization") != "Bearer "+routingContractKey() || request.Header.Get("X-TerraScale-Preferred-Region") != "" {
				t.Fatal("operation changed credentials or fabricated affinity")
			}
			if request.URL.Host == "atlas.terrascale.tech" {
				atlasCalls++
				return routingContractResponse(200, routingContractJSON(t, document)), nil
			}
			dispatched = append(dispatched, request.URL.Host)
			switch {
			case request.Method == http.MethodGet:
				return routingContractResponse(200, []byte(`{"version":1,"state":"absent","state_version":null,"context":[],"siblings":[]}`)), nil
			case strings.HasSuffix(request.URL.Path, "/query"):
				return routingContractResponse(200, []byte(`{"version":1,"rows":[]}`)), nil
			case strings.HasSuffix(request.URL.Path, "/subscriptions"):
				var phase struct {
					Phase string `json:"phase"`
				}
				if err := json.NewDecoder(request.Body).Decode(&phase); err != nil {
					t.Fatal(err)
				}
				if phase.Phase == "snapshot" {
					return routingContractResponse(200, []byte(`{"version":1,"phase":"snapshot","rows":[],"poll_cursor":"AQ"}`)), nil
				}
				return routingContractResponse(200, []byte(`{"version":1,"phase":"poll","groups":[],"poll_cursor":"Ag"}`)), nil
			case strings.HasSuffix(request.URL.Path, "/transactions/status"):
				return routingContractResponse(200, routingContractJSON(t, sdk.TransactionStatusResponse{Version: 1, State: sdk.TransactionUnknown, TransactionID: commit.TransactionID, Authority: commit.Authority, EffectDigest: commit.EffectDigest})), nil
			case strings.HasSuffix(request.URL.Path, "/transactions"):
				return routingContractResponse(200, routingContractJSON(t, sdk.CommitResponse{
					Version: 1, TransactionID: commit.TransactionID, Authority: commit.Authority, EffectDigest: commit.EffectDigest,
					Event:          sdk.DocumentEvent{Database: routingContractScope.Database, Shard: routingContractScope.Shard, Replica: routingContractCollection, Incarnation: sdk.Hex16{3}, Sequence: 1},
					CommitSequence: 1, Durability: sdk.DurabilityLocal,
				})), nil
			default:
				t.Fatalf("unexpected native path: %s", request.URL)
				return nil, errors.New("unexpected native path")
			}
		})},
	})
	if err != nil {
		t.Fatal(err)
	}
	operations := routingContractOperations(t)
	// Returning to READ proves that the prior WRITE choice did not pin every
	// operation to the farther endpoint in this same authenticated lease.
	operations = append(operations, operations[0])
	for _, operation := range operations {
		if err := operation.call(t.Context(), client); err != nil {
			t.Fatalf("%s: %v", operation.name, err)
		}
	}
	want := []string{"gru.terrascale.tech", "fra.terrascale.tech", "fra.terrascale.tech", "fra.terrascale.tech", "sfo.terrascale.tech", "sfo.terrascale.tech", "gru.terrascale.tech"}
	if atlasCalls != 1 || !slices.Equal(dispatched, want) {
		t.Fatalf("Atlas calls=%d, dispatched=%v, want=%v", atlasCalls, dispatched, want)
	}
}
