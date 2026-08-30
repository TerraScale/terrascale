package tbep

import (
	"context"
	"fmt"
	"strings"

	terrabasev1 "git.terrascale.tech/TerraScale/TerraBase/go/tbep/gen/terrabase/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
)

// The wire metadata keys of the contract (ADR 0028): identity on every
// RPC, the retry identity on every mutation.
const (
	// PrincipalMetadata names the backend-supplied principal.
	PrincipalMetadata = "x-tb-principal"
	// TenantMetadata names the backend-supplied tenant.
	TenantMetadata = "x-tb-tenant"
	// IdempotencyMetadata names a mutation's retry identity.
	IdempotencyMetadata = "x-tb-idempotency-key"

	// MaxIdempotencyBytes is the idempotency-key bound.
	MaxIdempotencyBytes = 128
)

// Identity is the application identity one client presents: the
// principal and tenant a trusted backend supplied. The transport is
// expected to be mutually authenticated TLS in production; the identity
// names the application tenant within that channel, it does not
// authenticate it.
type Identity struct {
	// Principal is the acting principal (1..=128 bytes).
	Principal string
	// Tenant is the tenant scope (1..=64 bytes).
	Tenant string
}

// Client is one bound engine connection: every RPC this package issues
// carries the identity, and every mutation carries its idempotency key.
type Client struct {
	conn      *grpc.ClientConn
	identity  Identity
	channel   terrabasev1.ChannelClient
	engine    terrabasev1.EngineClient
	backbone  terrabasev1.BackboneClient
	sync      terrabasev1.SyncClient
	schema    terrabasev1.SchemaClient
	peers     terrabasev1.ReplicationClient
	care      terrabasev1.MaintenanceClient
	tiers     terrabasev1.TierClient
}

// DialOption customizes the connection before the identity interceptors
// are attached.
type DialOption func([]grpc.DialOption) []grpc.DialOption

// WithGRPCDialOptions appends raw gRPC dial options (last).
func WithGRPCDialOptions(options ...grpc.DialOption) DialOption {
	return func(base []grpc.DialOption) []grpc.DialOption {
		return append(base, options...)
	}
}

// Dial connects to the engine at target (for example "unix:///run/terrabase/engine.sock"
// or "engine.internal:8421") under one identity.
func Dial(target string, identity Identity, options ...DialOption) (*Client, error) {
	if strings.TrimSpace(identity.Principal) == "" || strings.TrimSpace(identity.Tenant) == "" {
		return nil, fmt.Errorf("tbep: an identity carries a principal and a tenant")
	}
	base := []grpc.DialOption{grpc.WithTransportCredentials(insecure.NewCredentials())}
	for _, option := range options {
		base = option(base)
	}
	base = append(base, identityInterceptors(identity)...)
	conn, err := grpc.NewClient(target, base...)
	if err != nil {
		return nil, fmt.Errorf("tbep: dial %s: %w", target, err)
	}
	return FromConn(conn, identity), nil
}

// FromConn wraps an existing connection (tests and gateways reuse one).
func FromConn(conn *grpc.ClientConn, identity Identity) *Client {
	return &Client{
		conn:     conn,
		identity: identity,
		channel:  terrabasev1.NewChannelClient(conn),
		engine:   terrabasev1.NewEngineClient(conn),
		backbone: terrabasev1.NewBackboneClient(conn),
		sync:     terrabasev1.NewSyncClient(conn),
		schema:   terrabasev1.NewSchemaClient(conn),
		peers:    terrabasev1.NewReplicationClient(conn),
		care:     terrabasev1.NewMaintenanceClient(conn),
		tiers:    terrabasev1.NewTierClient(conn),
	}
}

// Close closes the underlying connection.
func (c *Client) Close() error {
	return c.conn.Close()
}

// Identity answers the bound identity.
func (c *Client) Identity() Identity {
	return c.identity
}

// metadataFromOutgoing reads the caller-set outgoing metadata the
// identity join must preserve.
func metadataFromOutgoing(ctx context.Context) metadata.MD {
	if existing, ok := metadata.FromOutgoingContext(ctx); ok {
		return existing
	}
	return metadata.MD{}
}

