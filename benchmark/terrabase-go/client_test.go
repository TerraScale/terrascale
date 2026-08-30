package terrabase

import (
	"context"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"io"
	"math"
	"net"
	"strings"
	"testing"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/peer"
	"google.golang.org/grpc/test/bufconn"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

// startFake serves the fake engine over one in-memory transport and
// returns a bound client. The server stops with the test.
func startFake(t *testing.T) (*Client, *fakeTBEP) {
	t.Helper()
	fake := newFakeTBEP()
	listener := bufconn.Listen(1 << 20)
	server := grpc.NewServer()
	fake.register(server)
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(server.Stop)
	client := dialBufconn(t, listener, WithInsecure())
	return client, fake
}

// dialBufconn dials one client onto the fake's in-memory listener.
func dialBufconn(t *testing.T, listener *bufconn.Listener, options ...Option) *Client {
	t.Helper()
	dialer := func(ctx context.Context, _ string) (net.Conn, error) { return listener.DialContext(ctx) }
	options = append(options, WithContextDialer(dialer), WithAuthority("localhost"))
	client, err := Dial("passthrough:///bufnet", options...)
	if err != nil {
		t.Fatalf("the client could not be dialed: %v", err)
	}
	t.Cleanup(func() { _ = client.Close() })
	return client
}

func mustBind(t *testing.T, client *Client) Binding {
	t.Helper()
	binding, err := client.Bind(context.Background(), "orders-api", "tenant-a")
	if err != nil {
		t.Fatalf("the channel could not be bound: %v", err)
	}
	return binding
}

// TestBindRequiresIdentity checks the local fail-closed identity rule:
// every RPC except Bind refuses to leave without it.
func TestBindRequiresIdentity(t *testing.T) {
	client, fake := startFake(t)
	if _, _, err := client.DocumentRead(context.Background(), "books", "ddia", ReadLevelDefault); !errors.Is(err, ErrUnboundChannel) {
		t.Fatalf("an unbound read must answer ErrUnboundChannel, got %v", err)
	}
	binding := mustBind(t, client)
	ctx := context.Background()
	if binding.Tenant != "tenant-a" || binding.ServingInstance != 7 {
		t.Fatalf("the bind answer is wrong: %+v", binding)
	}
	if _, _, err := client.DocumentRead(ctx, "books", "ddia", ReadLevelDefault); err != nil {
		t.Fatalf("the bound read failed: %v", err)
	}
	if fake.lastTenant() != "tenant-a" {
		t.Fatalf("the engine did not see the tenant, got %q", fake.lastTenant())
	}
	spoofed := metadata.NewOutgoingContext(ctx, metadata.Pairs(tenantMetadata, "attacker"))
	if _, _, err := client.DocumentRead(spoofed, "books", "ddia", ReadLevelDefault); err != nil {
		t.Fatalf("a caller metadata value must not break a bound read: %v", err)
	}
	if fake.lastTenant() != "tenant-a" {
		t.Fatalf("the caller must not override the bound tenant, got %q", fake.lastTenant())
	}
}

// TestBindValidation checks the bind bounds.
func TestBindValidation(t *testing.T) {
	client, _ := startFake(t)
	if _, err := client.Bind(context.Background(), "", "tenant-a"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an empty principal must be refused locally, got %v", err)
	}
	if _, err := client.Bind(context.Background(), strings.Repeat("p", 129), "tenant-a"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an oversized principal must be refused locally, got %v", err)
	}
	if _, err := client.Bind(context.Background(), "orders-api", strings.Repeat("t", 65)); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an oversized tenant must be refused locally, got %v", err)
	}
}

func TestBindRejectsTenantChange(t *testing.T) {
	client, fake := startFake(t)
	fake.bindTenant = "other-tenant"
	if _, err := client.Bind(context.Background(), "orders-api", "tenant-a"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("a bind response changing tenants must be refused, got %v", err)
	}
	if _, _, err := client.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault); !errors.Is(err, ErrUnboundChannel) {
		t.Fatalf("a refused bind must not establish identity, got %v", err)
	}
}

