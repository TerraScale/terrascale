package tbep

import (
	"context"
	"net"
	"testing"

	terrabasev1 "git.terrascale.tech/TerraScale/TerraBase/go/tbep/gen/terrabase/v1"
	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/metadata"
	"google.golang.org/grpc/status"
)

// The fixture engine: one bound instance, one document, typed refusals
// with the wire error-detail contract, and a metadata probe.
type fixtureEngine struct {
	terrabasev1.UnimplementedChannelServer
	terrabasev1.UnimplementedEngineServer
	terrabasev1.UnimplementedBackboneServer
	terrabasev1.UnimplementedSyncServer
	terrabasev1.UnimplementedSchemaServer
	terrabasev1.UnimplementedReplicationServer
	terrabasev1.UnimplementedMaintenanceServer
	terrabasev1.UnimplementedTierServer

	lastIdentity metadata.MD
}

func refused(kind terrabasev1.ErrorCode, detail string) error {
	refusal, err := status.New(codes.FailedPrecondition, detail).WithDetails(
		&terrabasev1.TbepError{Code: kind, Detail: detail},
	)
	if err != nil {
		panic(err)
	}
	return refusal.Err()
}

func (f *fixtureEngine) Bind(
	ctx context.Context, request *terrabasev1.ChannelBindRequest,
) (*terrabasev1.ChannelBindResponse, error) {
	identity, _ := metadata.FromIncomingContext(ctx)
	f.lastIdentity = identity
	return &terrabasev1.ChannelBindResponse{Tenant: request.Tenant, ServingInstance: 7}, nil
}

func (f *fixtureEngine) DocumentRead(
	ctx context.Context, request *terrabasev1.DocumentReadRequest,
) (*terrabasev1.DocumentReadResponse, error) {
	if request.Collection == "refused" {
		return nil, refused(terrabasev1.ErrorCode_ERROR_CODE_OPERATION_REFUSED, "the fixture refuses")
	}
	body, err := ValueFromJSON([]byte(`{"total": 21}`))
	if err != nil {
		return nil, err
	}
	return &terrabasev1.DocumentReadResponse{
		Found: true,
		Document: &terrabasev1.DocumentBody{
			Id: request.Document, Collection: request.Collection, Version: 3, Body: body,
		},
		Position: 9,
	}, nil
}

func (f *fixtureEngine) DocumentWrite(
	ctx context.Context, request *terrabasev1.DocumentWriteRequest,
) (*terrabasev1.DocumentWriteResponse, error) {
	if len(metadata.ValueFromIncomingContext(ctx, IdempotencyMetadata)) == 0 {
		return nil, refused(
			terrabasev1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING,
			"the mutation carries no key",
		)
	}
	return &terrabasev1.DocumentWriteResponse{
		Outcome: &terrabasev1.WriteOutcome{
			Outcome: &terrabasev1.WriteOutcome_Committed{
				Committed: &terrabasev1.CommitReceipt{
					Sequence: 4, Version: 1, OperationId: request.OperationId,
					Acknowledgement: terrabasev1.Acknowledgement_ACK_REGIONAL_QUORUM,
					Backup:          terrabasev1.BackupOutcome_BACKUP_FULL,
				},
			},
		},
	}, nil
}

func startFixture(t *testing.T) (*Client, *fixtureEngine) {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("the fixture listener binds: %v", err)
	}
	server := grpc.NewServer()
	fixture := &fixtureEngine{}
	terrabasev1.RegisterChannelServer(server, fixture)
	terrabasev1.RegisterEngineServer(server, fixture)
	terrabasev1.RegisterBackboneServer(server, fixture)
	terrabasev1.RegisterSyncServer(server, fixture)
	terrabasev1.RegisterSchemaServer(server, fixture)
	terrabasev1.RegisterReplicationServer(server, fixture)
	terrabasev1.RegisterMaintenanceServer(server, fixture)
	terrabasev1.RegisterTierServer(server, fixture)
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(server.Stop)

	client, err := Dial(
		listener.Addr().String(),
		Identity{Principal: "operator", Tenant: "tenant-a"},
		WithGRPCDialOptions(grpc.WithTransportCredentials(insecure.NewCredentials())),
	)
	if err != nil {
		t.Fatalf("the client dials: %v", err)
	}
	t.Cleanup(func() { _ = client.Close() })
	return client, fixture
}

func TestBindAnswersTheServingInstance(t *testing.T) {
	client, _ := startFixture(t)
	bound, err := client.Bind(context.Background())
	if err != nil {
		t.Fatalf("the bind answers: %v", err)
	}
	if bound.ServingInstance != 7 || bound.Tenant != "tenant-a" {
		t.Fatalf("unexpected bind answer: %+v", bound)
	}
}