func identityInterceptors(identity Identity) []grpc.DialOption {
	outgoing := metadata.Pairs(PrincipalMetadata, identity.Principal, TenantMetadata, identity.Tenant)
	unary := func(
		ctx context.Context,
		method string,
		req any,
		reply any,
		cc *grpc.ClientConn,
		invoker grpc.UnaryInvoker,
		opts ...grpc.CallOption,
	) error {
		// Join, not replace: per-call metadata the caller already
		// appended (an idempotency key) must survive the identity.
		return invoker(metadata.NewOutgoingContext(ctx, metadata.Join(outgoing, metadataFromOutgoing(ctx))), method, req, reply, cc, opts...)
	}
	stream := func(
		ctx context.Context,
		desc *grpc.StreamDesc,
		cc *grpc.ClientConn,
		method string,
		streamer grpc.Streamer,
		opts ...grpc.CallOption,
	) (grpc.ClientStream, error) {
		return streamer(metadata.NewOutgoingContext(ctx, metadata.Join(outgoing, metadataFromOutgoing(ctx))), desc, cc, method, opts...)
	}
	return []grpc.DialOption{
		grpc.WithChainUnaryInterceptor(unary),
		grpc.WithChainStreamInterceptor(stream),
	}
}

// TLS builds dial credentials from a server CA bundle (PEM), optionally
// with a client certificate for mutual TLS (all PEM).
func TLS(serverCA []byte, clientCert, clientKey []byte) (credentials.TransportCredentials, error) {
	pool, err := certPool(serverCA)
	if err != nil {
		return nil, fmt.Errorf("tbep: the server CA bundle is invalid: %w", err)
	}
	config, err := tlsClientConfig(pool, clientCert, clientKey)
	if err != nil {
		return nil, err
	}
	return credentials.NewTLS(config), nil
}

// mutation builds the per-call context carrying one mutation's retry
// identity. Keys are validated client-side so a malformed key fails
// before the round-trip.
func mutation(ctx context.Context, key string) (context.Context, error) {
	if strings.TrimSpace(key) == "" {
		return nil, &EngineError{
			Code:   terrabasev1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING,
			Detail: "the mutation carries no idempotency key",
		}
	}
	if len(key) > MaxIdempotencyBytes {
		return nil, &EngineError{
			Code:   terrabasev1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING,
			Detail: "the idempotency key exceeds its bound",
		}
	}
	for i := 0; i < len(key); i++ {
		if key[i] < 0x20 || key[i] == 0x7f {
			return nil, &EngineError{
				Code:   terrabasev1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING,
				Detail: "the idempotency key carries control characters",
			}
		}
	}
	return metadata.AppendToOutgoingContext(ctx, IdempotencyMetadata, key), nil
}