// TestDocumentLifecycle walks write, read-back, query, and delete
// against the in-memory fake.
func TestDocumentLifecycle(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	ctx := context.Background()

	outcome, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "ddia", Body: map[string]Value{
			"title": "Designing Data-Intensive Applications",
			"pages": 616,
			"tags":  []Value{"distributed", "storage"},
		}},
		ExpectAbsent(), AckRegionalQuorum, "op-write-1")
	if err != nil {
		t.Fatalf("the write failed: %v", err)
	}
	receipt, committed := outcome.Committed()
	if !committed {
		t.Fatalf("the write must commit, got %+v", outcome)
	}
	if receipt.Version != 1 || receipt.Sequence == 0 || receipt.OperationID != "op-write-1" {
		t.Fatalf("the write receipt is wrong: %+v", receipt)
	}
	if receipt.Ack != AckRegionalQuorum || receipt.Backup != BackupNone {
		t.Fatalf("the write acknowledgement is wrong: %+v", receipt)
	}

	document, found, err := client.DocumentRead(ctx, "books", "ddia", ReadLevelGlobalQuorum)
	if err != nil || !found {
		t.Fatalf("the read failed: found=%v err=%v", found, err)
	}
	if document.Version != 1 || document.Position == 0 {
		t.Fatalf("the read answer is wrong: %+v", document)
	}
	body, ok := document.Body.(map[string]Value)
	if !ok {
		t.Fatalf("the body must decode as a map, got %T", document.Body)
	}
	if body["title"] != "Designing Data-Intensive Applications" || body["pages"] != int64(616) {
		t.Fatalf("the body round-trip is wrong: %+v", body)
	}
	if tags, ok := body["tags"].([]Value); !ok || len(tags) != 2 || tags[1] != "storage" {
		t.Fatalf("the list round-trip is wrong: %#v", body["tags"])
	}

	if _, found, err := client.DocumentRead(ctx, "books", "missing", ReadLevelDefault); err != nil || found {
		t.Fatalf("a missing document must read as not found: found=%v err=%v", found, err)
	}

	result, err := client.QueryExecute(ctx, 1, "collection:books", 10, ReadLevelDefault)
	if err != nil {
		t.Fatalf("the query failed: %v", err)
	}
	if result.Count != 1 || len(result.Documents) != 1 || result.Truncated || result.Documents[0].ID != "ddia" {
		t.Fatalf("the query answer is wrong: %+v", result)
	}
	if _, err := client.QueryCancel(ctx, 1); err != nil {
		t.Fatalf("the cancel failed: %v", err)
	}
	if outcome, err := client.QueryCancel(ctx, 404); err != nil || outcome != UnknownQuery {
		t.Fatalf("the cancel of an unknown query is wrong: outcome=%v err=%v", outcome, err)
	}

	delete, err := client.DocumentDelete(ctx, "books", "ddia", ExpectExact(1), AckDefault, "op-delete-1")
	if err != nil {
		t.Fatalf("the delete failed: %v", err)
	}
	if _, committed := delete.Committed(); !committed {
		t.Fatalf("the delete must commit, got %+v", delete)
	}
	if _, found, _ := client.DocumentRead(ctx, "books", "ddia", ReadLevelDefault); found {
		t.Fatal("the deleted document must not read back")
	}
}

// TestWriteOutcomes checks the conflict and refusal mappings: engine
// outcomes, not transport errors.
func TestWriteOutcomes(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	ctx := context.Background()

	race, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "race", ID: "doc", Body: map[string]Value{"n": 1}},
		ExpectExact(9), AckDefault, "op-race")
	if err != nil {
		t.Fatalf("the racing write failed: %v", err)
	}
	if !race.Conflict() {
		t.Fatalf("the racing write must report the lost race, got %+v", race)
	}
	if _, committed := race.Committed(); committed {
		t.Fatal("a lost race must not carry a receipt")
	}

	refused, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "refuse", ID: "doc", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "op-refuse")
	if err != nil {
		t.Fatalf("the refused write failed: %v", err)
	}
	reason, wasRefused := refused.Refused()
	if !wasRefused || reason != WriteSchemaRefused {
		t.Fatalf("the refused write must name the reason, got refused=%v reason=%v", wasRefused, reason)
	}
	if reason.String() != "schema_refused" {
		t.Fatalf("the refusal reason name is wrong: %q", reason.String())
	}
}

// TestBatch covers the atomic batch path.
func TestBatch(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	outcome, err := client.DocumentBatch(context.Background(), []BatchItem{
		{Document: DocumentPut{Collection: "books", ID: "a", Body: map[string]Value{"i": 1}}, Expected: ExpectAbsent()},
		{Document: DocumentPut{Collection: "books", ID: "b", Body: map[string]Value{"i": 2}}, Expected: ExpectAbsent(), OperationID: "op-b"},
	}, AckDefault)
	if err != nil {
		t.Fatalf("the batch failed: %v", err)
	}
	receipt, committed := outcome.Committed()
	if !committed || receipt.Version != 1 {
		t.Fatalf("the batch must commit, got %+v", outcome)
	}
	result, err := client.QueryExecute(context.Background(), 2, "collection:books", 1, ReadLevelDefault)
	if err != nil {
		t.Fatalf("the query failed: %v", err)
	}
	if !result.Truncated || result.Count != 1 {
		t.Fatalf("the bounded query must truncate, got %+v", result)
	}
}

