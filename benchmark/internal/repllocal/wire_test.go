package repllocal

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"testing"
	"time"
)

func TestWireProxyAccountingHalfCloseAndRetarget(t *testing.T) {
	t.Parallel()
	p, err := StartWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	request := bytes.Repeat([]byte("request"), 20_000)
	var want int64
	for _, suffix := range []string{"first", "restarted"} {
		response := append(bytes.Repeat([]byte("reply"), 20_000), suffix...)
		target := wireTestPeer(t, func(conn net.Conn) error {
			got, err := io.ReadAll(conn)
			if err != nil || !bytes.Equal(got, request) {
				return fmt.Errorf("request mismatch or read error: %v", err)
			}
			_, err = conn.Write(response)
			return err
		})
		if err := p.Target("http://" + target.Addr().String()); err != nil {
			t.Fatal(err)
		}
		conn := wireTestDial(t, p)
		if _, err := conn.Write(request); err != nil {
			t.Fatal(err)
		}
		if err := conn.CloseWrite(); err != nil {
			t.Fatal(err)
		}
		got, err := io.ReadAll(conn)
		if err != nil || !bytes.Equal(got, response) {
			t.Fatalf("response mismatch or read error: %v", err)
		}
		want += int64(len(request) + len(response))
	}
	if err := p.Close(); err != nil {
		t.Fatal(err)
	}
	if p.Bytes() != want || p.upload.Load() != int64(2*len(request)) || p.download.Load() != want-int64(2*len(request)) {
		t.Fatalf("bytes %d, upload %d, download %d; want %d", p.Bytes(), p.upload.Load(), p.download.Load(), want)
	}
}

func TestWireProxyForwardsPlaintextHTTP2(t *testing.T) {
	t.Parallel()
	protocols := new(http.Protocols)
	protocols.SetUnencryptedHTTP2(true)
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.ProtoMajor != 2 {
			t.Errorf("protocol %s, want HTTP/2", r.Proto)
		}
		fmt.Fprint(w, "peer response")
	}))
	server.Config.Protocols = protocols
	server.Start()
	t.Cleanup(server.Close)
	p, err := StartWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	if err := p.Target(server.URL); err != nil {
		t.Fatal(err)
	}
	client := &http.Client{Transport: &http.Transport{Protocols: protocols}, Timeout: 3 * time.Second}
	t.Cleanup(client.CloseIdleConnections)
	resp, err := client.Get(p.URL())
	if err != nil {
		t.Fatal(err)
	}
	body, err := io.ReadAll(resp.Body)
	resp.Body.Close()
	if err != nil || string(body) != "peer response" || resp.ProtoMajor != 2 {
		t.Fatalf("body %q, error %v", body, err)
	}
	client.CloseIdleConnections()
	if err := p.Close(); err != nil {
		t.Fatal(err)
	}
	if p.Bytes() <= int64(len(body)) {
		t.Fatalf("HTTP/2 framing and control traffic not counted: %d bytes", p.Bytes())
	}
}

func TestWireProxyCancellationClosesActiveConnections(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	p, err := StartWireProxy(ctx)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	accepted := make(chan struct{})
	target := wireTestPeer(t, func(conn net.Conn) error {
		close(accepted)
		_, err := io.Copy(io.Discard, conn)
		return err
	})
	if err := p.Target("http://" + target.Addr().String()); err != nil {
		t.Fatal(err)
	}
	conn := wireTestDial(t, p)
	select {
	case <-accepted:
	case <-time.After(3 * time.Second):
		t.Fatal("proxy did not connect to peer")
	}
	cancel()
	done := make(chan error, 1)
	go func() { done <- p.Close() }()
	select {
	case err := <-done:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(3 * time.Second):
		t.Fatal("Close did not join proxy goroutines")
	}
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("active client read: %v, want EOF", err)
	}
	if conn, err := net.DialTimeout("tcp", p.listener.Addr().String(), time.Second); err == nil {
		conn.Close()
		t.Fatal("listener remained open")
	}
}