func TestTheIdentityRidesEveryCall(t *testing.T) {
	client, fixture := startFixture(t)
	if _, err := client.Bind(context.Background()); err != nil {
		t.Fatalf("the bind answers: %v", err)
	}
	if got := fixture.lastIdentity.Get(PrincipalMetadata); len(got) != 1 || got[0] != "operator" {
		t.Fatalf("the principal metadata did not ride: %v", fixture.lastIdentity)
	}
	if got := fixture.lastIdentity.Get(TenantMetadata); len(got) != 1 || got[0] != "tenant-a" {
		t.Fatalf("the tenant metadata did not ride: %v", fixture.lastIdentity)
	}
}

func TestDocumentRoundTrip(t *testing.T) {
	client, _ := startFixture(t)
	body, err := ValueFromJSON([]byte(`{"total": 21}`))
	if err != nil {
		t.Fatalf("the body parses: %v", err)
	}
	written, err := client.WriteDocument(context.Background(), &terrabasev1.DocumentWriteRequest{
		Document: &terrabasev1.DocumentPut{Id: "d-1", Collection: "orders", Body: body},
		Expected: &terrabasev1.ExpectedVersion{
			Discipline: &terrabasev1.ExpectedVersion_Any{Any: &terrabasev1.ExpectedAny{}},
		},
		OperationId: "op-1",
	}, "op-1")
	if err != nil {
		t.Fatalf("the write commits: %v", err)
	}
	committed := written.GetOutcome().GetCommitted()
	if committed == nil || committed.Sequence != 4 {
		t.Fatalf("unexpected write outcome: %+v", written.GetOutcome())
	}

	read, err := client.ReadDocument(context.Background(), &terrabasev1.DocumentReadRequest{
		Collection: "orders", Document: "d-1",
	})
	if err != nil {
		t.Fatalf("the read answers: %v", err)
	}
	if !read.Found || read.Document.Version != 3 {
		t.Fatalf("unexpected read answer: %+v", read)
	}
	encoded, err := JSONFromValue(read.Document.Body)
	if err != nil {
		t.Fatalf("the body encodes: %v", err)
	}
	if string(encoded) != `{"total":21}` {
		t.Fatalf("unexpected body: %s", encoded)
	}
}

func TestRefusalsMapOntoEngineErrorCodes(t *testing.T) {
	client, _ := startFixture(t)
	_, err := client.ReadDocument(context.Background(), &terrabasev1.DocumentReadRequest{
		Collection: "refused", Document: "d-1",
	})
	engine, ok := err.(*EngineError)
	if !ok {
		t.Fatalf("the refusal maps onto an engine error, got %T: %v", err, err)
	}
	if !engine.IsRefusal(terrabasev1.ErrorCode_ERROR_CODE_OPERATION_REFUSED) {
		t.Fatalf("unexpected code: %v", engine.Code)
	}
	if engine.Detail != "the fixture refuses" {
		t.Fatalf("unexpected detail: %q", engine.Detail)
	}
}

func TestMutationsRefuseAKeylessRetryIdentity(t *testing.T) {
	client, _ := startFixture(t)
	_, err := client.WriteDocument(context.Background(), &terrabasev1.DocumentWriteRequest{
		Document: &terrabasev1.DocumentPut{Id: "d-1", Collection: "orders"},
		Expected: &terrabasev1.ExpectedVersion{
			Discipline: &terrabasev1.ExpectedVersion_Any{Any: &terrabasev1.ExpectedAny{}},
		},
		OperationId: "op-1",
	}, " ")
	engine, ok := err.(*EngineError)
	if !ok {
		t.Fatalf("the keyless mutation refuses client-side, got %T: %v", err, err)
	}
	if !engine.IsRefusal(terrabasev1.ErrorCode_ERROR_CODE_IDEMPOTENCY_PAIRING) {
		t.Fatalf("unexpected code: %v", engine.Code)
	}
}

func TestValueJSONConversionKeepsIntegersAndCanonicalObjects(t *testing.T) {
	value, err := ValueFromJSON([]byte(`{"z": 1, "a": 0.5, "m": [true, null, "x"]}`))
	if err != nil {
		t.Fatalf("the document parses: %v", err)
	}
	if value.GetMap().Entries[0].Name != "a" {
		t.Fatalf("objects canonicalize to ascending names: %+v", value.GetMap().Entries)
	}
	if value.GetMap().Entries[2].GetValue().GetInteger() != 1 {
		t.Fatalf("integers stay integers: %+v", value.GetMap().Entries[2])
	}
	if value.GetMap().Entries[0].GetValue().GetNumber() != 0.5 {
		t.Fatalf("doubles stay doubles: %+v", value.GetMap().Entries[0])
	}
	encoded, err := JSONFromValue(value)
	if err != nil {
		t.Fatalf("the value encodes: %v", err)
	}
	if string(encoded) != `{"a":0.5,"m":[true,null,"x"],"z":1}` {
		t.Fatalf("unexpected encoding: %s", encoded)
	}
}
