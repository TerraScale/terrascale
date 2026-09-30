package sdk

import (
	"encoding/json"
	"errors"
	"math"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

const (
	nativeTestCollection UUID = "33333333-3333-3333-3333-333333333333"
	nativeTestDocument   UUID = "44444444-4444-4444-4444-444444444444"
	nativeAbsentPoint         = `{"version":1,"state":"absent","state_version":null,"context":[],"siblings":[]}`
)

var nativeTestScope = DocumentScope{Tenant: "11111111-1111-1111-1111-111111111111", Database: "22222222-2222-2222-2222-222222222222", Shard: DecimalU64(math.MaxUint64)}
var nativeTestID = TransactionID{ExpiresAtUnixMS: DecimalU64(math.MaxUint64), Nonce: Hex16{1}}

func nativeTestWrite() DocumentWrite {
	return DocumentWrite{Collection: nativeTestCollection, Document: nativeTestDocument, SchemaGeneration: 1, SchemaDigest: Hex32{2}, Condition: DocumentCondition{Kind: ConditionAbsent}, Observed: []Observed{}, Value: WriteValue{Kind: "live", Fields: []DocumentField{{ID: 1, Value: I64Value(math.MinInt64)}}}}
}
func nativeTestReceipt(request CommitRequest) CommitResponse {
	return CommitResponse{Version: 1, TransactionID: request.TransactionID, Authority: request.Authority, EffectDigest: request.EffectDigest, Event: DocumentEvent{Database: nativeTestScope.Database, Shard: nativeTestScope.Shard, Replica: nativeTestCollection, Incarnation: Hex16{3}, Sequence: 1}, CommitSequence: 1, Durability: DurabilityLocal}
}
func nativeTestLivePoint() DocumentPoint {
	version := Hex32{1}
	event := DocumentEvent{Database: nativeTestScope.Database, Shard: nativeTestScope.Shard, Replica: nativeTestCollection, Incarnation: Hex16{1}, Sequence: 1}
	return DocumentPoint{Version: 1, State: PointValue, StateVersion: &version, Context: []Observed{{Replica: event.Replica, Incarnation: event.Incarnation, Sequence: 1}}, Siblings: []DocumentSibling{{Event: event, SchemaGeneration: 1, SchemaDigest: Hex32{2}, Kind: "live", Fields: []DocumentField{{ID: 1, Value: I64Value(math.MinInt64)}}}}}
}
func nativeTestClient(t *testing.T, handler http.HandlerFunc) (*Client, *httptest.Server) {
	t.Helper()
	server := httptest.NewServer(handler)
	t.Cleanup(server.Close)
	client, err := NewClient(Config{DataBaseURL: server.URL, DataAccessToken: "native-key", MaxRetryAttempts: 8})
	if err != nil {
		t.Fatal(err)
	}
	return client, server
}

func TestNativeDocumentPointRouteAndStrictState(t *testing.T) {
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "GET" || r.URL.Path != nativeTestScope.path()+"/collections/"+string(nativeTestCollection)+"/documents/"+string(nativeTestDocument) || r.URL.RawQuery != "" {
			t.Errorf("wrong native route: %s %s", r.Method, r.URL)
		}
		if r.Header.Get("Authorization") != "Bearer native-key" || r.Header.Get("If-Match") != "" || r.Header.Get("X-TerraScale-Session") != "" {
			t.Errorf("wrong native headers: %v", r.Header)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(nativeAbsentPoint))
	})
	point, err := client.Documents.Get(t.Context(), nativeTestScope, nativeTestCollection, nativeTestDocument)
	if err != nil || point.State != PointAbsent || point.StateVersion != nil {
		t.Fatalf("absent state lost: %+v %v", point, err)
	}
}

func TestNativePointPreservesDeletedAndConflict(t *testing.T) {
	first := DocumentEvent{Database: nativeTestScope.Database, Shard: nativeTestScope.Shard, Replica: nativeTestCollection, Incarnation: Hex16{1}, Sequence: 2}
	second := first
	second.Replica = nativeTestDocument
	context := []Observed{{Replica: first.Replica, Incarnation: first.Incarnation, Sequence: 2}, {Replica: second.Replica, Incarnation: second.Incarnation, Sequence: 2}}
	live := DocumentSibling{Event: first, SchemaGeneration: 1, SchemaDigest: Hex32{1}, Kind: "live", Fields: []DocumentField{{ID: 1, Value: BoolValue(true)}}}
	tombstone := DocumentSibling{Event: second, SchemaGeneration: 1, SchemaDigest: Hex32{1}, Kind: "tombstone"}
	conflict := DocumentPoint{Version: 1, State: PointConflict, Context: context, Siblings: []DocumentSibling{live, tombstone}}
	if err := conflict.validate(nativeTestScope); err != nil {
		t.Fatal(err)
	}
	data, err := json.Marshal(conflict)
	if err != nil {
		t.Fatal(err)
	}
	var decoded DocumentPoint
	if err := decodeNativeJSON(data, &decoded); err != nil {
		t.Fatal(err)
	}
	if len(decoded.Siblings) != 2 || decoded.Siblings[1].Kind != "tombstone" || decoded.StateVersion != nil {
		t.Fatal("conflict flattened")
	}
	version := Hex32{9}
	deleted := DocumentPoint{Version: 1, State: PointDeleted, StateVersion: &version, Context: context, Siblings: []DocumentSibling{tombstone}}
	if err := deleted.validate(nativeTestScope); err != nil {
		t.Fatal(err)
	}
	deleted.StateVersion = nil
	if err := deleted.validate(nativeTestScope); err == nil {
		t.Fatal("deleted state without CAS version accepted")
	}
}

