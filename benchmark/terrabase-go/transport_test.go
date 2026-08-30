package terrabase

import (
	"context"
	"errors"
	"net"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/grpc"
	"google.golang.org/grpc/codes"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/grpc/status"
	"google.golang.org/grpc/test/bufconn"

	v1 "terrascale.tech/terrabase-go/terrabase/v1"
)

func startFakeTransport(t *testing.T, options ...Option) (*Transport, *fakeTBEP) {
	t.Helper()
	fake := newFakeTBEP()
	listener := bufconn.Listen(1 << 20)
	server := grpc.NewServer()
	fake.register(server)
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(server.Stop)
	dialer := func(ctx context.Context, _ string) (net.Conn, error) {
		return listener.DialContext(ctx)
	}
	options = append(options, WithInsecure(), WithContextDialer(dialer), WithAuthority("localhost"))
	transport, err := DialTransport("passthrough:///bufnet", options...)
	if err != nil {
		t.Fatalf("the shared transport could not be dialed: %v", err)
	}
	t.Cleanup(func() { _ = transport.Close() })
	return transport, fake
}

func TestSharedTransportKeepsIdentityImmutable(t *testing.T) {
	transport, fake := startFakeTransport(t)
	alpha, alphaBinding, err := transport.Bind(context.Background(), "orders-api", "tenant-a")
	if err != nil {
		t.Fatalf("tenant-a could not bind: %v", err)
	}
	beta, betaBinding, err := transport.Bind(context.Background(), "analytics-api", "tenant-b")
	if err != nil {
		t.Fatalf("tenant-b could not bind: %v", err)
	}
	if alphaBinding.Tenant != "tenant-a" || betaBinding.Tenant != "tenant-b" {
		t.Fatalf("the bindings are wrong: alpha=%+v beta=%+v", alphaBinding, betaBinding)
	}
	if _, err := alpha.Bind(context.Background(), "attacker", "tenant-b"); !errors.Is(err, ErrAlreadyBound) {
		t.Fatalf("a logical client must not be rebound, got %v", err)
	}

	if _, _, err := alpha.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault); err != nil {
		t.Fatalf("tenant-a read failed: %v", err)
	}
	if tenant := fake.lastTenant(); tenant != "tenant-a" {
		t.Fatalf("tenant-a identity did not ride the shared connection, got %q", tenant)
	}
	if _, _, err := beta.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault); err != nil {
		t.Fatalf("tenant-b read failed: %v", err)
	}
	if tenant := fake.lastTenant(); tenant != "tenant-b" {
		t.Fatalf("tenant-b identity did not ride the shared connection, got %q", tenant)
	}

	var wait sync.WaitGroup
	failures := make(chan error, 32)
	for _, client := range []*Client{alpha, beta} {
		for index := 0; index < 16; index++ {
			wait.Add(1)
			go func(client *Client) {
				defer wait.Done()
				_, _, callErr := client.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault)
				failures <- callErr
			}(client)
		}
	}
	wait.Wait()
	close(failures)
	for callErr := range failures {
		if callErr != nil {
			t.Fatalf("a concurrent shared-transport read failed: %v", callErr)
		}
	}

	if err := alpha.Close(); err != nil {
		t.Fatalf("the logical close failed: %v", err)
	}
	if _, _, err := alpha.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault); !errors.Is(err, ErrClientClosed) {
		t.Fatalf("a closed logical client must answer ErrClientClosed, got %v", err)
	}
	if _, _, err := beta.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault); err != nil {
		t.Fatalf("closing one logical client must not close the shared transport: %v", err)
	}
	if err := transport.Close(); err != nil {
		t.Fatalf("the shared transport close failed: %v", err)
	}
	if err := transport.Close(); err != nil {
		t.Fatalf("a repeated shared transport close failed: %v", err)
	}
	if _, _, err := beta.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault); !errors.Is(err, ErrClientClosed) {
		t.Fatalf("a child of a closed transport must answer ErrClientClosed, got %v", err)
	}
}