func TestWireProxyTargetValidationAndDialError(t *testing.T) {
	t.Parallel()
	p, err := StartWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	for _, raw := range []string{"https://127.0.0.1:1", "http://", "http://127.0.0.1:0", "http://127.0.0.1:65536", "http://user@127.0.0.1", "http://127.0.0.1/path", "http://127.0.0.1?x=1", "http://192.0.2.1:80"} {
		if err := p.Target(raw); err == nil {
			t.Fatalf("accepted invalid target %q", raw)
		}
	}
	conn := wireTestDial(t, p)
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("unset target read: %v", err)
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	if err := p.Target("http://" + listener.Addr().String()); err != nil {
		t.Fatal(err)
	}
	listener.Close()
	conn = wireTestDial(t, p)
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("unavailable target read: %v", err)
	}
	p.Suspend()
	if err := p.Close(); err == nil || !strings.Contains(err.Error(), "peer dial") {
		t.Fatalf("dial failure was not retained: %v", err)
	}
}

func TestWireProxySuspendAndResume(t *testing.T) {
	t.Parallel()
	p, err := StartWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	accepted := make(chan struct{})
	peer := wireTestPeer(t, func(conn net.Conn) error {
		close(accepted)
		_, err := io.Copy(io.Discard, conn)
		return err
	})
	if err := p.Target("http://" + peer.Addr().String()); err != nil {
		t.Fatal(err)
	}
	conn := wireTestDial(t, p)
	select {
	case <-accepted:
	case <-time.After(3 * time.Second):
		t.Fatal("proxy did not connect to peer")
	}
	p.Suspend()
	peer.Close()
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("suspended stream read: %v", err)
	}
	conn = wireTestDial(t, p)
	if _, err := conn.Read(make([]byte, 1)); !errors.Is(err, io.EOF) {
		t.Fatalf("suspended target read: %v", err)
	}
	restarted := wireTestPeer(t, func(conn net.Conn) error {
		_, err := conn.Write([]byte("ready"))
		return err
	})
	if err := p.Target("http://" + restarted.Addr().String()); err != nil {
		t.Fatal(err)
	}
	conn = wireTestDial(t, p)
	got, err := io.ReadAll(conn)
	if err != nil || string(got) != "ready" {
		t.Fatalf("resumed response %q: %v", got, err)
	}
	if err := p.Close(); err != nil {
		t.Fatal(err)
	}
}

func TestWireProxySuspendedClientKeepsItsGeneration(t *testing.T) {
	t.Parallel()
	p, err := StartWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	peer := wireTestPeer(t, func(conn net.Conn) error {
		_, err := conn.Write([]byte("ready"))
		return err
	})
	if err := p.Target("http://" + peer.Addr().String()); err != nil {
		t.Fatal(err)
	}
	oldTarget := p.target.Load()
	client, remote := net.Pipe()
	t.Cleanup(func() { remote.Close() })
	if !p.track(client) {
		t.Fatal("client was not tracked")
	}
	p.Suspend()
	if err := p.Target("http://" + peer.Addr().String()); err != nil {
		t.Fatal(err)
	}
	// Delay forwarding until after resume: the closed client must retain its old generation.
	p.forward(client, oldTarget)
	conn := wireTestDial(t, p)
	got, err := io.ReadAll(conn)
	if err != nil || string(got) != "ready" {
		t.Fatalf("resumed response %q: %v", got, err)
	}
	if err := p.Close(); err != nil {
		t.Fatal(err)
	}
}

func TestWireProxyClientResetFailsTheRun(t *testing.T) {
	t.Parallel()
	p, err := StartWireProxy(t.Context())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { p.Close() })
	disconnected := make(chan struct{})
	peer := wireTestPeer(t, func(conn net.Conn) error {
		defer close(disconnected)
		payload := make([]byte, 32*1024)
		for {
			if _, err := conn.Write(payload); err != nil {
				return nil
			}
		}
	})
	if err := p.Target("http://" + peer.Addr().String()); err != nil {
		t.Fatal(err)
	}
	client := wireTestDial(t, p)
	if _, err := client.Read(make([]byte, 1)); err != nil {
		t.Fatal(err)
	}
	if err := client.SetLinger(0); err != nil {
		t.Fatal(err)
	}
	client.Close()
	select {
	case <-disconnected:
	case <-time.After(3 * time.Second):
		t.Fatal("reset did not disconnect the peer")
	}
	if err := p.Close(); !errors.Is(err, syscall.ECONNRESET) && !errors.Is(err, syscall.EPIPE) {
		t.Fatalf("client reset was not retained: %v", err)
	}
}