func TestNativeCommitAndStatusIdentityAreFrozen(t *testing.T) {
	request, err := NewCommitRequest(nativeTestScope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{nativeTestWrite()})
	if err != nil {
		t.Fatal(err)
	}
	expected := nativeTestReceipt(request)
	calls := 0
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.Header().Set("Content-Type", "application/json")
		if r.URL.Path == nativeTestScope.path()+"/transactions" {
			var sent CommitRequest
			if err := json.NewDecoder(r.Body).Decode(&sent); err != nil {
				t.Error(err)
			}
			if sent.EffectDigest != expected.EffectDigest || r.Header.Get("Idempotency-Key") != "" {
				t.Error("wrong transaction effects")
			}
			request.Writes[0].Condition.Kind = ConditionNone // Caller mutation after dispatch cannot change expected identity.
			_ = json.NewEncoder(w).Encode(expected)
		} else if strings.HasSuffix(r.URL.Path, "/transactions/status") {
			_ = json.NewEncoder(w).Encode(TransactionStatusResponse{Version: 1, State: TransactionCommitted, TransactionID: expected.TransactionID, Authority: expected.Authority, EffectDigest: expected.EffectDigest, Outcome: &TransactionOutcome{Receipt: &expected}})
		} else {
			t.Errorf("wrong route %s", r.URL)
		}
	})
	receipt, err := client.Documents.Commit(t.Context(), nativeTestScope, request)
	if err != nil || receipt.Durability != DurabilityLocal {
		t.Fatalf("receipt invalid: %+v %v", receipt, err)
	}
	status, err := client.Documents.TransactionStatus(t.Context(), nativeTestScope, request.StatusRequest())
	if err != nil || !status.Definitive() || status.Outcome.Receipt.EffectDigest != expected.EffectDigest {
		t.Fatalf("status invalid: %+v %v", status, err)
	}
	if calls != 2 {
		t.Fatalf("unexpected replay %d", calls)
	}
}

func TestNativeMalformedCommitReceiptIsUncertain(t *testing.T) {
	request, err := NewCommitRequest(nativeTestScope, nativeTestID, MutationAuthority{Mode: AuthorityLeaderless}, []DocumentWrite{nativeTestWrite()})
	if err != nil {
		t.Fatal(err)
	}
	for _, change := range []func(*CommitResponse){func(r *CommitResponse) { r.EffectDigest[0] ^= 1 }, func(r *CommitResponse) { r.Event.Shard-- }, func(r *CommitResponse) { r.Durability = "global_durable" }} {
		receipt := nativeTestReceipt(request)
		change(&receipt)
		client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
			w.Header().Set("Content-Type", "application/json")
			_ = json.NewEncoder(w).Encode(receipt)
		})
		_, err := client.Documents.Commit(t.Context(), nativeTestScope, request)
		var commitErr *CommitError
		if !errors.As(err, &commitErr) || !commitErr.Uncertain {
			t.Fatalf("malformed receipt considered definitive: %v", err)
		}
	}
}

func TestNativeQueryExactTypedRouteAndResultOrder(t *testing.T) {
	value := I64Value(math.MinInt64)
	client, _ := nativeTestClient(t, func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "POST" || r.URL.Path != nativeTestScope.path()+"/collections/"+string(nativeTestCollection)+"/documents/query" {
			t.Error("wrong native query route")
		}
		var request QueryRequest
		if err := json.NewDecoder(r.Body).Decode(&request); err != nil {
			t.Error(err)
		}
		if got, ok := request.Predicate.Value.Int64(); !ok || got != math.MinInt64 {
			t.Error("query integer lost")
		}
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(QueryResponse{Version: 1, Rows: []DocumentRow{{Document: nativeTestDocument, Point: nativeTestLivePoint()}}, NextCursor: "AQ"})
	})
	result, err := client.Documents.Query(t.Context(), nativeTestScope, nativeTestCollection, QueryRequest{Version: 1, Limit: 1, Predicate: QueryPredicate{Operation: QueryEqual, Path: []uint32{1}, Value: &value}})
	if err != nil || len(result.Rows) != 1 || result.NextCursor != "AQ" {
		t.Fatalf("native query failed: %+v %v", result, err)
	}
	rows := []DocumentRow{{Document: nativeTestDocument, Point: nativeTestLivePoint()}, {Document: nativeTestCollection, Point: nativeTestLivePoint()}}
	if err := validateRows(nativeTestScope, rows); err == nil {
		t.Fatal("unordered query rows accepted")
	}
	rows[0].Point = DocumentPoint{Version: 1, State: PointAbsent, Context: []Observed{}, Siblings: []DocumentSibling{}}
	if err := validateRows(nativeTestScope, rows[:1]); err == nil {
		t.Fatal("absent query entry accepted")
	}
}