type closeCountingConn struct {
	grpc.ClientConnInterface
	closes atomic.Int32
}

func (c *closeCountingConn) Close() error {
	c.closes.Add(1)
	return nil
}

func TestNewClientDoesNotOwnInjectedConnection(t *testing.T) {
	conn := &closeCountingConn{}
	client := NewClient(conn)
	if err := client.Close(); err != nil {
		t.Fatalf("the logical client close failed: %v", err)
	}
	if closes := conn.closes.Load(); closes != 0 {
		t.Fatalf("NewClient closed a caller-owned connection %d times", closes)
	}
	if err := client.Close(); err != nil {
		t.Fatalf("the repeated logical close failed: %v", err)
	}
	if _, err := NewClient(nil).Bind(context.Background(), "principal", "tenant"); !errors.Is(err, ErrClientClosed) {
		t.Fatalf("a nil injected connection must fail without panicking, got %v", err)
	}
}

func TestUnixSocketInterop(t *testing.T) {
	path := filepath.Join(t.TempDir(), "terrabase.sock")
	listener, err := net.Listen("unix", path)
	if err != nil {
		t.Fatalf("the Unix socket could not listen: %v", err)
	}
	t.Cleanup(func() { _ = listener.Close() })
	fake := newFakeTBEP()
	server := grpc.NewServer()
	fake.register(server)
	go func() { _ = server.Serve(listener) }()
	t.Cleanup(server.Stop)

	client, err := DialUnix(path)
	if err != nil {
		t.Fatalf("the Unix client could not be dialed: %v", err)
	}
	t.Cleanup(func() { _ = client.Close() })
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if _, err := client.Bind(ctx, "local-tool", "tenant-local"); err != nil {
		t.Fatalf("the Unix client could not bind: %v", err)
	}
	if _, _, err := client.DocumentRead(ctx, "books", "doc", ReadLevelDefault); err != nil {
		t.Fatalf("the Unix document read failed: %v", err)
	}
	if tenant := fake.lastTenant(); tenant != "tenant-local" {
		t.Fatalf("the Unix RPC carried the wrong tenant %q", tenant)
	}

	if _, err := DialUnix("relative.sock"); err == nil {
		t.Fatal("a relative Unix socket path must be refused")
	}
	if _, err := DialUnix(path, WithInsecure()); err == nil {
		t.Fatal("the explicit Unix helper must reject a second transport configuration")
	}
}

func TestTLS13AndInstrumentationOptions(t *testing.T) {
	config := dialConfig{}
	if version := config.ensureTLS().MinVersion; version != 0x0304 {
		t.Fatalf("the client TLS minimum is %#x, want TLS 1.3", version)
	}
	if _, err := Dial("", WithInsecure()); err == nil {
		t.Fatal("an empty target must be refused")
	}
	if _, err := Dial("localhost:1", nil); err == nil {
		t.Fatal("a nil dial option must be refused")
	}
	if _, err := Dial("localhost:1", WithUnaryInterceptor(nil)); err == nil {
		t.Fatal("a nil unary interceptor must be refused")
	}
	if _, err := Dial("localhost:1", WithStreamInterceptor(nil)); err == nil {
		t.Fatal("a nil stream interceptor must be refused")
	}
	if _, err := Dial("localhost:1", WithStatsHandler(nil)); err == nil {
		t.Fatal("a nil stats handler must be refused")
	}

	var unaryCalls atomic.Int32
	var streamCalls atomic.Int32
	transport, _ := startFakeTransport(t,
		WithUnaryInterceptor(func(ctx context.Context, method string, request, response any, conn *grpc.ClientConn, invoke grpc.UnaryInvoker, options ...grpc.CallOption) error {
			unaryCalls.Add(1)
			return invoke(ctx, method, request, response, conn, options...)
		}),
		WithStreamInterceptor(func(ctx context.Context, descriptor *grpc.StreamDesc, conn *grpc.ClientConn, method string, streamer grpc.Streamer, options ...grpc.CallOption) (grpc.ClientStream, error) {
			streamCalls.Add(1)
			return streamer(ctx, descriptor, conn, method, options...)
		}),
	)
	client, _, err := transport.Bind(context.Background(), "orders-api", "tenant-a")
	if err != nil {
		t.Fatalf("the instrumented client could not bind: %v", err)
	}
	if _, _, err := client.DocumentRead(context.Background(), "books", "doc", ReadLevelDefault); err != nil {
		t.Fatalf("the instrumented read failed: %v", err)
	}
	subscription, err := client.Subscribe(context.Background(), 1, []string{"books"}, FromLive())
	if err != nil {
		t.Fatalf("the instrumented stream could not open: %v", err)
	}
	_ = subscription.Close()
	if unaryCalls.Load() < 2 || streamCalls.Load() != 1 {
		t.Fatalf("the interceptors did not observe the calls: unary=%d stream=%d", unaryCalls.Load(), streamCalls.Load())
	}
}

