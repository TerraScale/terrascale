package repllocal

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/url"
	"os"
	"strconv"
	"sync"
	"sync/atomic"
	"syscall"
)

type WireProxy struct {
	listener       net.Listener
	ctx            context.Context
	cancel         context.CancelFunc
	target         atomic.Pointer[string]
	upload         atomic.Int64
	download       atomic.Int64
	wg             sync.WaitGroup
	stopped        chan struct{}
	mu             sync.Mutex
	active         map[net.Conn]struct{}
	err            error
	quiescing      bool
	shutdownResets int
}

func StartWireProxy(ctx context.Context) (*WireProxy, error) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithCancel(ctx)
	p := &WireProxy{listener: listener, ctx: ctx, cancel: cancel, active: make(map[net.Conn]struct{}), stopped: make(chan struct{})}
	context.AfterFunc(ctx, func() {
		defer close(p.stopped)
		p.mu.Lock()
		defer p.mu.Unlock()
		listener.Close()
		for conn := range p.active {
			conn.Close()
		}
	})
	p.wg.Go(func() {
		for {
			conn, err := listener.Accept()
			if err != nil {
				p.record(err)
				cancel()
				return
			}
			target := p.target.Load()
			if p.track(conn) {
				p.wg.Go(func() { p.forward(conn, target) })
			}
		}
	})
	return p, nil
}

func (p *WireProxy) URL() string { return "http://" + p.listener.Addr().String() }

// Target changes apply to subsequent connections; existing streams retain their original peer.
func (p *WireProxy) Target(rawURL string) error {
	u, err := url.Parse(rawURL)
	if err != nil || u.Scheme != "http" || u.User != nil || u.RawQuery != "" || u.Fragment != "" || (u.Path != "" && u.Path != "/") {
		return errors.New("peer target must be an HTTP loopback origin")
	}
	if ip := net.ParseIP(u.Hostname()); ip == nil || !ip.IsLoopback() {
		return errors.New("peer target must use a loopback IP")
	}
	port := u.Port()
	if port == "" {
		port = "80"
	}
	if n, err := strconv.Atoi(port); err != nil || n < 1 || n > 65535 {
		return errors.New("invalid peer target port")
	}
	addr := net.JoinHostPort(u.Hostname(), port)
	if current := p.target.Load(); current != nil && *current == addr {
		return nil
	}
	p.target.Store(&addr)
	return nil
}

// Suspend disconnects an intentionally stopped worker; Target resumes forwarding after restart.
func (p *WireProxy) Suspend() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.target.Store(nil)
	for conn := range p.active {
		conn.Close()
	}
}

// Bytes counts transport stream bytes, including HTTP/2 control traffic, without TCP/IP headers.
func (p *WireProxy) Bytes() int64 { return p.upload.Load() + p.download.Load() }

func (p *WireProxy) Close() error {
	p.cancel()
	<-p.stopped
	p.wg.Wait()
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.err
}

func (p *WireProxy) record(err error) {
	if err != nil && p.ctx.Err() == nil {
		p.mu.Lock()
		defer p.mu.Unlock()
		if p.err == nil {
			p.err = err
		}
	}
}

// beginQuiescence keeps forwarding available while workers intentionally cancel their channels.
func (p *WireProxy) beginQuiescence() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.quiescing = true
}

func (p *WireProxy) endQuiescence() {
	p.mu.Lock()
	defer p.mu.Unlock()
	p.quiescing = false
}

func (p *WireProxy) recordForward(err error, target *string, uploadComplete bool) {
	p.mu.Lock()
	defer p.mu.Unlock()
	if err == nil || p.ctx.Err() != nil || (p.target.Load() != target && !p.quiescing) {
		return
	}
	// After upload EOF, an intentionally stopped gRPC channel can reset the response half.
	if p.quiescing && uploadComplete && errors.Is(err, syscall.ECONNRESET) {
		p.shutdownResets++
		fmt.Fprintf(os.Stderr, "benchmark expected-shutdown-reset count=%d: %v\n", p.shutdownResets, err)
		return
	}
	if p.err == nil {
		p.err = err
	}
}

func (p *WireProxy) track(conn net.Conn) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.ctx.Err() != nil {
		conn.Close()
		return false
	}
	p.active[conn] = struct{}{}
	return true
}

func (p *WireProxy) release(conn net.Conn) {
	conn.Close()
	p.mu.Lock()
	delete(p.active, conn)
	p.mu.Unlock()
}

func (p *WireProxy) forward(client net.Conn, target *string) {
	defer p.release(client)
	if target == nil || p.target.Load() != target {
		return // The directory can advertise the proxy before its worker is ready.
	}
	peer, err := (&net.Dialer{}).DialContext(p.ctx, "tcp", *target)
	if err != nil {
		p.recordForward(fmt.Errorf("peer dial: %w", err), target, false)
		return
	}
	if !p.track(peer) {
		return
	}
	defer p.release(peer)
	if p.target.Load() != target {
		return
	}
	var streams sync.WaitGroup
	var uploadComplete atomic.Bool
	copyStream := func(dst, src net.Conn, count *atomic.Int64, upload bool) {
		// Hide TCP's ReaderFrom/WriterTo methods to keep buffers and accounting explicit.
		_, err := io.CopyBuffer(wireWriter{dst, count}, struct{ io.Reader }{src}, make([]byte, 32*1024))
		if err == nil {
			if upload {
				uploadComplete.Store(true)
			}
			err = dst.(*net.TCPConn).CloseWrite()
			// A peer that already closed can leave its socket disconnected.
			if errors.Is(err, syscall.ENOTCONN) {
				err = nil
			}
		}
		if err != nil {
			p.recordForward(fmt.Errorf("peer stream: %w", err), target, !upload && uploadComplete.Load())
			client.Close()
			peer.Close()
		}
	}
	streams.Go(func() { copyStream(peer, client, &p.upload, true) })
	streams.Go(func() { copyStream(client, peer, &p.download, false) })
	streams.Wait()
}

type wireWriter struct {
	io.Writer
	count *atomic.Int64
}

func (w wireWriter) Write(b []byte) (int, error) {
	n, err := w.Writer.Write(b)
	w.count.Add(int64(n))
	return n, err
}
