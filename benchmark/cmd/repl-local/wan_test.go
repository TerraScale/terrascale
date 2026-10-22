package main

import (
	"context"
	"io"
	"net"
	"os"
	"os/exec"
	"testing"
	"time"
)

func TestWANPeerPortIsolation(t *testing.T) {
	t.Parallel()
	ctx, cancel := context.WithTimeout(t.Context(), 30*time.Second)
	defer cancel()
	if os.Getenv("REPL_WAN_TEST_CHILD") == "guard" {
		if err := isolatedNetwork(); err == nil {
			t.Fatal("namespace guard accepted a non-network or current-namespace FD")
		}
		return
	}
	if os.Getenv("REPL_WAN_TEST_CHILD") == "" {
		if err := isolatedNetwork(); err == nil {
			t.Fatal("expected namespace guard to reject the outer process")
		}
		cmd := exec.CommandContext(ctx, "bash", "-c", `exec 3</dev/null
REPL_WAN_TEST_CHILD=guard "$@" || exit
exec 3</proc/self/ns/net
REPL_WAN_TEST_CHILD=guard "$@" || exit
exec unshare --user --map-root-user --net bash -c 'ip link set lo up; exec "$@"' wan-check "$@"`,
			"wan-check", os.Args[0], "-test.run=^TestWANPeerPortIsolation$", "-test.v")
		cmd.Env = append(os.Environ(), "REPL_WAN_TEST_CHILD=1")
		output, err := cmd.CombinedOutput()
		t.Logf("isolated TCP self-check:\n%s", output)
		if err != nil {
			t.Fatal(err)
		}
		return
	}
	original, _ := os.Readlink("/proc/self/fd/3")
	current, _ := os.Readlink("/proc/self/ns/net")
	t.Logf("original_namespace=%s isolated_namespace=%s", original, current)

	proxy, err := startWireProxy(ctx)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { proxy.Close() })
	peer := wireTestPeer(t, func(conn net.Conn) error { _, err := io.Copy(conn, conn); return err })
	if err := proxy.Target("http://" + peer.Addr().String()); err != nil {
		t.Fatal(err)
	}
	shaped := wireTestDial(t, proxy)
	controlPeer := wireTestPeer(t, func(conn net.Conn) error { _, err := io.Copy(conn, conn); return err })
	control, err := net.DialTimeout("tcp", controlPeer.Addr().String(), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { control.Close() })
	ping := func(conn net.Conn) time.Duration {
		t.Helper()
		if err := conn.SetDeadline(time.Now().Add(3 * time.Second)); err != nil {
			t.Fatal(err)
		}
		started := time.Now()
		if _, err := conn.Write([]byte{42}); err != nil {
			t.Fatal(err)
		}
		var reply [1]byte
		if _, err := io.ReadFull(conn, reply[:]); err != nil || reply[0] != 42 {
			t.Fatalf("TCP echo: %v, %v", reply, err)
		}
		return time.Since(started)
	}
	baseline := ping(shaped)
	cleanup, err := shapeWAN(ctx, []int{proxy.listener.Addr().(*net.TCPAddr).Port})
	if err != nil {
		t.Fatal(err)
	}
	defer func() {
		if cleanup != nil {
			if err := cleanup(); err != nil {
				t.Error(err)
			}
		}
	}()
	for range 3 {
		rtt, unshaped := ping(shaped), ping(control)
		t.Logf("baseline=%s shaped_RTT=%s unshaped_control=%s", baseline, rtt, unshaped)
		if rtt < 180*time.Millisecond || unshaped >= 100*time.Millisecond {
			t.Fatal("peer RTT or unshaped control does not match the WAN profile")
		}
	}
	if err := cleanup(); err != nil {
		t.Fatal(err)
	}
	cleanup = nil
	if restored := ping(shaped); restored >= 100*time.Millisecond {
		t.Fatalf("cleanup left shaping active: %s", restored)
	}
}