func TestInsecureTransportIsLocalOnly(t *testing.T) {
	for _, target := range []string{"engine.example:443", "dns:///engine.example:443", "192.0.2.10:443"} {
		if _, err := Dial(target, WithInsecure()); err == nil {
			t.Fatalf("plaintext transport must refuse non-loopback target %q", target)
		}
		if _, err := Dial(target, WithTransportCredentials(insecure.NewCredentials())); err == nil {
			t.Fatalf("direct insecure credentials must refuse non-loopback target %q", target)
		}
	}
	for _, target := range []string{"localhost:1", "127.0.0.1:1", "[::1]:1"} {
		client, err := Dial(target, WithInsecure())
		if err != nil {
			t.Fatalf("loopback plaintext target %q was refused: %v", target, err)
		}
		_ = client.Close()
	}
	if _, err := Dial("passthrough:///bufnet", WithInsecure()); err == nil {
		t.Fatal("an in-memory target without a custom dialer must be refused")
	}
}

func TestCancellationAndDeadlineRemainInspectable(t *testing.T) {
	client, _ := startFake(t)
	mustBind(t, client)

	canceled, cancel := context.WithCancel(context.Background())
	cancel()
	_, _, err := client.DocumentRead(canceled, "books", "doc", ReadLevelDefault)
	if !errors.Is(err, context.Canceled) || status.Code(err) != codes.Canceled {
		t.Fatalf("cancellation lost its context or gRPC identity: %v", err)
	}

	deadline, stop := context.WithDeadline(context.Background(), time.Now().Add(-time.Second))
	defer stop()
	_, _, err = client.DocumentRead(deadline, "books", "doc", ReadLevelDefault)
	if !errors.Is(err, context.DeadlineExceeded) || status.Code(err) != codes.DeadlineExceeded {
		t.Fatalf("deadline loss its context or gRPC identity: %v", err)
	}
}

func TestWholeMessageLimit(t *testing.T) {
	request := &v1.FamilyWriteRequest{Request: make([]byte, maxFrameBytes+1)}
	if err := checkMessageSize("request", request); !errors.Is(err, ErrRequestTooLarge) {
		t.Fatalf("an oversized request must answer ErrRequestTooLarge, got %v", err)
	}
	response := &v1.FamilyReadResponse{Response: make([]byte, maxFrameBytes+1)}
	if err := checkMessageSize("response", response); !errors.Is(err, ErrEngineFailure) {
		t.Fatalf("an oversized response must answer ErrEngineFailure, got %v", err)
	}
	if err := checkMessageSize("request", &v1.FamilyWriteRequest{Request: []byte(strings.Repeat("x", 8))}); err != nil {
		t.Fatalf("a bounded request was refused: %v", err)
	}
}