func wireTestDial(t *testing.T, p *WireProxy) *net.TCPConn {
	t.Helper()
	conn, err := net.DialTimeout("tcp", strings.TrimPrefix(p.URL(), "http://"), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { conn.Close() })
	if err := conn.SetDeadline(time.Now().Add(3 * time.Second)); err != nil {
		t.Fatal(err)
	}
	return conn.(*net.TCPConn)
}

func wireTestPeer(t *testing.T, handle func(net.Conn) error) net.Listener {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	var wg sync.WaitGroup
	wg.Go(func() {
		conn, err := listener.Accept()
		if err != nil {
			t.Errorf("peer accept: %v", err)
			return
		}
		defer conn.Close()
		if err := conn.SetDeadline(time.Now().Add(3 * time.Second)); err != nil {
			t.Errorf("peer deadline: %v", err)
			return
		}
		if err := handle(conn); err != nil {
			t.Errorf("peer: %v", err)
		}
	})
	t.Cleanup(func() { listener.Close(); wg.Wait() })
	return listener
}

func TestClusterShutdownKeepsTransportUntilQuiescenceAndExit(t *testing.T) {
	t.Parallel()
	for _, mode := range []string{"close", "stop"} {
		t.Run(mode, func(t *testing.T) {
			t.Parallel()
			c := &Cluster{Work: t.TempDir(), Nodes: map[string]*Worker{}, Preserve: true}
			count := 2
			if mode == "stop" {
				count = 1
			}
			var workers []*Worker
			var clients []*net.TCPConn
			for i := range count {
				region := []string{"gru", "sjc"}[i]
				dir := filepath.Join(c.Work, region)
				if err := os.Mkdir(dir, 0o700); err != nil {
					t.Fatal(err)
				}
				if err := prepareWorkerControl(dir); err != nil {
					t.Fatal(err)
				}
				proxy, err := StartWireProxy(t.Context())
				if err != nil {
					t.Fatal(err)
				}
				t.Cleanup(func() { proxy.Close() })
				peer := wireTestPeer(t, func(conn net.Conn) error { _, err := io.Copy(conn, conn); return err })
				if err := proxy.Target("http://" + peer.Addr().String()); err != nil {
					t.Fatal(err)
				}
				w := &Worker{Config: workerConfig{Region: region}, Proxy: proxy,
					cmd: &exec.Cmd{Process: &os.Process{Pid: 1000000 + i}}, exited: make(chan error, 1)}
				workers = append(workers, w)
				c.Nodes[region] = w
				clients = append(clients, wireTestDial(t, proxy))
			}
			echo := func() {
				t.Helper()
				for _, conn := range clients {
					if _, err := conn.Write([]byte("x")); err != nil {
						t.Fatal(err)
					}
					b := make([]byte, 1)
					if _, err := io.ReadFull(conn, b); err != nil || string(b) != "x" {
						t.Fatalf("transport unavailable: %q %v", b, err)
					}
				}
			}
			echo()
			transportError := errors.New("pre-quiescence transport failure")
			if mode == "close" {
				workers[0].Proxy.record(transportError)
			}
			done := make(chan error, 1)
			var pending []chan error
			for _, w := range workers {
				pending = append(pending, w.exited)
			}
			go func() {
				if mode == "close" {
					done <- c.Close()
				} else {
					done <- c.Stop(workers[0])
				}
			}()
			t.Cleanup(func() {
				for _, ch := range pending {
					select {
					case ch <- nil:
					default:
					}
				}
				select {
				case <-done:
				case <-time.After(3 * time.Second):
					t.Error("shutdown did not join")
				}
			})
			for i, w := range workers {
				dir := filepath.Join(c.Work, w.Config.Region)
				waitShutdownFile(t, filepath.Join(dir, "quiesce.request"), true)
				// No proxy can disarm or disconnect while even one worker has not acknowledged.
				echo()
				for _, other := range workers {
					if other.Proxy.target.Load() == nil {
						t.Fatal("proxy disarmed before all acknowledgements")
					}
					if _, err := os.Stat(filepath.Join(c.Work, other.Config.Region, "control")); err != nil {
						t.Fatal("control removed before all acknowledgements")
					}
				}
				// A PID from an older run must not satisfy the handshake.
				if i == 0 {
					if err := os.WriteFile(filepath.Join(dir, "quiesced"), []byte("old-pid"), 0o600); err != nil {
						t.Fatal(err)
					}
					ctx, cancel := context.WithCancel(t.Context())
					cancel()
					if err := c.awaitQuiescence(ctx, w); !errors.Is(err, context.Canceled) {
						t.Fatalf("stale acknowledgement accepted: %v", err)
					}
				}
				if err := os.WriteFile(filepath.Join(dir, "quiesced"), []byte(strconv.Itoa(w.cmd.Process.Pid)), 0o600); err != nil {
					t.Fatal(err)
				}
			}
			for _, w := range workers {
				waitShutdownFile(t, filepath.Join(c.Work, w.Config.Region, "control"), false)
			}
			// Disarming generations must leave already established streams usable through worker disposal.
			echo()
			if count == 2 {
				pending[0] <- nil
				echo()
			}
			fatalExit := exec.Command("sh", "-c", "exit 2").Run()
			pending[count-1] <- fatalExit
			select {
			case err := <-done:
				// Let cleanup join even if a failure assertion below aborts the test.
				done <- err
				exit, ok := errors.AsType[*exec.ExitError](err)
				if !ok || exit.ExitCode() != 2 {
					t.Fatalf("nonzero worker exit lost: %v", err)
				}
				if mode == "close" && !errors.Is(err, transportError) {
					t.Fatalf("pre-existing proxy failure lost: %v", err)
				}
			case <-time.After(3 * time.Second):
				t.Fatal("shutdown did not finish after every worker exit")
			}
		})
	}
}