// TestErrorMapping checks the engine-shaped failure mapping into the
// typed error model and the sentinel errors.
func TestErrorMapping(t *testing.T) {
	client, fake := startFake(t)
	mustBind(t, client)
	overloaded := v1.ErrorCode_ERROR_CODE_OVERLOADED
	fake.failCode = &overloaded
	_, err := client.DocumentWrite(context.Background(),
		DocumentPut{Collection: "books", ID: "doc", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "op-1")
	if !errors.Is(err, ErrOverloaded) {
		t.Fatalf("an overloaded write must answer ErrOverloaded, got %v", err)
	}
	typed := new(Error)
	if !errors.As(err, &typed) {
		t.Fatalf("the failure must unwrap into *terrabase.Error, got %T", err)
	}
	if typed.Code != CodeOverloaded || typed.Detail == "" || typed.GRPCCode().String() != "ResourceExhausted" {
		t.Fatalf("the typed failure is wrong: %+v", typed)
	}
	if !strings.Contains(typed.Error(), "overloaded") {
		t.Fatalf("the failure text must name the code: %q", typed.Error())
	}

	fake.failCode = nil
	fake.unregistered = true
	_, err = client.DocumentWrite(context.Background(),
		DocumentPut{Collection: "books", ID: "doc", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "op-2")
	if errors.Is(err, ErrEngineFailure) {
		t.Fatalf("an unknown engine code must not match a sentinel, got %v", err)
	}
	if errors.As(err, &typed) && typed.Code != CodeUnspecified {
		t.Fatalf("an unknown engine code must stay unspecified, got %v", typed.Code)
	}
}

// TestEveryCodeMapsToItsSentinel walks the whole closed vocabulary.
func TestEveryCodeMapsToItsSentinel(t *testing.T) {
	pairs := []struct {
		code     v1.ErrorCode
		sentinel error
	}{
		{v1.ErrorCode_ERROR_CODE_UNSUPPORTED_VERSION, ErrUnsupportedVersion},
		{v1.ErrorCode_ERROR_CODE_MALFORMED_REQUEST, ErrMalformedRequest},
		{v1.ErrorCode_ERROR_CODE_UNKNOWN_OPERATION, ErrUnknownOperation},
		{v1.ErrorCode_ERROR_CODE_DIGEST_MISMATCH, ErrDigestMismatch},
		{v1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING, ErrIdempotencyPairing},
		{v1.ErrorCode_ERROR_CODE_UNBOUND_CHANNEL, ErrUnboundChannel},
		{v1.ErrorCode_ERROR_CODE_ALREADY_BOUND, ErrAlreadyBound},
		{v1.ErrorCode_ERROR_CODE_INVALID_REQUEST, ErrInvalidRequest},
		{v1.ErrorCode_ERROR_CODE_OPERATION_REFUSED, ErrOperationRefused},
		{v1.ErrorCode_ERROR_CODE_ENGINE_FAILURE, ErrEngineFailure},
		{v1.ErrorCode_ERROR_CODE_SESSION_REFUSED, ErrSessionRefused},
		{v1.ErrorCode_ERROR_CODE_SUBSCRIPTION_REFUSED, ErrSubscriptionRefused},
		{v1.ErrorCode_ERROR_CODE_CHECKPOINT_REFUSED, ErrCheckpointRefused},
		{v1.ErrorCode_ERROR_CODE_SHUTTING_DOWN, ErrShuttingDown},
		{v1.ErrorCode_ERROR_CODE_REQUEST_TOO_LARGE, ErrRequestTooLarge},
		{v1.ErrorCode_ERROR_CODE_OVERLOADED, ErrOverloaded},
		{v1.ErrorCode_ERROR_CODE_PROTOCOL_VERSION_MISMATCH, ErrProtocolVersionMismatch},
		{v1.ErrorCode_ERROR_CODE_BACKUP_CLUSTER_READ_ONLY, ErrBackupClusterReadOnly},
		{v1.ErrorCode_ERROR_CODE_BACKUP_CLUSTER_IMMUTABLE, ErrBackupClusterImmutable},
		{v1.ErrorCode_ERROR_CODE_COLD_TIER_UNAVAILABLE, ErrColdTierUnavailable},
		{v1.ErrorCode_ERROR_CODE_FRONTIER_UNREACHABLE, ErrFrontierUnreachable},
		{v1.ErrorCode_ERROR_CODE_USAGE_UNAVAILABLE, ErrUsageUnavailable},
		{v1.ErrorCode_ERROR_CODE_USAGE_CORRUPT, ErrUsageCorrupt},
	}
	for _, pair := range pairs {
		err := interpret(engineStatus(9, pair.code, "detail"))
		if !errors.Is(err, pair.sentinel) {
			t.Fatalf("engine code %v must map to %v, got %v", pair.code, pair.sentinel, err)
		}
		typed := new(Error)
		if !errors.As(err, &typed) || typed.Detail != "detail" {
			t.Fatalf("engine code %v must keep its detail, got %v", pair.code, err)
		}
	}
}

// TestTransportFailureStaysUntouched checks that transport-level
// failures pass through so context and code checks keep working.
func TestTransportFailureStaysUntouched(t *testing.T) {
	err := interpret(context.DeadlineExceeded)
	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("a context failure must pass through, got %v", err)
	}
	if interpret(nil) != nil {
		t.Fatal("nil must interpret to nil")
	}
}

// TestIdempotencyKeys checks the retry-identity conventions: explicit
// key, operation identity, and the random fallback.
func TestIdempotencyKeys(t *testing.T) {
	client, fake := startFake(t)
	mustBind(t, client)
	ctx := context.Background()
	write := func(collection string, options ...CallOption) {
		t.Helper()
		if _, err := client.DocumentWrite(ctx,
			DocumentPut{Collection: collection, ID: "doc", Body: map[string]Value{"n": 1}},
			ExpectAny(), AckDefault, "", options...); err != nil {
			t.Fatalf("the write failed: %v", err)
		}
	}
	write("books", WithIdempotencyKey("retry-key"))
	if key := fake.lastKey(); key != "retry-key" {
		t.Fatalf("the explicit key must ride the metadata, got %q", key)
	}
	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "doc2", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "op-derived"); err != nil {
		t.Fatalf("the write failed: %v", err)
	}
	if key := fake.lastKey(); key != "op-derived" {
		t.Fatalf("the operation id must become the retry key, got %q", key)
	}
	write("books")
	if key := fake.lastKey(); len(key) != 32 {
		t.Fatalf("a random key must be generated, got %q", key)
	}
	long := strings.Repeat("k", MaxIdempotencyBytes+1)
	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "doc", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "", WithIdempotencyKey(long)); !errors.Is(err, ErrIdempotencyPairing) {
		t.Fatalf("an oversized key must be refused, got %v", err)
	}
	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "doc", Body: map[string]Value{"n": 1}},
		ExpectAny(), AckDefault, "", WithIdempotencyKey("bad\nkey")); !errors.Is(err, ErrIdempotencyPairing) {
		t.Fatalf("a control-character key must be refused, got %v", err)
	}
}

