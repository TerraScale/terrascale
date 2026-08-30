package terrabase

import (
	"context"
	"fmt"
	"strings"
	"sync"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
	"google.golang.org/protobuf/proto"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

// fakeTBEP is the in-memory stand-in for the Rust engine, implemented
// from the generated service stubs. It enforces the serving
// conventions the real server does — identity on every RPC except
// Channel/Bind, idempotency keys on mutations, engine-shaped errors —
// and answers from a tiny document store plus canned state.
type fakeTBEP struct {
	v1.UnimplementedChannelServer
	v1.UnimplementedEngineServer
	v1.UnimplementedBackboneServer
	v1.UnimplementedSyncServer
	v1.UnimplementedSchemaServer
	v1.UnimplementedReplicationServer
	v1.UnimplementedMaintenanceServer
	v1.UnimplementedUsageServer
	v1.UnimplementedTierServer

	mu                  sync.Mutex
	documents           map[string]*v1.DocumentBody
	sequence            uint64
	changes             []*v1.ChangeEvent
	principals          []string
	tenants             []string
	keys                []string
	admissions          []*v1.UsageAdmissionContext
	usageFacts          map[string]*v1.UsageRecordResponse
	usageTenantOverride string
	usageRegionOverride *v1.UsageRegion
	usageMeterOverride  *v1.UsageMeter
	// bindTenant lets boundary tests model a compromised or buggy host
	// returning a tenant other than the one requested by the caller.
	bindTenant string
	// failCode, when set, refuses the next engine mutation with the
	// engine-shaped error of that code; unregistered answers a code
	// outside the closed vocabulary instead.
	failCode     *v1.ErrorCode
	unregistered bool
}

func newFakeTBEP() *fakeTBEP {
	return &fakeTBEP{
		documents:  map[string]*v1.DocumentBody{},
		usageFacts: map[string]*v1.UsageRecordResponse{},
	}
}

// register wires every service of the contract onto one server.
func (fake *fakeTBEP) register(server *grpc.Server) {
	v1.RegisterChannelServer(server, fake)
	v1.RegisterEngineServer(server, fake)
	v1.RegisterBackboneServer(server, fake)
	v1.RegisterSyncServer(server, fake)
	v1.RegisterSchemaServer(server, fake)
	v1.RegisterReplicationServer(server, fake)
	v1.RegisterMaintenanceServer(server, fake)
	v1.RegisterUsageServer(server, fake)
	v1.RegisterTierServer(server, fake)
}

// engineStatus builds the failure shape of the Rust server: the closed
// engine code carried as a TbepError detail of the trailing status.
// WithDetails wraps the detail with the same type URL the engine
// packs, so the bytes on the wire match the real failure shape.
func engineStatus(grpcCode codes.Code, engineCode v1.ErrorCode, detail string) error {
	failure, err := status.New(grpcCode, detail).WithDetails(&v1.TbepError{
		Code:   engineCode,
		Detail: detail,
	})
	if err != nil {
		panic(err)
	}
	return failure.Err()
}

// identityOf extracts the channel identity, failing closed exactly as
// the engine does.
func (fake *fakeTBEP) identityOf(ctx context.Context) (string, string, error) {
	incoming, _ := metadata.FromIncomingContext(ctx)
	principal := firstMetadata(incoming, principalMetadata)
	tenant := firstMetadata(incoming, tenantMetadata)
	fake.mu.Lock()
	defer fake.mu.Unlock()
	fake.principals = append(fake.principals, principal)
	fake.tenants = append(fake.tenants, tenant)
	if principal == "" || tenant == "" {
		return "", "", engineStatus(codes.Unauthenticated, v1.ErrorCode_ERROR_CODE_UNBOUND_CHANNEL,
			"the RPC carries no bound channel identity")
	}
	return principal, tenant, nil
}

// idempotencyOf extracts the mutation retry identity, failing closed
// exactly as the engine does.
func (fake *fakeTBEP) idempotencyOf(ctx context.Context) (string, error) {
	incoming, _ := metadata.FromIncomingContext(ctx)
	key := firstMetadata(incoming, idempotencyMetadata)
	fake.mu.Lock()
	defer fake.mu.Unlock()
	fake.keys = append(fake.keys, key)
	if key == "" {
		return "", engineStatus(codes.InvalidArgument, v1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING,
			"the mutation RPC carries no idempotency key")
	}
	return key, nil
}

func firstMetadata(incoming metadata.MD, key string) string {
	if values := incoming.Get(key); len(values) > 0 {
		return values[0]
	}
	return ""
}

// plannedFailure answers the injected refusal of one engine mutation.
func (fake *fakeTBEP) plannedFailure() error {
	if fake.unregistered {
		// A code the client's vocabulary does not know yet.
		failure, err := status.New(codes.Internal, "future").WithDetails(&v1.TbepError{
			Code:   v1.ErrorCode(99),
			Detail: "an error code this client does not know",
		})
		if err != nil {
			panic(err)
		}
		return failure.Err()
	}
	if fake.failCode != nil {
		return engineStatus(codes.ResourceExhausted, *fake.failCode, "the engine is serving its in-flight bound; retry")
	}
	return nil
}

// lastKey is the idempotency key of the most recent mutation.
func (fake *fakeTBEP) lastKey() string {
	fake.mu.Lock()
	defer fake.mu.Unlock()
	if len(fake.keys) == 0 {
		return ""
	}
	return fake.keys[len(fake.keys)-1]
}

// lastTenant is the tenant of the most recent identity check.
func (fake *fakeTBEP) lastTenant() string {
	fake.mu.Lock()
	defer fake.mu.Unlock()
	if len(fake.tenants) == 0 {
		return ""
	}
	return fake.tenants[len(fake.tenants)-1]
}

func (fake *fakeTBEP) admitUsage(usage *v1.UsageAdmissionContext, key string) error {
	if usage == nil {
		return nil
	}
	if usage.GetRequestId() != key {
		return engineStatus(codes.InvalidArgument, v1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING,
			"the usage admission request identity differs from the idempotency key")
	}
	copied := proto.Clone(usage).(*v1.UsageAdmissionContext)
	fake.mu.Lock()
	fake.admissions = append(fake.admissions, copied)
	fake.mu.Unlock()
	return nil
}

func (fake *fakeTBEP) lastAdmission() *v1.UsageAdmissionContext {
	fake.mu.Lock()
	defer fake.mu.Unlock()
	if len(fake.admissions) == 0 {
		return nil
	}
	return proto.Clone(fake.admissions[len(fake.admissions)-1]).(*v1.UsageAdmissionContext)
}

// put stores one document and records the backbone change.
func (fake *fakeTBEP) put(collection, id string, body *v1.Value) *v1.CommitReceipt {
	fake.mu.Lock()
	defer fake.mu.Unlock()
	key := collection + "\x00" + id
	previous := fake.documents[key]
	version := uint64(1)
	if previous != nil {
		version = previous.Version + 1
	}
	fake.documents[key] = &v1.DocumentBody{Id: id, Collection: collection, Version: version, Body: body}
	fake.sequence++
	fake.changes = append(fake.changes, &v1.ChangeEvent{
		Sequence:   fake.sequence,
		Kind:       v1.ChangeKind_CHANGE_KIND_PUT,
		Collection: collection,
		Document:   id,
	})
	return &v1.CommitReceipt{
		Sequence:        fake.sequence,
		Version:         version,
		Acknowledgement: v1.Acknowledgement_ACK_REGIONAL_QUORUM,
		Backup:          v1.BackupOutcome_BACKUP_NONE,
	}
}

// remove deletes one document and records the backbone change.
func (fake *fakeTBEP) remove(collection, id string) *v1.CommitReceipt {
	fake.mu.Lock()
	defer fake.mu.Unlock()
	key := collection + "\x00" + id
	if _, held := fake.documents[key]; !held {
		return nil
	}
	delete(fake.documents, key)
	fake.sequence++
	fake.changes = append(fake.changes, &v1.ChangeEvent{
		Sequence:   fake.sequence,
		Kind:       v1.ChangeKind_CHANGE_KIND_DELETE,
		Collection: collection,
		Document:   id,
	})
	return &v1.CommitReceipt{
		Sequence:        fake.sequence,
		Acknowledgement: v1.Acknowledgement_ACK_REGIONAL_QUORUM,
		Backup:          v1.BackupOutcome_BACKUP_NONE,
	}
}

func documentKey(collection, id string) string { return collection + "\x00" + id }

// --- Channel ---

func (fake *fakeTBEP) Bind(_ context.Context, request *v1.ChannelBindRequest) (*v1.ChannelBindResponse, error) {
	if request.GetPrincipal() == "" || request.GetTenant() == "" {
		return nil, engineStatus(codes.InvalidArgument, v1.ErrorCode_ERROR_CODE_INVALID_REQUEST,
			"a bind carries no application identity")
	}
	tenant := request.GetTenant()
	if fake.bindTenant != "" {
		tenant = fake.bindTenant
	}
	return &v1.ChannelBindResponse{Tenant: tenant, ServingInstance: 7}, nil
}

// --- Engine ---

func (fake *fakeTBEP) DocumentRead(ctx context.Context, request *v1.DocumentReadRequest) (*v1.DocumentReadResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	fake.mu.Lock()
	defer fake.mu.Unlock()
	body, held := fake.documents[documentKey(request.GetCollection(), request.GetDocument())]
	if !held {
		return &v1.DocumentReadResponse{Found: false}, nil
	}
	return &v1.DocumentReadResponse{Found: true, Document: body, Position: fake.sequence}, nil
}

func (fake *fakeTBEP) DocumentWrite(ctx context.Context, request *v1.DocumentWriteRequest) (*v1.DocumentWriteResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	key, err := fake.idempotencyOf(ctx)
	if err != nil {
		return nil, err
	}
	if err := fake.admitUsage(request.GetUsage(), key); err != nil {
		return nil, err
	}
	if failure := fake.plannedFailure(); failure != nil {
		return nil, failure
	}
	switch collection := request.GetDocument().GetCollection(); {
	case collection == "race":
		return &v1.DocumentWriteResponse{Outcome: &v1.WriteOutcome{
			Outcome: &v1.WriteOutcome_Conflict{Conflict: &v1.WriteConflict{}}}}, nil
	case collection == "refuse":
		return &v1.DocumentWriteResponse{Outcome: &v1.WriteOutcome{
			Outcome: &v1.WriteOutcome_Rejected{Rejected: &v1.WriteRejected{
				Reason: v1.WriteReason_WRITE_REASON_SCHEMA_REFUSED}}}}, nil
	}
	receipt := fake.put(request.GetDocument().GetCollection(), request.GetDocument().GetId(), request.GetDocument().GetBody())
	receipt.OperationId = request.GetOperationId()
	return &v1.DocumentWriteResponse{Outcome: &v1.WriteOutcome{
		Outcome: &v1.WriteOutcome_Committed{Committed: receipt}}}, nil
}

func (fake *fakeTBEP) DocumentDelete(ctx context.Context, request *v1.DocumentDeleteRequest) (*v1.DocumentDeleteResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	key, err := fake.idempotencyOf(ctx)
	if err != nil {
		return nil, err
	}
	if err := fake.admitUsage(request.GetUsage(), key); err != nil {
		return nil, err
	}
	if failure := fake.plannedFailure(); failure != nil {
		return nil, failure
	}
	receipt := fake.remove(request.GetCollection(), request.GetDocument())
	if receipt == nil {
		return &v1.DocumentDeleteResponse{Outcome: &v1.WriteOutcome{
			Outcome: &v1.WriteOutcome_Rejected{Rejected: &v1.WriteRejected{
				Reason: v1.WriteReason_WRITE_REASON_NOT_FOUND}}}}, nil
	}
	receipt.OperationId = request.GetOperationId()
	return &v1.DocumentDeleteResponse{Outcome: &v1.WriteOutcome{
		Outcome: &v1.WriteOutcome_Committed{Committed: receipt}}}, nil
}

func (fake *fakeTBEP) DocumentBatch(ctx context.Context, request *v1.DocumentBatchRequest) (*v1.DocumentBatchResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	key, err := fake.idempotencyOf(ctx)
	if err != nil {
		return nil, err
	}
	if err := fake.admitUsage(request.GetUsage(), key); err != nil {
		return nil, err
	}
	if failure := fake.plannedFailure(); failure != nil {
		return nil, failure
	}
	var last *v1.CommitReceipt
	for _, item := range request.GetItems() {
		last = fake.put(item.GetDocument().GetCollection(), item.GetDocument().GetId(), item.GetDocument().GetBody())
		last.OperationId = item.GetOperationId()
	}
	return &v1.DocumentBatchResponse{Outcome: &v1.WriteOutcome{
		Outcome: &v1.WriteOutcome_Committed{Committed: last}}}, nil
}

// QueryExecute answers every document of the collection named by the
// query text ("collection:<name>"), honoring maxItems as the item
// bound.
func (fake *fakeTBEP) QueryExecute(ctx context.Context, request *v1.QueryExecuteRequest) (*v1.QueryExecuteResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	collection := strings.TrimPrefix(request.GetText(), "collection:")
	fake.mu.Lock()
	defer fake.mu.Unlock()
	var documents []*v1.DocumentBody
	for _, body := range fake.documents {
		if body.Collection == collection {
			documents = append(documents, body)
		}
	}
	terminal := v1.QueryTerminal_QUERY_TERMINAL_COMPLETE
	limit := int(request.GetMaxItems())
	if limit > 0 && len(documents) > limit {
		documents = documents[:limit]
		terminal = v1.QueryTerminal_QUERY_TERMINAL_TRUNCATED
	}
	return &v1.QueryExecuteResponse{
		Documents: documents,
		Count:     uint32(len(documents)),
		Terminal:  terminal,
		Position:  fake.sequence,
	}, nil
}

func (fake *fakeTBEP) QueryCancel(ctx context.Context, request *v1.QueryCancelRequest) (*v1.QueryCancelResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if request.GetQueryId() == 404 {
		return &v1.QueryCancelResponse{Outcome: v1.CancelOutcome_CANCEL_OUTCOME_UNKNOWN_QUERY}, nil
	}
	return &v1.QueryCancelResponse{Outcome: v1.CancelOutcome_CANCEL_OUTCOME_CANCELLED}, nil
}

func (fake *fakeTBEP) FamilyRead(ctx context.Context, request *v1.FamilyReadRequest) (*v1.FamilyReadResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.FamilyReadResponse{
		ResponseCodec: "terrabase.family-response/1",
		Response:      []byte(fmt.Sprintf("read:%s:%s", request.GetFamily(), request.GetName())),
	}, nil
}

func (fake *fakeTBEP) FamilyWrite(ctx context.Context, request *v1.FamilyWriteRequest) (*v1.FamilyWriteResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	key, err := fake.idempotencyOf(ctx)
	if err != nil {
		return nil, err
	}
	if err := fake.admitUsage(request.GetUsage(), key); err != nil {
		return nil, err
	}
	return &v1.FamilyWriteResponse{
		ResponseCodec: "terrabase.family-response/1",
		Response:      append([]byte("written:"), request.GetRequest()...),
	}, nil
}

// --- Backbone ---

// Subscribe streams the confirmation, one batch of every recorded
// change after the requested start, one trigger, and the close frame.
func (fake *fakeTBEP) Subscribe(request *v1.BackboneSubscribeRequest, stream grpc.ServerStreamingServer[v1.BackboneStreamEvent]) error {
	if _, _, err := fake.identityOf(stream.Context()); err != nil {
		return err
	}
	fake.mu.Lock()
	start := uint64(0)
	if sequence, ok := request.GetFrom().GetStart().(*v1.SubscriptionStart_Sequence); ok {
		start = sequence.Sequence
	}
	var events []*v1.ChangeEvent
	for _, change := range fake.changes {
		if change.GetSequence() > start {
			events = append(events, change)
		}
	}
	sequence := fake.sequence
	fake.mu.Unlock()
	if err := stream.Send(&v1.BackboneStreamEvent{Event: &v1.BackboneStreamEvent_Subscribed{
		Subscribed: &v1.BackboneSubscribeResponse{
			SubscriptionId: request.GetSubscriptionId(),
			StartSequence:  sequence,
		}}}); err != nil {
		return err
	}
	if err := stream.Send(&v1.BackboneStreamEvent{Event: &v1.BackboneStreamEvent_Batch{
		Batch: &v1.SubscriptionBatch{
			SubscriptionId: request.GetSubscriptionId(),
			FromSequence:   start + 1,
			Events:         events,
			NextSequence:   sequence + 1,
		}}}); err != nil {
		return err
	}
	if err := stream.Send(&v1.BackboneStreamEvent{Event: &v1.BackboneStreamEvent_Trigger{
		Trigger: &v1.TriggerNotification{
			SubscriptionId: request.GetSubscriptionId(),
			Collection:     "books",
			Sequence:       sequence,
			OperationId:    "op-trigger",
		}}}); err != nil {
		return err
	}
	return stream.Send(&v1.BackboneStreamEvent{Event: &v1.BackboneStreamEvent_Closed{
		Closed: &v1.SubscriptionClosed{
			SubscriptionId: request.GetSubscriptionId(),
			Reason:         v1.SubscriptionCloseReason_SUBSCRIPTION_CLOSE_REASON_SHUTTING_DOWN,
		}}})
}

func (fake *fakeTBEP) Acknowledge(ctx context.Context, request *v1.BackboneAcknowledgeRequest) (*v1.BackboneAcknowledgeResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.BackboneAcknowledgeResponse{
		SubscriptionId:      request.GetSubscriptionId(),
		AcknowledgedThrough: request.GetThroughSequence(),
	}, nil
}

func (fake *fakeTBEP) Unsubscribe(ctx context.Context, request *v1.BackboneUnsubscribeRequest) (*v1.BackboneUnsubscribeResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.BackboneUnsubscribeResponse{SubscriptionId: request.GetSubscriptionId()}, nil
}

// --- Sync ---

func (fake *fakeTBEP) SessionSetup(ctx context.Context, request *v1.SyncSessionSetupRequest) (*v1.SyncSessionSetupResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SyncSessionSetupResponse{
		SessionId:     11,
		Checkpoint:    []byte("checkpoint-1"),
		RetainedFloor: []byte("floor-1"),
	}, nil
}

func (fake *fakeTBEP) DeltaPull(ctx context.Context, request *v1.SyncDeltaPullRequest) (*v1.SyncDeltaPullResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SyncDeltaPullResponse{
		SessionId:    request.GetSessionId(),
		PayloadCodec: "terrabase-native/1",
		Payload:      []byte("delta-payload"),
		Next:         []byte("cursor-2"),
		Commits:      2,
	}, nil
}

func (fake *fakeTBEP) SnapshotPull(ctx context.Context, request *v1.SyncSnapshotPullRequest) (*v1.SyncSnapshotPullResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SyncSnapshotPullResponse{
		SessionId:    request.GetSessionId(),
		PayloadCodec: "terrabase-snapshot/1",
		Payload:      []byte("snapshot-payload"),
		Checkpoint:   []byte("checkpoint-9"),
	}, nil
}

func (fake *fakeTBEP) CheckpointAck(ctx context.Context, request *v1.SyncCheckpointAckRequest) (*v1.SyncCheckpointAckResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SyncCheckpointAckResponse{SessionId: request.GetSessionId()}, nil
}

func (fake *fakeTBEP) MutationSubmit(ctx context.Context, request *v1.SyncMutationSubmitRequest) (*v1.SyncMutationSubmitResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	key, err := fake.idempotencyOf(ctx)
	if err != nil {
		return nil, err
	}
	if err := fake.admitUsage(request.GetUsage(), key); err != nil {
		return nil, err
	}
	if request.GetDocument() == "out-of-order" {
		return &v1.SyncMutationSubmitResponse{Outcome: &v1.MutationOutcome{
			Outcome: &v1.MutationOutcome_Rejected{Rejected: &v1.MutationRejected{
				Reason: v1.MutationReason_MUTATION_REASON_SEQUENCE_OUT_OF_ORDER}}}}, nil
	}
	return &v1.SyncMutationSubmitResponse{Outcome: &v1.MutationOutcome{
		Outcome: &v1.MutationOutcome_Applied{Applied: &v1.CommitReceipt{
			Sequence:        5,
			Version:         1,
			Acknowledgement: v1.Acknowledgement_ACK_REGIONAL_QUORUM,
			Backup:          v1.BackupOutcome_BACKUP_NONE,
		}}}}, nil
}

func (fake *fakeTBEP) SessionClose(ctx context.Context, request *v1.SyncSessionCloseRequest) (*v1.SyncSessionCloseResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SyncSessionCloseResponse{SessionId: request.GetSessionId()}, nil
}

// --- Schema ---

func (fake *fakeTBEP) Declare(ctx context.Context, request *v1.SchemaDeclareRequest) (*v1.SchemaDeclareResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if _, err := fake.idempotencyOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SchemaDeclareResponse{EnforcedVersion: 4, RuleCount: 2}, nil
}

func (fake *fakeTBEP) Status(ctx context.Context, request *v1.SchemaStatusRequest) (*v1.SchemaStatusResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SchemaStatusResponse{EnforcedVersion: 4, RuleCount: 2}, nil
}

// Validate answers invalid when the document's "title" field is the
// text "invalid"; anything else validates clean.
func (fake *fakeTBEP) Validate(ctx context.Context, request *v1.SchemaValidateRequest) (*v1.SchemaValidateResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	for _, entry := range request.GetDocument().GetMap().GetEntries() {
		if entry.GetName() == "title" && entry.GetValue().GetText() == "invalid" {
			return &v1.SchemaValidateResponse{EnforcedVersion: 4, Valid: false, Violations: []*v1.SchemaViolation{{
				Field: "title",
				Kind:  v1.ViolationKind_VIOLATION_KIND_TYPE_MISMATCH,
			}}}, nil
		}
	}
	return &v1.SchemaValidateResponse{EnforcedVersion: 4, Valid: true}, nil
}

func (fake *fakeTBEP) MigrationStatus(ctx context.Context, request *v1.MigrationStatusRequest) (*v1.MigrationStatusResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.MigrationStatusResponse{State: &v1.MigrationState{
		Kind: v1.MigrationStateKind_MIGRATION_STATE_KIND_NONE,
	}}, nil
}

func (fake *fakeTBEP) MigrationDryRun(ctx context.Context, request *v1.MigrationDryRunRequest) (*v1.MigrationDryRunResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	contract := request.GetContract()
	return &v1.MigrationDryRunResponse{Plan: &v1.MigrationPlan{
		Manifest:        "manifest-1",
		Collection:      contract.GetCollection(),
		FromVersion:     contract.GetFromVersion(),
		ToVersion:       contract.GetToVersion(),
		Ready:           true,
		Documents:       3,
		SourceDigest:    "digest-a",
		ResultDigest:    "digest-b",
		TransformCounts: &v1.TransformCounts{Added: 1, Renamed: 1},
	}}, nil
}

func (fake *fakeTBEP) MigrationApply(ctx context.Context, request *v1.MigrationApplyRequest) (*v1.MigrationApplyResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if _, err := fake.idempotencyOf(ctx); err != nil {
		return nil, err
	}
	contract := request.GetContract()
	return &v1.MigrationApplyResponse{Receipt: &v1.MigrationReceipt{
		Collection:      contract.GetCollection(),
		Manifest:        "manifest-1",
		FromVersion:     contract.GetFromVersion(),
		ToVersion:       contract.GetToVersion(),
		Documents:       3,
		Transformed:     3,
		TransformCounts: &v1.TransformCounts{Added: 1, Renamed: 1},
		Pages:           1,
		RollbackLimit:   v1.RollbackLimitKind_ROLLBACK_LIMIT_KIND_REVERSIBLE,
	}}, nil
}

// --- Replication ---

func (fake *fakeTBEP) Conflicts(ctx context.Context, _ *v1.ReplicationConflictsRequest) (*v1.ReplicationConflictsResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.ReplicationConflictsResponse{Member: "alpha", Records: []*v1.ConflictRecord{{
		Tenant:     "tenant-a",
		Operations: []string{"op-1"},
		Members:    []string{"alpha", "beta"},
		Clocks:     []*v1.ClockStamp{{Ticks: 3, Counter: 1}},
		Outcome:    &v1.ConflictOutcome{Kind: v1.ConflictOutcomeKind_CONFLICT_OUTCOME_KIND_WINNER, Member: "alpha"},
	}}}, nil
}

func (fake *fakeTBEP) Fenced(ctx context.Context, _ *v1.ReplicationFencedRequest) (*v1.ReplicationFencedResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.ReplicationFencedResponse{Member: "alpha", Records: []*v1.FencedRecord{{
		Range: "0-99", BatchEpoch: 2, HighestEpoch: 3, Source: "beta",
	}}}, nil
}

func (fake *fakeTBEP) Depositions(ctx context.Context, _ *v1.ReplicationDepositionsRequest) (*v1.ReplicationDepositionsResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.ReplicationDepositionsResponse{Member: "alpha", Records: []*v1.DepositionRecord{{
		Range: "0-99", HeldEpoch: 1, OwnerEpoch: 2, NewOwner: "beta",
	}}}, nil
}

func (fake *fakeTBEP) Cursor(ctx context.Context, _ *v1.ReplicationCursorRequest) (*v1.ReplicationCursorResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.ReplicationCursorResponse{Member: "alpha", Position: 42}, nil
}

func (fake *fakeTBEP) Health(ctx context.Context, _ *v1.ReplicationHealthRequest) (*v1.ReplicationHealthResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.ReplicationHealthResponse{
		Member: "alpha",
		Status: v1.ReplicationStatus_REPLICATION_STATUS_ACTIVE,
	}, nil
}

func (fake *fakeTBEP) Denials(ctx context.Context, _ *v1.ReplicationDenialsRequest) (*v1.ReplicationDenialsResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.ReplicationDenialsResponse{Member: "alpha", Records: []*v1.DenialRecord{{
		Seam:            v1.DenialSeam_DENIAL_SEAM_SERVE_REQUEST,
		ScopeTenant:     "tenant-a",
		AttemptedTenant: "tenant-b",
		Counterparty:    "beta",
	}}}, nil
}

// --- Maintenance ---

func (fake *fakeTBEP) TopologyRead(ctx context.Context, _ *v1.TopologyReadRequest) (*v1.TopologyReadResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TopologyReadResponse{
		Binding:                 v1.BindingKind_BINDING_KIND_NETWORK_MTLS,
		Subsystems:              []string{"engine", "backbone"},
		EngineTopologyAvailable: true,
		ClusterMode:             v1.ClusterMode_CLUSTER_MODE_STANDALONE,
	}, nil
}

func (fake *fakeTBEP) AdminStatus(ctx context.Context, _ *v1.AdminStatusRequest) (*v1.AdminStatusResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.AdminStatusResponse{
		RegistryVersion: "1.3",
		FramesServed:    42,
		Sessions:        1,
		Subscriptions:   2,
		Scopes:          3,
	}, nil
}

func (fake *fakeTBEP) SupervisionHealth(ctx context.Context, _ *v1.SupervisionHealthRequest) (*v1.SupervisionHealthResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.SupervisionHealthResponse{
		State:         v1.HealthState_HEALTH_STATE_SERVING,
		Sessions:      1,
		Subscriptions: 2,
		FramesServed:  42,
	}, nil
}

func (fake *fakeTBEP) SupervisionShutdown(ctx context.Context, request *v1.SupervisionShutdownRequest) (*v1.SupervisionShutdownResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if request.GetReason() == v1.ShutdownReason_SHUTDOWN_REASON_UNSPECIFIED {
		return nil, engineStatus(codes.InvalidArgument, v1.ErrorCode_ERROR_CODE_INVALID_REQUEST, "a shutdown carries no reason")
	}
	return &v1.SupervisionShutdownResponse{
		State:    v1.HealthState_HEALTH_STATE_DRAINING,
		InFlight: 0,
	}, nil
}

// --- Usage ---

func (fake *fakeTBEP) Record(ctx context.Context, request *v1.UsageRecordRequest) (*v1.UsageRecordResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	key, err := fake.idempotencyOf(ctx)
	if err != nil {
		return nil, err
	}
	if key != request.GetFactId() {
		return nil, engineStatus(codes.InvalidArgument, v1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING,
			"the usage fact identity differs from the idempotency key")
	}
	switch request.GetOriginRegion() {
	case v1.UsageRegion_USAGE_REGION_GRU,
		v1.UsageRegion_USAGE_REGION_SFO,
		v1.UsageRegion_USAGE_REGION_FRA:
	default:
		return nil, engineStatus(codes.InvalidArgument, v1.ErrorCode_ERROR_CODE_INVALID_REQUEST,
			"the usage fact carries an invalid Region")
	}
	if failure := fake.plannedFailure(); failure != nil {
		return nil, failure
	}
	fake.mu.Lock()
	defer fake.mu.Unlock()
	if previous := fake.usageFacts[request.GetFactId()]; previous != nil {
		copied := proto.Clone(previous).(*v1.UsageRecordResponse)
		copied.AlreadyRecorded = true
		return copied, nil
	}
	fake.sequence++
	receipt := &v1.UsageRecordResponse{Sequence: fake.sequence, ReplicaEpoch: 7}
	fake.usageFacts[request.GetFactId()] = receipt
	return proto.Clone(receipt).(*v1.UsageRecordResponse), nil
}

func (fake *fakeTBEP) Export(ctx context.Context, request *v1.UsageExportRequest) (*v1.UsageExportResponse, error) {
	_, tenant, err := fake.identityOf(ctx)
	if err != nil {
		return nil, err
	}
	if failure := fake.plannedFailure(); failure != nil {
		return nil, failure
	}
	if fake.usageTenantOverride != "" {
		tenant = fake.usageTenantOverride
	}
	checksum := make([]byte, usageChecksumBytes)
	for index := range checksum {
		checksum[index] = byte(index + 1)
	}
	sequence := uint64(1)
	factID := "fact-1"
	continuation := []byte("page-2")
	if string(request.GetContinuation()) == "page-2" {
		sequence = 2
		factID = "fact-2"
		continuation = nil
	}
	canonical := append([]byte("usage-envelope:"+factID+":"), checksum...)
	region := v1.UsageRegion_USAGE_REGION_GRU
	if fake.usageRegionOverride != nil {
		region = *fake.usageRegionOverride
	}
	meter := v1.UsageMeter_USAGE_METER_ADMITTED_REQUEST_UNITS
	if fake.usageMeterOverride != nil {
		meter = *fake.usageMeterOverride
	}
	return &v1.UsageExportResponse{
		Facts: []*v1.UsageFact{{
			FactId:                 factID,
			Meter:                  meter,
			MeterVersion:           1,
			Unit:                   v1.UsageUnit_USAGE_UNIT_REQUEST_UNIT,
			ChargeScope:            v1.UsageChargeScope_USAGE_CHARGE_SCOPE_ADMITTED_REQUEST,
			MintingRule:            v1.UsageMintingRule_USAGE_MINTING_RULE_STABLE_ADMITTED_REQUEST,
			Tenant:                 tenant,
			Project:                "project-a",
			Database:               "database-a",
			Partition:              "partition-a",
			Region:                 region,
			MeasurementStartMillis: 1000,
			MeasurementEndMillis:   1001,
			IntegerDelta:           "1",
			Sequence:               sequence,
			ReplicaEpoch:           7,
			FormatVersion:          1,
			Canonical:              canonical,
			Checksum:               checksum,
		}},
		Continuation:     continuation,
		SnapshotSequence: 2,
	}, nil
}

// --- Tier ---

func (fake *fakeTBEP) Configure(ctx context.Context, request *v1.TierConfigureRequest) (*v1.TierConfigureResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if _, err := fake.idempotencyOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TierConfigureResponse{
		PolicyId: request.GetPolicyId(),
		Version:  request.GetVersion(),
		Paused:   false,
	}, nil
}

func (fake *fakeTBEP) Inspect(ctx context.Context, _ *v1.TierInspectRequest) (*v1.TierInspectResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TierInspectResponse{Configured: true, Paused: false, ManifestEpoch: 2, ObjectCount: 8, NvmeBytes: 4096}, nil
}

func (fake *fakeTBEP) DryRun(ctx context.Context, _ *v1.TierDryRunRequest) (*v1.TierDryRunResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TierDryRunResponse{Receipt: &v1.TierMoveReceipt{
		PolicyId:      "tier-1",
		PolicyVersion: 3,
		Decision:      v1.TierDecision_TIER_DECISION_NONE,
		Reasons:       []*v1.TierMoveReason{{Fact: "budget", Observed: "under budget"}},
		DryRun:        true,
	}}, nil
}

func (fake *fakeTBEP) Explain(ctx context.Context, _ *v1.TierExplainRequest) (*v1.TierExplainResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TierExplainResponse{Receipt: &v1.TierMoveReceipt{
		PolicyId:      "tier-1",
		PolicyVersion: 3,
		Decision:      v1.TierDecision_TIER_DECISION_PROMOTE,
	}}, nil
}

func (fake *fakeTBEP) Run(ctx context.Context, _ *v1.TierRunRequest) (*v1.TierRunResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if _, err := fake.idempotencyOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TierRunResponse{Receipt: &v1.TierMoveReceipt{
		PolicyId:         "tier-1",
		PolicyVersion:    3,
		Decision:         v1.TierDecision_TIER_DECISION_DEMOTE,
		SegmentId:        9,
		Bytes:            128,
		ManifestBefore:   4,
		ManifestAfter:    5,
		ManifestAccepted: true,
	}}, nil
}

func (fake *fakeTBEP) Pause(ctx context.Context, _ *v1.TierPauseRequest) (*v1.TierPauseResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if _, err := fake.idempotencyOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TierPauseResponse{Paused: true}, nil
}

func (fake *fakeTBEP) Resume(ctx context.Context, _ *v1.TierResumeRequest) (*v1.TierResumeResponse, error) {
	if _, _, err := fake.identityOf(ctx); err != nil {
		return nil, err
	}
	if _, err := fake.idempotencyOf(ctx); err != nil {
		return nil, err
	}
	return &v1.TierResumeResponse{Paused: false}, nil
}
