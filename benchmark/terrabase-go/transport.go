package terrabase

import (
	"context"
	"fmt"
	"net"
	"path/filepath"
	"strings"

	"google.golang.org/grpc"
	"google.golang.org/grpc/credentials/insecure"
	"google.golang.org/protobuf/proto"
)

// Transport owns one multiplexed gRPC connection. Create one Client per
// principal and tenant with Bind. Those logical clients share reconnects and
// HTTP/2 connection capacity without sharing mutable identity.
type Transport struct {
	conn         grpc.ClientConnInterface
	state        *connectionState
	defaultLevel ReadLevel
}

// DialTransport creates one shared production transport. The connection is
// lazy and reconnects according to gRPC's connection state machine.
func DialTransport(target string, options ...Option) (*Transport, error) {
	conn, config, err := dialConnection(target, options...)
	if err != nil {
		return nil, err
	}
	return &Transport{
		conn:         conn,
		state:        &connectionState{},
		defaultLevel: config.level,
	}, nil
}

// DialUnix opens a logical client over a local Unix domain socket. UDS is a
// plaintext, same-host development and process-isolated test lane. TLS and
// custom transport or dialer options are rejected so this helper cannot be
// mistaken for the production TCP path.
func DialUnix(path string, options ...Option) (*Client, error) {
	transport, err := DialUnixTransport(path, options...)
	if err != nil {
		return nil, err
	}
	client := transport.Client()
	client.ownsConn = true
	return client, nil
}

// DialUnixTransport creates a shared local Unix domain socket transport.
func DialUnixTransport(path string, options ...Option) (*Transport, error) {
	if path == "" || !filepath.IsAbs(path) || strings.IndexByte(path, 0) >= 0 {
		return nil, fmt.Errorf("terrabase: the Unix socket path must be a non-empty absolute path")
	}
	dialer := func(ctx context.Context, _ string) (net.Conn, error) {
		var network net.Dialer
		return network.DialContext(ctx, "unix", path)
	}
	unixOption := func(config *dialConfig) error {
		if config.tlsConfig != nil || config.transport != nil || config.dialer != nil {
			return fmt.Errorf("terrabase: Unix sockets cannot be combined with TLS, transport credentials, or another dialer")
		}
		config.transport = insecure.NewCredentials()
		config.dialer = dialer
		return nil
	}
	options = append(options, unixOption)
	return DialTransport("passthrough:///terrabase-unix", options...)
}

// Client returns a new unbound logical client on the shared connection. Close
// releases only that logical client. Close the Transport to release the
// underlying connection.
func (t *Transport) Client() *Client {
	if t == nil {
		return NewClient(nil)
	}
	client := newClient(t.conn, t.state)
	client.defaultLevel = t.defaultLevel
	return client
}

// Bind returns a new immutable per-identity client on this shared transport.
func (t *Transport) Bind(ctx context.Context, principal, tenant string) (*Client, Binding, error) {
	client := t.Client()
	if err := client.available(); err != nil {
		return nil, Binding{}, err
	}
	binding, err := client.Bind(ctx, principal, tenant)
	if err != nil {
		_ = client.Close()
		return nil, Binding{}, err
	}
	return client, binding, nil
}

// Close releases the shared connection and makes every child Client answer
// ErrClientClosed. It is safe to call concurrently and more than once.
func (t *Transport) Close() error {
	if t == nil || t.state == nil || !t.state.closed.CompareAndSwap(false, true) || t.conn == nil {
		return nil
	}
	closer, ok := t.conn.(interface{ Close() error })
	if !ok {
		return nil
	}
	return closer.Close()
}

// boundedClientConn applies the protocol's whole-message ceiling to clients
// created by both Dial and NewClient. Dial also configures the transport limit
// so an oversized response is rejected before allocation.
type boundedClientConn struct {
	grpc.ClientConnInterface
}

func (c boundedClientConn) Invoke(ctx context.Context, method string, request, response any, options ...grpc.CallOption) error {
	if err := checkMessageSize("request", request); err != nil {
		return err
	}
	options = append(options,
		grpc.MaxCallRecvMsgSize(maxFrameBytes),
		grpc.MaxCallSendMsgSize(maxFrameBytes),
	)
	if err := c.ClientConnInterface.Invoke(ctx, method, request, response, options...); err != nil {
		return err
	}
	return checkMessageSize("response", response)
}

func (c boundedClientConn) NewStream(ctx context.Context, descriptor *grpc.StreamDesc, method string, options ...grpc.CallOption) (grpc.ClientStream, error) {
	options = append(options,
		grpc.MaxCallRecvMsgSize(maxFrameBytes),
		grpc.MaxCallSendMsgSize(maxFrameBytes),
	)
	stream, err := c.ClientConnInterface.NewStream(ctx, descriptor, method, options...)
	if err != nil {
		return nil, err
	}
	return boundedClientStream{ClientStream: stream}, nil
}

type boundedClientStream struct {
	grpc.ClientStream
}

func (s boundedClientStream) SendMsg(message any) error {
	if err := checkMessageSize("request", message); err != nil {
		return err
	}
	return s.ClientStream.SendMsg(message)
}

func (s boundedClientStream) RecvMsg(message any) error {
	if err := s.ClientStream.RecvMsg(message); err != nil {
		return err
	}
	return checkMessageSize("response", message)
}

func checkMessageSize(direction string, message any) error {
	typed, ok := message.(proto.Message)
	if !ok || typed == nil {
		return nil
	}
	size := proto.Size(typed)
	if size <= maxFrameBytes {
		return nil
	}
	code := CodeRequestTooLarge
	if direction == "response" {
		code = CodeEngineFailure
	}
	return &Error{
		Code:   code,
		Detail: fmt.Sprintf("the %s is %d bytes; the protocol limit is %d", direction, size, maxFrameBytes),
	}
}