// TestLocalValidation checks the mirrored fail-closed bounds.
func TestLocalValidation(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	ctx := context.Background()
	oversized := strings.Repeat("c", maxCollectionBytes+1)
	if _, _, err := client.DocumentRead(ctx, oversized, "doc", ReadLevelDefault); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an oversized collection must be refused, got %v", err)
	}
	if _, _, err := client.DocumentRead(ctx, "books", "", ReadLevelDefault); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an empty document id must be refused, got %v", err)
	}
	if _, err := client.QueryExecute(ctx, 1, "", 0, ReadLevelDefault); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("empty query text must be refused, got %v", err)
	}
	if _, err := client.QueryExecute(ctx, 1, strings.Repeat("q", maxQueryTextBytes+1), 0, ReadLevelDefault); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized query text must be refused, got %v", err)
	}
	if _, err := client.DocumentBatch(ctx, nil, AckDefault); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an empty batch must be refused, got %v", err)
	}
	tooMany := make([]BatchItem, maxBatchItems+1)
	if _, err := client.DocumentBatch(ctx, tooMany, AckDefault); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an oversized batch must be refused, got %v", err)
	}
	if _, err := client.FamilyRead(ctx, "not-a-family", "name", "codec", nil); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an unknown family must be refused, got %v", err)
	}
	tooManyCollections := make([]string, maxCollections+1)
	for index := range tooManyCollections {
		tooManyCollections[index] = "c"
	}
	if _, err := client.Subscribe(ctx, 1, tooManyCollections, FromLive()); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an oversized collection filter must be refused, got %v", err)
	}
	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "doc", Body: map[string]Value{"text": strings.Repeat("t", maxTextBytes+1)}},
		ExpectAny(), AckDefault, "op-1"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized value text must be refused, got %v", err)
	}
	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "doc", Body: map[string]Value{"": 1}},
		ExpectAny(), AckDefault, "op-1"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an empty field name must be refused, got %v", err)
	}
	if _, err := client.DocumentWrite(ctx,
		DocumentPut{Collection: "books", ID: "doc", Body: struct{}{}},
		ExpectAny(), AckDefault, "op-1"); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("an unknown value type must be refused, got %v", err)
	}
}

func TestHostileValuesAreRejectedOnBothBoundaries(t *testing.T) {
	if _, err := documentFrom(nil); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("a found document without a body must be rejected, got %v", err)
	}
	if _, err := valueToProto(math.NaN()); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("NaN must not be encoded, got %v", err)
	}
	if _, err := valueToProto(string([]byte{0xff})); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("invalid UTF-8 must not be encoded, got %v", err)
	}

	duplicate := &v1.Value{Kind: &v1.Value_Map{Map: &v1.ValueMap{Entries: []*v1.ValueEntry{
		{Name: "same", Value: &v1.Value{Kind: &v1.Value_Integer{Integer: 1}}},
		{Name: "same", Value: &v1.Value{Kind: &v1.Value_Integer{Integer: 2}}},
	}}}}
	if _, err := valueFromProto(duplicate); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("duplicate map names must be rejected, got %v", err)
	}
	tooMany := make([]*v1.Value, maxListItems+1)
	for index := range tooMany {
		tooMany[index] = &v1.Value{Kind: &v1.Value_Null{Null: v1.NullValue_NULL}}
	}
	if _, err := valueFromProto(&v1.Value{Kind: &v1.Value_List{List: &v1.ValueList{Items: tooMany}}}); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("oversized lists must be rejected, got %v", err)
	}
	nested := &v1.Value{Kind: &v1.Value_Null{Null: v1.NullValue_NULL}}
	for index := 0; index <= maxDetailIndent; index++ {
		nested = &v1.Value{Kind: &v1.Value_List{List: &v1.ValueList{Items: []*v1.Value{nested}}}}
	}
	if _, err := valueFromProto(nested); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("deeply nested values must be rejected, got %v", err)
	}
	if _, err := valueFromProto(&v1.Value{Kind: &v1.Value_Number{Number: math.Inf(1)}}); !errors.Is(err, ErrInvalidRequest) {
		t.Fatalf("infinite numbers must be rejected, got %v", err)
	}
}