// Bind validates the identity with the engine and answers the serving
// instance.
func (c *Client) Bind(ctx context.Context) (*terrabasev1.ChannelBindResponse, error) {
	response, err := c.channel.Bind(ctx, &terrabasev1.ChannelBindRequest{
		Principal: c.identity.Principal,
		Tenant:    c.identity.Tenant,
	})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReadDocument serves one typed document read.
func (c *Client) ReadDocument(
	ctx context.Context, request *terrabasev1.DocumentReadRequest,
) (*terrabasev1.DocumentReadResponse, error) {
	response, err := c.engine.DocumentRead(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// WriteDocument commits one document write under a retry identity.
func (c *Client) WriteDocument(
	ctx context.Context, request *terrabasev1.DocumentWriteRequest, idempotencyKey string,
) (*terrabasev1.DocumentWriteResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.engine.DocumentWrite(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// DeleteDocument commits one document delete under a retry identity.
func (c *Client) DeleteDocument(
	ctx context.Context, request *terrabasev1.DocumentDeleteRequest, idempotencyKey string,
) (*terrabasev1.DocumentDeleteResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.engine.DocumentDelete(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// BatchDocuments commits one atomic batch under a retry identity.
func (c *Client) BatchDocuments(
	ctx context.Context, request *terrabasev1.DocumentBatchRequest, idempotencyKey string,
) (*terrabasev1.DocumentBatchResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.engine.DocumentBatch(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ExecuteQuery serves one query execution.
func (c *Client) ExecuteQuery(
	ctx context.Context, request *terrabasev1.QueryExecuteRequest,
) (*terrabasev1.QueryExecuteResponse, error) {
	response, err := c.engine.QueryExecute(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// CancelQuery cancels one running query.
func (c *Client) CancelQuery(
	ctx context.Context, request *terrabasev1.QueryCancelRequest,
) (*terrabasev1.QueryCancelResponse, error) {
	response, err := c.engine.QueryCancel(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReadFamily serves one typed family read.
func (c *Client) ReadFamily(
	ctx context.Context, request *terrabasev1.FamilyReadRequest,
) (*terrabasev1.FamilyReadResponse, error) {
	response, err := c.engine.FamilyRead(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// WriteFamily commits one family write under a retry identity.
func (c *Client) WriteFamily(
	ctx context.Context, request *terrabasev1.FamilyWriteRequest, idempotencyKey string,
) (*terrabasev1.FamilyWriteResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.engine.FamilyWrite(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// Subscribe opens one backbone subscription stream. The first event is
// the confirmation; ordered batches, triggers, and the close frame
// follow. Acknowledgements ride Acknowledge on the same connection.
func (c *Client) Subscribe(
	ctx context.Context, request *terrabasev1.BackboneSubscribeRequest,
) (terrabasev1.Backbone_SubscribeClient, error) {
	stream, err := c.backbone.Subscribe(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return stream, nil
}

// Acknowledge records one subscription checkpoint acknowledgement.
func (c *Client) Acknowledge(
	ctx context.Context, request *terrabasev1.BackboneAcknowledgeRequest,
) (*terrabasev1.BackboneAcknowledgeResponse, error) {
	response, err := c.backbone.Acknowledge(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// Unsubscribe tears one subscription down.
func (c *Client) Unsubscribe(
	ctx context.Context, request *terrabasev1.BackboneUnsubscribeRequest,
) (*terrabasev1.BackboneUnsubscribeResponse, error) {
	response, err := c.backbone.Unsubscribe(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// SetupSession opens (or resumes) one sync session.
func (c *Client) SetupSession(
	ctx context.Context, request *terrabasev1.SyncSessionSetupRequest,
) (*terrabasev1.SyncSessionSetupResponse, error) {
	response, err := c.sync.SessionSetup(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// DeltaPull pulls one bounded delta page.
func (c *Client) DeltaPull(
	ctx context.Context, request *terrabasev1.SyncDeltaPullRequest,
) (*terrabasev1.SyncDeltaPullResponse, error) {
	response, err := c.sync.DeltaPull(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// SnapshotPull pulls one full snapshot.
func (c *Client) SnapshotPull(
	ctx context.Context, request *terrabasev1.SyncSnapshotPullRequest,
) (*terrabasev1.SyncSnapshotPullResponse, error) {
	response, err := c.sync.SnapshotPull(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// AckCheckpoint acknowledges one session checkpoint.
func (c *Client) AckCheckpoint(
	ctx context.Context, request *terrabasev1.SyncCheckpointAckRequest,
) (*terrabasev1.SyncCheckpointAckResponse, error) {
	response, err := c.sync.CheckpointAck(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// SubmitMutation submits one session mutation under a retry identity.
func (c *Client) SubmitMutation(
	ctx context.Context, request *terrabasev1.SyncMutationSubmitRequest, idempotencyKey string,
) (*terrabasev1.SyncMutationSubmitResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.sync.MutationSubmit(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// CloseSession closes one sync session.
func (c *Client) CloseSession(
	ctx context.Context, request *terrabasev1.SyncSessionCloseRequest,
) (*terrabasev1.SyncSessionCloseResponse, error) {
	response, err := c.sync.SessionClose(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// DeclareSchema declares one collection schema under a retry identity.
func (c *Client) DeclareSchema(
	ctx context.Context, request *terrabasev1.SchemaDeclareRequest, idempotencyKey string,
) (*terrabasev1.SchemaDeclareResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.schema.Declare(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// SchemaStatus reads one collection's schema status.
func (c *Client) SchemaStatus(
	ctx context.Context, request *terrabasev1.SchemaStatusRequest,
) (*terrabasev1.SchemaStatusResponse, error) {
	response, err := c.schema.Status(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ValidateSchema validates one document against the enforced schema.
func (c *Client) ValidateSchema(
	ctx context.Context, request *terrabasev1.SchemaValidateRequest,
) (*terrabasev1.SchemaValidateResponse, error) {
	response, err := c.schema.Validate(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// MigrationStatus reads one collection's migration state.
func (c *Client) MigrationStatus(
	ctx context.Context, request *terrabasev1.MigrationStatusRequest,
) (*terrabasev1.MigrationStatusResponse, error) {
	response, err := c.schema.MigrationStatus(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// MigrationDryRun plans one migration without mutating anything.
func (c *Client) MigrationDryRun(
	ctx context.Context, request *terrabasev1.MigrationDryRunRequest,
) (*terrabasev1.MigrationDryRunResponse, error) {
	response, err := c.schema.MigrationDryRun(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// MigrationApply applies one migration under a retry identity.
func (c *Client) MigrationApply(
	ctx context.Context, request *terrabasev1.MigrationApplyRequest, idempotencyKey string,
) (*terrabasev1.MigrationApplyResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.schema.MigrationApply(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReplicationConflicts observes resolved conflicts.
func (c *Client) ReplicationConflicts(
	ctx context.Context,
) (*terrabasev1.ReplicationConflictsResponse, error) {
	response, err := c.peers.Conflicts(ctx, &terrabasev1.ReplicationConflictsRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReplicationFenced observes fenced ranges.
func (c *Client) ReplicationFenced(
	ctx context.Context,
) (*terrabasev1.ReplicationFencedResponse, error) {
	response, err := c.peers.Fenced(ctx, &terrabasev1.ReplicationFencedRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReplicationDepositions observes leadership depositions.
func (c *Client) ReplicationDepositions(
	ctx context.Context,
) (*terrabasev1.ReplicationDepositionsResponse, error) {
	response, err := c.peers.Depositions(ctx, &terrabasev1.ReplicationDepositionsRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReplicationCursor observes the replication position.
func (c *Client) ReplicationCursor(
	ctx context.Context,
) (*terrabasev1.ReplicationCursorResponse, error) {
	response, err := c.peers.Cursor(ctx, &terrabasev1.ReplicationCursorRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReplicationHealth observes replication health.
func (c *Client) ReplicationHealth(
	ctx context.Context,
) (*terrabasev1.ReplicationHealthResponse, error) {
	response, err := c.peers.Health(ctx, &terrabasev1.ReplicationHealthRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ReplicationDenials observes cross-tenant denials.
func (c *Client) ReplicationDenials(
	ctx context.Context,
) (*terrabasev1.ReplicationDenialsResponse, error) {
	response, err := c.peers.Denials(ctx, &terrabasev1.ReplicationDenialsRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// Topology reads the serving topology.
func (c *Client) Topology(
	ctx context.Context,
) (*terrabasev1.TopologyReadResponse, error) {
	response, err := c.care.TopologyRead(ctx, &terrabasev1.TopologyReadRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// AdminStatus reads the engine's administrative status.
func (c *Client) AdminStatus(ctx context.Context) (*terrabasev1.AdminStatusResponse, error) {
	response, err := c.care.AdminStatus(ctx, &terrabasev1.AdminStatusRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// Health reads the supervision health.
func (c *Client) Health(ctx context.Context) (*terrabasev1.SupervisionHealthResponse, error) {
	response, err := c.care.SupervisionHealth(ctx, &terrabasev1.SupervisionHealthRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// Shutdown begins a graceful engine shutdown.
func (c *Client) Shutdown(
	ctx context.Context, reason terrabasev1.ShutdownReason,
) (*terrabasev1.SupervisionShutdownResponse, error) {
	response, err := c.care.SupervisionShutdown(ctx, &terrabasev1.SupervisionShutdownRequest{
		Reason: reason,
	})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ConfigureTier configures the Storage Tier Mover under a retry identity.
func (c *Client) ConfigureTier(
	ctx context.Context, request *terrabasev1.TierConfigureRequest, idempotencyKey string,
) (*terrabasev1.TierConfigureResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.tiers.Configure(ctx, request)
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// InspectTier reads the tier administration state.
func (c *Client) InspectTier(
	ctx context.Context,
) (*terrabasev1.TierInspectResponse, error) {
	response, err := c.tiers.Inspect(ctx, &terrabasev1.TierInspectRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// TierDryRun evaluates one tier move without moving anything.
func (c *Client) TierDryRun(ctx context.Context) (*terrabasev1.TierDryRunResponse, error) {
	response, err := c.tiers.DryRun(ctx, &terrabasev1.TierDryRunRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// TierExplain explains the next tier move.
func (c *Client) TierExplain(ctx context.Context) (*terrabasev1.TierExplainResponse, error) {
	response, err := c.tiers.Explain(ctx, &terrabasev1.TierExplainRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// RunTier executes one tier move under a retry identity.
func (c *Client) RunTier(
	ctx context.Context, idempotencyKey string,
) (*terrabasev1.TierRunResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.tiers.Run(ctx, &terrabasev1.TierRunRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// PauseTier pauses the tier mover under a retry identity.
func (c *Client) PauseTier(
	ctx context.Context, idempotencyKey string,
) (*terrabasev1.TierPauseResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.tiers.Pause(ctx, &terrabasev1.TierPauseRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}

// ResumeTier resumes the tier mover under a retry identity.
func (c *Client) ResumeTier(
	ctx context.Context, idempotencyKey string,
) (*terrabasev1.TierResumeResponse, error) {
	ctx, err := mutation(ctx, idempotencyKey)
	if err != nil {
		return nil, err
	}
	response, err := c.tiers.Resume(ctx, &terrabasev1.TierResumeRequest{})
	if err != nil {
		return nil, FromError(err)
	}
	return response, nil
}
