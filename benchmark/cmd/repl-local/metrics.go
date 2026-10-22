package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"math"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
)

type snapshot struct {
	CPUms, Allocated, FeedReads, MergedGroups float64
	Raw                                       string
}

// Snapshots are cumulative. Callers must reject falling counters and compare only within one PID.
func scrape(ctx context.Context, endpoint string) (snapshot, error) {
	var s snapshot
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, endpoint, nil)
	if err != nil {
		return s, err
	}
	// Request legacy text so exporter name normalization is deterministic.
	req.Header.Set("Accept", "text/plain; version=0.0.4")
	resp, err := http.DefaultClient.Do(req)
	if err != nil {
		return s, err
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	s.Raw = string(body)
	if err != nil {
		return s, err
	}
	if resp.StatusCode != http.StatusOK {
		return s, fmt.Errorf("metrics HTTP status %d", resp.StatusCode)
	}
	// OTel normalizes dots, appends the UCUM unit, then appends _total for counters.
	names := []string{"dotnet_process_cpu_time_seconds_total", "dotnet_gc_heap_total_allocated_bytes_total", "terrabase_storage_reads_total", "terrabase_replication_merged_groups_total"}
	values := []*float64{&s.CPUms, &s.Allocated, &s.FeedReads, &s.MergedGroups}
	var appeared, found [4]bool
	for line := range strings.SplitSeq(s.Raw, "\n") {
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		if strings.HasPrefix(line, "#") {
			fields := strings.Fields(line)
			if len(fields) >= 3 && (fields[1] == "TYPE" || fields[1] == "HELP") {
				for i, name := range names {
					if fields[2] == name {
						appeared[i] = true
					}
				}
			}
			continue
		}
		end := strings.IndexAny(line, "{ \t")
		if end < 0 {
			end = len(line)
		}
		for i, name := range names {
			if line[:end] != name {
				continue
			}
			appeared[i] = true
			path, value, err := metricSample(line[end:])
			if err != nil {
				return s, fmt.Errorf("%s: %w", name, err)
			}
			if i == 2 && path != "feed" {
				if path == "" {
					return s, fmt.Errorf("%s: missing path label", name)
				}
				found[i] = true // A known storage instrument with no feed series has made zero feed reads.
				continue
			}
			found[i] = true
			*values[i] += value
		}
	}
	for i, name := range names {
		if !found[i] && (i < 2 || appeared[i]) {
			return s, fmt.Errorf("missing metric sample %s", name)
		}
	}
	s.CPUms *= 1000
	return s, nil
}

func metricSample(rest string) (string, float64, error) {
	path := ""
	if strings.HasPrefix(rest, "{") {
		rest = rest[1:]
		for !strings.HasPrefix(strings.TrimSpace(rest), "}") {
			key, tail, ok := strings.Cut(rest, "=")
			if !ok {
				return "", 0, errors.New("invalid labels")
			}
			tail = strings.TrimSpace(tail)
			quoted, err := strconv.QuotedPrefix(tail)
			if err != nil {
				return "", 0, err
			}
			value, err := strconv.Unquote(quoted)
			if err != nil {
				return "", 0, err
			}
			if strings.TrimSpace(key) == "path" {
				path = value
			}
			rest = strings.TrimSpace(tail[len(quoted):])
			if strings.HasPrefix(rest, ",") {
				rest = rest[1:]
			} else if !strings.HasPrefix(rest, "}") {
				return "", 0, errors.New("invalid label separator")
			}
		}
		rest = strings.TrimSpace(rest)[1:]
	}
	fields := strings.Fields(rest)
	if len(fields) == 0 {
		return "", 0, errors.New("missing value")
	}
	v, err := strconv.ParseFloat(fields[0], 64)
	if err != nil || math.IsNaN(v) || math.IsInf(v, 0) || v < 0 {
		return "", 0, errors.New("invalid cumulative value")
	}
	return path, v, nil
}

type wireProxy struct {
	listener net.Listener
	ctx      context.Context
	cancel   context.CancelFunc
	target   atomic.Pointer[string]
	upload   atomic.Int64
	download atomic.Int64
	wg       sync.WaitGroup
	stopped  chan struct{}
	mu       sync.Mutex
	active   map[net.Conn]struct{}
	err      error
}

func startWireProxy(ctx context.Context) (*wireProxy, error) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithCancel(ctx)
	p := &wireProxy{listener: listener, ctx: ctx, cancel: cancel, active: make(map[net.Conn]struct{}), stopped: make(chan struct{})}
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
			if p.track(conn) {
				p.wg.Go(func() { p.forward(conn) })
			}
		}
	})
	return p, nil
}

func (p *wireProxy) URL() string { return "http://" + p.listener.Addr().String() }

// Target changes apply to subsequent connections; existing streams retain their original peer.
func (p *wireProxy) Target(rawURL string) error {
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
	p.target.Store(&addr)
	return nil
}

// Bytes counts transport stream bytes, including HTTP/2 control traffic, without TCP/IP headers.
func (p *wireProxy) Bytes() int64 { return p.upload.Load() + p.download.Load() }

func (p *wireProxy) Close() error {
	p.cancel()
	<-p.stopped
	p.wg.Wait()
	p.mu.Lock()
	defer p.mu.Unlock()
	return p.err
}

func (p *wireProxy) record(err error) {
	if err != nil && p.ctx.Err() == nil {
		p.mu.Lock()
		defer p.mu.Unlock()
		if p.err == nil {
			p.err = err
		}
	}
}

func (p *wireProxy) track(conn net.Conn) bool {
	p.mu.Lock()
	defer p.mu.Unlock()
	if p.ctx.Err() != nil {
		conn.Close()
		return false
	}
	p.active[conn] = struct{}{}
	return true
}

func (p *wireProxy) release(conn net.Conn) {
	conn.Close()
	p.mu.Lock()
	delete(p.active, conn)
	p.mu.Unlock()
}

func (p *wireProxy) forward(client net.Conn) {
	defer p.release(client)
	target := p.target.Load()
	if target == nil {
		return // The directory can advertise the proxy before its worker is ready.
	}
	peer, err := (&net.Dialer{}).DialContext(p.ctx, "tcp", *target)
	if err != nil {
		p.record(fmt.Errorf("peer dial: %w", err))
		return
	}
	if !p.track(peer) {
		return
	}
	defer p.release(peer)
	var streams sync.WaitGroup
	copyStream := func(dst, src net.Conn, count *atomic.Int64) {
		// Hide TCP's ReaderFrom/WriterTo methods to keep buffers and accounting explicit.
		_, err := io.CopyBuffer(wireWriter{dst, count}, struct{ io.Reader }{src}, make([]byte, 32*1024))
		if err == nil {
			err = dst.(*net.TCPConn).CloseWrite()
		}
		if err != nil {
			p.record(fmt.Errorf("peer stream: %w", err))
			client.Close()
			peer.Close()
		}
	}
	streams.Go(func() { copyStream(peer, client, &p.upload) })
	streams.Go(func() { copyStream(client, peer, &p.download) })
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