// TestValueRoundTrip walks every value kind through the wire both
// ways.
func TestValueRoundTrip(t *testing.T) {
	original := map[string]Value{
		"null":    nil,
		"bool":    true,
		"integer": int64(-42),
		"number":  3.5,
		"text":    "hello",
		"list":    []Value{int64(1), "two", nil, []Value{int64(3)}},
		"map":     map[string]Value{"nested": false},
	}
	encoded, err := valueToProto(original)
	if err != nil {
		t.Fatalf("the value could not be encoded: %v", err)
	}
	decoded, err := valueFromProto(encoded)
	if err != nil {
		t.Fatalf("the value could not be decoded: %v", err)
	}
	body, ok := decoded.(map[string]Value)
	if !ok {
		t.Fatalf("the decoded value must be a map, got %T", decoded)
	}
	if body["null"] != nil || body["bool"] != true || body["integer"] != int64(-42) ||
		body["number"] != 3.5 || body["text"] != "hello" {
		t.Fatalf("the scalar round-trip is wrong: %+v", body)
	}
	list, ok := body["list"].([]Value)
	if !ok || len(list) != 4 || list[2] != nil {
		t.Fatalf("the list round-trip is wrong: %#v", body["list"])
	}
	inner, ok := list[3].([]Value)
	if !ok || inner[0] != int64(3) {
		t.Fatalf("the nested list round-trip is wrong: %#v", list[3])
	}
	nested, ok := body["map"].(map[string]Value)
	if !ok || nested["nested"] != false {
		t.Fatalf("the map round-trip is wrong: %#v", body["map"])
	}
	// Go integer and float kinds normalize into the closed set.
	for _, accepted := range []Value{int(1), int8(2), int16(3), int32(4), uint(5), uint8(6), uint16(7), uint32(8), uint64(9), float32(0.5)} {
		if _, err := valueToProto(accepted); err != nil {
			t.Fatalf("the value %v (%T) must be accepted: %v", accepted, accepted, err)
		}
	}
	if _, err := valueToProto(uint64(1 << 63)); err == nil {
		t.Fatal("an unsigned integer above the sint64 range must be refused")
	}
	if _, err := valueToProto(make([]Value, maxListItems+1)); err == nil {
		t.Fatal("an oversized list must be refused")
	}
	names := make(map[string]Value)
	for index := 0; index <= maxMapEntries; index++ {
		names[fmt.Sprintf("f%04d", index)] = int64(index)
	}
	if _, err := valueToProto(names); err == nil {
		t.Fatal("an oversized map must be refused")
	}
}

// TestSubscriptionStreaming walks one full subscription: confirmation,
// batch, trigger, close frame, and the sibling helpers.
func TestSubscriptionStreaming(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	ctx := context.Background()
	for index := 0; index < 2; index++ {
		if _, err := client.DocumentWrite(ctx,
			DocumentPut{Collection: "books", ID: "d", Body: map[string]Value{"i": index}},
			ExpectAny(), AckDefault, "op-1"); err != nil {
			t.Fatalf("the write failed: %v", err)
		}
	}
	subscription, err := client.Subscribe(ctx, 5, []string{"books"}, FromSequence(1))
	if err != nil {
		t.Fatalf("the subscription failed: %v", err)
	}
	defer subscription.Close()

	first, err := subscription.Recv()
	if err != nil {
		t.Fatalf("the first frame failed: %v", err)
	}
	confirmation, ok := first.Confirmed()
	if !ok {
		t.Fatal("the first frame must be the confirmation")
	}
	if confirmation.SubscriptionID != 5 || confirmation.StartSequence != 2 {
		t.Fatalf("the confirmation is wrong: %+v", confirmation)
	}
	second, err := subscription.Recv()
	if err != nil {
		t.Fatalf("the second frame failed: %v", err)
	}
	batch, ok := second.BatchEvent()
	if !ok {
		t.Fatal("the second frame must be a batch")
	}
	if len(batch.Events) != 1 || batch.Events[0].Kind != ChangePut || batch.Events[0].Sequence != 2 {
		t.Fatalf("the batch is wrong: %+v", batch)
	}
	through, err := subscription.Acknowledge(ctx, batch.NextSequence-1)
	if err != nil || through != batch.NextSequence-1 {
		t.Fatalf("the acknowledge failed: through=%d err=%v", through, err)
	}
	third, err := subscription.Recv()
	if err != nil {
		t.Fatalf("the third frame failed: %v", err)
	}
	trigger, ok := third.TriggerEvent()
	if !ok || trigger.Collection != "books" || trigger.Sequence != 2 {
		t.Fatalf("the trigger is wrong: ok=%v frame=%+v", ok, trigger)
	}
	fourth, err := subscription.Recv()
	if err != nil {
		t.Fatalf("the fourth frame failed: %v", err)
	}
	closed, ok := fourth.ClosedEvent()
	if !ok || closed.Reason != CloseShuttingDown {
		t.Fatalf("the close frame is wrong: ok=%v frame=%+v", ok, closed)
	}
	if err := subscription.Unsubscribe(ctx); err != nil {
		t.Fatalf("the unsubscribe failed: %v", err)
	}
	if _, err := subscription.Recv(); !errors.Is(err, io.EOF) {
		t.Fatalf("the stream must end after the close frame, got %v", err)
	}
}