func TestPrepareWorkerControlRemovesPreviousGeneration(t *testing.T) {
	t.Parallel()
	dir := t.TempDir()
	for _, name := range []string{"quiesce.request", "quiesced", "target.json", "listening.json", "bootstrap.json"} {
		if err := os.WriteFile(filepath.Join(dir, name), []byte("previous-generation"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	if err := prepareWorkerControl(dir); err != nil {
		t.Fatal(err)
	}
	for _, name := range []string{"quiesce.request", "quiesced", "target.json", "listening.json", "bootstrap.json"} {
		if _, err := os.Stat(filepath.Join(dir, name)); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("stale %s survived relaunch: %v", name, err)
		}
	}
	if _, err := os.Stat(filepath.Join(dir, "control")); err != nil {
		t.Fatal(err)
	}
}

func TestClusterStopRestoresStrictErrorsBeforeRecovery(t *testing.T) {
	t.Parallel()
	c := &Cluster{Work: t.TempDir(), Nodes: map[string]*Worker{}}
	for _, region := range []string{"gru", "fra"} {
		proxy, err := StartWireProxy(t.Context())
		if err != nil {
			t.Fatal(err)
		}
		t.Cleanup(func() { proxy.Close() })
		proxy.target.Store(new("127.0.0.1:1"))
		c.Nodes[region] = &Worker{Config: workerConfig{Region: region}, Proxy: proxy}
	}
	w := c.Nodes["fra"]
	dir := filepath.Join(c.Work, "fra")
	if err := os.Mkdir(dir, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := prepareWorkerControl(dir); err != nil {
		t.Fatal(err)
	}
	w.cmd = &exec.Cmd{Process: &os.Process{Pid: 1000000}}
	w.exited = make(chan error, 1)
	exited := w.exited
	done := make(chan error, 1)
	go func() { done <- c.Stop(w) }()
	t.Cleanup(func() {
		select {
		case exited <- nil:
		default:
		}
		select {
		case <-done:
		case <-time.After(3 * time.Second):
			t.Error("stop did not join")
		}
	})
	waitShutdownFile(t, filepath.Join(dir, "quiesce.request"), true)
	other := c.Nodes["gru"].Proxy
	target := other.target.Load()
	other.recordForward(syscall.ECONNRESET, target, true)
	other.mu.Lock()
	allowed := other.err == nil && other.shutdownResets == 1
	other.mu.Unlock()
	if !allowed {
		t.Fatal("outgoing shutdown response reset was not classified")
	}
	if err := os.WriteFile(filepath.Join(dir, "quiesced"), []byte(strconv.Itoa(w.cmd.Process.Pid)), 0o600); err != nil {
		t.Fatal(err)
	}
	waitShutdownFile(t, filepath.Join(dir, "control"), false)
	exited <- nil
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	done <- nil
	other.recordForward(syscall.ECONNRESET, target, true)
	if !errors.Is(other.err, syscall.ECONNRESET) {
		t.Fatalf("runtime reset after stop was not retained: %v", other.err)
	}
	for _, peer := range c.Nodes {
		if peer.Proxy.quiescing {
			t.Fatal("shutdown allowance survived Stop")
		}
	}
}

func waitShutdownFile(t *testing.T, path string, exists bool) {
	t.Helper()
	ctx, cancel := context.WithTimeout(t.Context(), 3*time.Second)
	defer cancel()
	for {
		_, err := os.Stat(path)
		if (exists && err == nil) || (!exists && errors.Is(err, os.ErrNotExist)) {
			return
		}
		if err != nil && !errors.Is(err, os.ErrNotExist) {
			t.Fatal(err)
		}
		select {
		case <-ctx.Done():
			t.Fatalf("file %s exists=%t: %v", path, exists, ctx.Err())
		case <-time.After(time.Millisecond):
		}
	}
}

func TestWireProxyQuiescenceOnlyAcceptsCompletedUploadReset(t *testing.T) {
	t.Parallel()
	for _, test := range []struct {
		name           string
		quiescing      bool
		uploadComplete bool
		err            error
		wantReset      bool
	}{
		{"in-run-reset", false, true, syscall.ECONNRESET, false},
		{"in-run-broken-pipe", false, true, syscall.EPIPE, false},
		{"unfinished-upload", true, false, syscall.ECONNRESET, false},
		{"intentional-response-reset", true, true, syscall.ECONNRESET, true},
		{"broken-pipe", true, true, syscall.EPIPE, false},
		{"refused-dial", true, true, syscall.ECONNREFUSED, false},
		{"unexpected-error", true, true, errors.New("unexpected"), false},
	} {
		t.Run(test.name, func(t *testing.T) {
			t.Parallel()
			p := &WireProxy{ctx: t.Context()}
			target := new("127.0.0.1:1")
			p.target.Store(target)
			if test.quiescing {
				p.beginQuiescence()
			}
			err := fmt.Errorf("wrapped transport error: %w", test.err)
			p.recordForward(err, target, test.uploadComplete)
			if test.wantReset {
				if p.err != nil || p.shutdownResets != 1 {
					t.Fatalf("expected reset was not classified: %v, count=%d", p.err, p.shutdownResets)
				}
			} else if !errors.Is(p.err, test.err) || p.shutdownResets != 0 {
				t.Fatalf("transport failure lost: %v, count=%d", p.err, p.shutdownResets)
			}
			// Beginning shutdown never erases an error already recorded during measurement.
			p.beginQuiescence()
			p.recordForward(syscall.ECONNRESET, target, true)
			if !test.wantReset && !errors.Is(p.err, test.err) {
				t.Fatalf("previous failure erased: %v", p.err)
			}
		})
	}
}

func TestWireProxyFinalDisarmRetainsUnexpectedError(t *testing.T) {
	t.Parallel()
	p := &WireProxy{ctx: t.Context()}
	target := new("127.0.0.1:1")
	p.target.Store(target)
	p.beginQuiescence()
	p.target.Store(nil)
	p.recordForward(syscall.ECONNREFUSED, target, false)
	if !errors.Is(p.err, syscall.ECONNREFUSED) {
		t.Fatalf("post-acknowledgement dial error lost: %v", p.err)
	}
}