// TestUnboundStream checks the identity rule on the stream path.
func TestUnboundStream(t *testing.T) {
	client, _ := startFake(t)
	if _, err := client.Subscribe(context.Background(), 1, nil, FromLive()); !errors.Is(err, ErrUnboundChannel) {
		t.Fatalf("an unbound subscribe must answer ErrUnboundChannel, got %v", err)
	}
}

// TestMutualTLSOverBufconn drives the certificate options over a real
// mutually authenticated TLS handshake on the in-memory transport.
func TestMutualTLSOverBufconn(t *testing.T) {
	pki := newTestPKI(t)
	fake := newFakeTBEP()
	listener := bufconn.Listen(1 << 20)
	pair, err := tls.X509KeyPair(pki.serverPEM, pki.serverKey)
	if err != nil {
		t.Fatalf("the server pair does not parse: %v", err)
	}
	clientPool := x509.NewCertPool()
	if !clientPool.AppendCertsFromPEM(pki.caPEM) {
		t.Fatal("the CA does not parse")
	}
	type handshakeObservation struct {
		state tls.ConnectionState
		err   error
	}
	negotiated := make(chan handshakeObservation, 1)
	server := grpc.NewServer(grpc.Creds(credentials.NewTLS(&tls.Config{
		Certificates: []tls.Certificate{pair},
		ClientCAs:    clientPool,
		ClientAuth:   tls.RequireAndVerifyClientCert,
		MinVersion:   tls.VersionTLS13,
	})), grpc.UnaryInterceptor(func(ctx context.Context, request any, _ *grpc.UnaryServerInfo, handler grpc.UnaryHandler) (any, error) {
		observation := handshakeObservation{}
		remote, ok := peer.FromContext(ctx)
		if !ok {
			observation.err = errors.New("the mTLS RPC carries no peer information")
		} else if tlsInfo, typed := remote.AuthInfo.(credentials.TLSInfo); !typed {
			observation.err = fmt.Errorf("the mTLS RPC carries auth info %T", remote.AuthInfo)
		} else {
			observation.state = tlsInfo.State
		}
		select {
		case negotiated <- observation:
		default:
		}
		return handler(ctx, request)
	}))
	fake.register(server)
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(server.Stop)

	client := dialBufconn(t, listener,
		WithRootCerts(pki.caPEM),
		WithClientCert(pki.clientPEM, pki.clientKey),
	)
	binding, err := client.Bind(context.Background(), "orders-api", "tenant-a")
	if err != nil {
		t.Fatalf("the bound channel over mTLS failed: %v", err)
	}
	if binding.ServingInstance != 7 {
		t.Fatalf("the bind answer is wrong: %+v", binding)
	}
	observation := <-negotiated
	if observation.err != nil {
		t.Fatal(observation.err)
	}
	if observation.state.Version != tls.VersionTLS13 {
		t.Fatalf("the channel negotiated TLS version %#x, want TLS 1.3", observation.state.Version)
	}
	if len(observation.state.PeerCertificates) != 1 || observation.state.PeerCertificates[0].Subject.CommonName != "terrabase-test-client" {
		t.Fatalf("the server did not authenticate the expected client certificate: %+v", observation.state.PeerCertificates)
	}
	if _, _, err := client.DocumentRead(context.Background(), "books", "ddia", ReadLevelDefault); err != nil {
		t.Fatalf("the read over mTLS failed: %v", err)
	}
}

// TestCertificateOptionFailures checks the certificate options refuse
// unusable material.
func TestCertificateOptionFailures(t *testing.T) {
	if _, err := Dial("localhost:1", WithRootCerts([]byte("not a certificate"))); err == nil {
		t.Fatal("a bundle with no certificate must be refused")
	}
	if _, err := Dial("localhost:1", WithClientCert([]byte("nope"), []byte("nope"))); err == nil {
		t.Fatal("an unparseable client pair must be refused")
	}
	if _, err := Dial("localhost:1", WithTransportCredentials(nil)); err == nil {
		t.Fatal("nil credentials must be refused")
	}
	if _, err := Dial("localhost:1", WithContextDialer(nil)); err == nil {
		t.Fatal("a nil dialer must be refused")
	}
}

// TestFullSurfaceSmoke drives every typed method once against the
// fake, so a conversion mistake anywhere in the surface fails here
// rather than in production.
func TestFullSurfaceSmoke(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)
	ctx := context.Background()
	operation := func(name string, call func() error) {
		t.Helper()
		if err := call(); err != nil {
			t.Fatalf("%s failed: %v", name, err)
		}
	}
	operation("family read", func() error {
		_, err := client.FamilyRead(ctx, FamilyCounter, "hits", "terrabase.family-request/1", []byte("payload"))
		return err
	})
	operation("family write", func() error {
		_, err := client.FamilyWrite(ctx, FamilyCounter, "hits", "terrabase.family-request/1", []byte("payload"), "op-family")
		return err
	})
	operation("sync setup", func() error {
		_, err := client.SyncSessionSetup(ctx, "books", nil)
		return err
	})
	operation("sync delta", func() error {
		_, err := client.SyncDeltaPull(ctx, 11, []byte("cursor-1"))
		return err
	})
	operation("sync snapshot", func() error {
		_, err := client.SyncSnapshotPull(ctx, 11)
		return err
	})
	operation("sync checkpoint", func() error {
		return client.SyncCheckpointAck(ctx, 11, []byte("checkpoint-1"))
	})
	operation("sync mutation", func() error {
		outcome, err := client.SyncMutationSubmit(ctx, 11, "doc", SyncPut(map[string]Value{"n": 1}), "op-sync")
		if err != nil {
			return err
		}
		if _, applied := outcome.Applied(); !applied {
			t.Fatal("the sync mutation must apply")
		}
		return nil
	})
	operation("sync close", func() error { return client.SyncSessionClose(ctx, 11) })
	operation("schema declare", func() error {
		_, err := client.SchemaDeclare(ctx, "books", map[string]Value{"title": "text"}, "op-schema")
		return err
	})
	operation("schema status", func() error {
		_, err := client.SchemaStatus(ctx, "books")
		return err
	})
	operation("schema validate", func() error {
		validation, err := client.SchemaValidate(ctx, "books", map[string]Value{"title": "ok"})
		if err != nil {
			return err
		}
		if !validation.Valid || validation.EnforcedVersion != 4 {
			t.Fatalf("the validation answer is wrong: %+v", validation)
		}
		return nil
	})
	operation("migration status", func() error {
		state, err := client.MigrationStatus(ctx, "books")
		if err != nil {
			return err
		}
		if state.Kind != MigrationNone {
			t.Fatalf("the migration state is wrong: %+v", state)
		}
		return nil
	})
	operation("migration dry run", func() error {
		subtitleDefault := TextLiteral("none")
		plan, err := client.MigrationDryRun(ctx, MigrationContract{
			Collection:  "books",
			FromVersion: 1,
			ToVersion:   2,
			Target:      map[string]Value{"title": "text"},
			Transforms: []TransformStep{
				{Kind: TransformAddField, Field: "subtitle", Default: &subtitleDefault},
				{Kind: TransformRenameField, Field: "name", To: "title"},
				{Kind: TransformRetypeField, Field: "count", To: "pages", Coercion: CoercionIntegerToNumber},
				{Kind: TransformRemoveField, Field: "legacy"},
			},
		})
		if err != nil {
			return err
		}
		if !plan.Ready || plan.TransformCounts.Added != 1 || plan.TransformCounts.Renamed != 1 {
			t.Fatalf("the plan is wrong: %+v", plan)
		}
		return nil
	})
	operation("migration apply", func() error {
		receipt, err := client.MigrationApply(ctx, MigrationContract{
			Collection: "books", FromVersion: 1, ToVersion: 2, Target: map[string]Value{"title": "text"},
		}, false, "op-migrate")
		if err != nil {
			return err
		}
		if receipt.RollbackLimit != RollbackReversible || receipt.Transformed != 3 {
			t.Fatalf("the migration receipt is wrong: %+v", receipt)
		}
		return nil
	})
	operation("replication conflicts", func() error {
		report, err := client.ReplicationConflicts(ctx)
		if err != nil {
			return err
		}
		if report.Member != "alpha" || len(report.Records) != 1 || report.Records[0].Outcome.Kind != ConflictWinner {
			t.Fatalf("the conflicts report is wrong: %+v", report)
		}
		return nil
	})
	operation("replication fenced", func() error {
		report, err := client.ReplicationFenced(ctx)
		if err != nil {
			return err
		}
		if len(report.Records) != 1 || report.Records[0].HighestEpoch != 3 {
			t.Fatalf("the fenced report is wrong: %+v", report)
		}
		return nil
	})
	operation("replication depositions", func() error {
		report, err := client.ReplicationDepositions(ctx)
		if err != nil {
			return err
		}
		if len(report.Records) != 1 || report.Records[0].NewOwner != "beta" {
			t.Fatalf("the depositions report is wrong: %+v", report)
		}
		return nil
	})
	operation("replication cursor", func() error {
		cursor, err := client.ReplicationCursor(ctx)
		if err != nil {
			return err
		}
		if cursor.Position != 42 {
			t.Fatalf("the cursor is wrong: %+v", cursor)
		}
		return nil
	})
	operation("replication health", func() error {
		health, err := client.ReplicationHealth(ctx)
		if err != nil {
			return err
		}
		if health.Status != ReplicationActive || health.Status.String() != "active" {
			t.Fatalf("the replication health is wrong: %+v", health)
		}
		return nil
	})
	operation("replication denials", func() error {
		report, err := client.ReplicationDenials(ctx)
		if err != nil {
			return err
		}
		if len(report.Records) != 1 || report.Records[0].Seam != SeamServeRequest {
			t.Fatalf("the denials report is wrong: %+v", report)
		}
		return nil
	})
	operation("topology", func() error {
		topology, err := client.TopologyRead(ctx)
		if err != nil {
			return err
		}
		if topology.Binding != BindingNetworkMTLS || topology.ClusterMode != ClusterStandalone || !topology.EngineTopologyAvailable {
			t.Fatalf("the topology is wrong: %+v", topology)
		}
		return nil
	})
	operation("admin status", func() error {
		status, err := client.AdminStatus(ctx)
		if err != nil {
			return err
		}
		if status.RegistryVersion != "1.3" || status.FramesServed != 42 {
			t.Fatalf("the admin status is wrong: %+v", status)
		}
		return nil
	})
	operation("supervision health", func() error {
		report, err := client.SupervisionHealth(ctx)
		if err != nil {
			return err
		}
		if report.State != HealthServing || report.State.String() != "serving" {
			t.Fatalf("the supervision report is wrong: %+v", report)
		}
		return nil
	})
	operation("supervision shutdown", func() error {
		receipt, err := client.SupervisionShutdown(ctx, ShutdownOperator)
		if err != nil {
			return err
		}
		if receipt.State != HealthDraining {
			t.Fatalf("the shutdown receipt is wrong: %+v", receipt)
		}
		return nil
	})
	operation("tier configure", func() error {
		status, err := client.TierConfigure(ctx, TierPolicy{
			PolicyID: "tier-1", Version: 3, NVMeBudgetBytes: 1 << 30, PinnedSegmentIDs: []uint64{9},
		})
		if err != nil {
			return err
		}
		if status.PolicyID != "tier-1" || status.Version != 3 {
			t.Fatalf("the tier status is wrong: %+v", status)
		}
		return nil
	})
	operation("tier inspect", func() error {
		status, err := client.TierInspect(ctx)
		if err != nil {
			return err
		}
		if !status.Configured || status.ObjectCount != 8 {
			t.Fatalf("the tier status is wrong: %+v", status)
		}
		return nil
	})
	operation("tier dry run", func() error {
		move, err := client.TierDryRun(ctx)
		if err != nil {
			return err
		}
		if !move.DryRun || move.Decision != TierDecisionNone || len(move.Reasons) != 1 {
			t.Fatalf("the tier dry run is wrong: %+v", move)
		}
		return nil
	})
	operation("tier explain", func() error {
		move, err := client.TierExplain(ctx)
		if err != nil {
			return err
		}
		if move.Decision != TierPromote {
			t.Fatalf("the tier explain is wrong: %+v", move)
		}
		return nil
	})
	operation("tier run", func() error {
		move, err := client.TierRun(ctx, "op-tier")
		if err != nil {
			return err
		}
		if move.Decision != TierDemote || !move.ManifestAccepted {
			t.Fatalf("the tier run is wrong: %+v", move)
		}
		return nil
	})
	operation("tier pause", func() error {
		paused, err := client.TierPause(ctx, "op-pause")
		if err != nil {
			return err
		}
		if !paused {
			t.Fatal("the tier must pause")
		}
		return nil
	})
	operation("tier resume", func() error {
		paused, err := client.TierResume(ctx, "op-resume")
		if err != nil {
			return err
		}
		if paused {
			t.Fatal("the tier must resume")
		}
		return nil
	})
	operation("sync rejection", func() error {
		outcome, err := client.SyncMutationSubmit(ctx, 11, "out-of-order", SyncDelete(), "op-sync-2")
		if err != nil {
			return err
		}
		if reason, refused := outcome.Refused(); !refused || reason != MutationSequenceOutOfOrder {
			t.Fatalf("the sync rejection is wrong: %+v", outcome)
		}
		return nil
	})
	operation("schema violation", func() error {
		validation, err := client.SchemaValidate(ctx, "books", map[string]Value{"title": "invalid"})
		if err != nil {
			return err
		}
		if validation.Valid || len(validation.Violations) != 1 || validation.Violations[0].Kind != ViolationTypeMismatch {
			t.Fatalf("the validation answer is wrong: %+v", validation)
		}
		return nil
	})
}
