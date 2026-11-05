package main

import (
	"bufio"
	"cmp"
	"context"
	"crypto/rand"
	"encoding/base64"
	"errors"
	"fmt"
	"maps"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"syscall"
	"time"
)

func itoa(n int) string { return strconv.Itoa(n) }

// freePort reserves an ephemeral port on ip and releases it for the child process to bind.
func freePort(ip string) (int, error) {
	ln, err := net.Listen("tcp", net.JoinHostPort(ip, "0"))
	if err != nil {
		return 0, err
	}
	defer ln.Close()
	return ln.Addr().(*net.TCPAddr).Port, nil
}

// sharedPort finds one ephemeral port free on every ip: a peers-file line carries only an address, so every node
// listens for peers on the same internal port, as in production.
func sharedPort(ips ...string) (int, error) {
	for range 50 {
		port, err := freePort(ips[0])
		if err != nil {
			return 0, err
		}
		free := true
		for _, ip := range ips[1:] {
			ln, err := net.Listen("tcp", net.JoinHostPort(ip, itoa(port)))
			if err != nil {
				free = false
				break
			}
			ln.Close()
		}
		if free {
			return port, nil
		}
	}
	return 0, errors.New("no port is free on every node address")
}

// process is a child in its own process group, logging to an appended file.
type process struct {
	name string
	cmd  *exec.Cmd
	done chan struct{}
	err  error
}

func startProcess(name, logPath string, env []string, argv ...string) (*process, error) {
	logFile, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return nil, err
	}
	fmt.Fprintf(logFile, "\n=== region-sim: start %s at %s ===\n", name, time.Now().UTC().Format(time.RFC3339Nano))
	cmd := exec.Command(argv[0], argv[1:]...)
	cmd.Env = env
	cmd.Stdout, cmd.Stderr = logFile, logFile
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true, Pdeathsig: syscall.SIGKILL}
	if err := cmd.Start(); err != nil {
		logFile.Close()
		return nil, fmt.Errorf("start %s: %w", name, err)
	}
	p := &process{name: name, cmd: cmd, done: make(chan struct{})}
	go func() {
		p.err = cmd.Wait()
		logFile.Close()
		close(p.done)
	}()
	return p, nil
}

func (p *process) exited() bool {
	select {
	case <-p.done:
		return true
	default:
		return false
	}
}

// stop sends SIGTERM to the process group, then SIGKILL after grace.
func (p *process) stop(grace time.Duration) {
	if p == nil || p.exited() {
		return
	}
	syscall.Kill(-p.cmd.Process.Pid, syscall.SIGTERM)
	select {
	case <-p.done:
	case <-time.After(grace):
		syscall.Kill(-p.cmd.Process.Pid, syscall.SIGKILL)
		<-p.done
	}
}

// nats is the run's nats-server, token-authenticated through a 0600 config file.
type nats struct {
	bin, conf, logPath string
	port               int
	proc               *process
}

func newNATS(dir, logDir, token string) (*nats, error) {
	bin, err := exec.LookPath("nats-server")
	if err != nil {
		return nil, errors.New("nats-server is not on PATH (installed on dev hosts; install it to run region-sim)")
	}
	port, err := freePort("127.0.0.1")
	if err != nil {
		return nil, err
	}
	n := &nats{bin: bin, conf: filepath.Join(dir, "nats.conf"), logPath: filepath.Join(logDir, "nats.log"), port: port}
	conf := fmt.Sprintf("listen: \"127.0.0.1:%d\"\nauthorization { token: %q }\n", port, token)
	conf += fmt.Sprintf("jetstream { store_dir: %q }\n", filepath.Join(dir, "jetstream"))
	return n, writeFile0600(n.conf, []byte(conf))
}

func (n *nats) url() string { return "nats://127.0.0.1:" + itoa(n.port) }

func (n *nats) start(ctx context.Context) error {
	p, err := startProcess("nats-server", n.logPath, []string{"PATH=" + os.Getenv("PATH")}, n.bin, "-c", n.conf)
	if err != nil {
		return err
	}
	n.proc = p
	return poll(ctx, 15*time.Second, func() (bool, error) {
		if p.exited() {
			return false, fmt.Errorf("nats-server exited: %v (see %s)", p.err, n.logPath)
		}
		c, err := net.DialTimeout("tcp", "127.0.0.1:"+itoa(n.port), time.Second)
		if err == nil {
			c.Close()
		}
		return err == nil, nil
	})
}

func (n *nats) stop() { n.proc.stop(5 * time.Second) }

// node is one TerraBase process: a region with a single machine.
type node struct {
	name, region, ip string
	dir, logPath     string
	clientPort       int
	env              []string
	argv             []string
	proc             *process
}

func (n *node) base() string { return "http://127.0.0.1:" + itoa(n.clientPort) }

func (n *node) start(ctx context.Context) error {
	p, err := startProcess(n.name, n.logPath, n.env, n.argv...)
	if err != nil {
		return err
	}
	n.proc = p
	hc := &http.Client{Timeout: 2 * time.Second}
	return poll(ctx, 90*time.Second, func() (bool, error) {
		if p.exited() {
			return false, fmt.Errorf("%s exited: %v (see %s)", n.name, p.err, n.logPath)
		}
		res, err := hc.Get(n.base() + "/readyz")
		if err != nil {
			return false, nil
		}
		res.Body.Close()
		return res.StatusCode == http.StatusOK, nil
	})
}

func (n *node) stop() { n.proc.stop(30 * time.Second) }

func (n *node) restart(ctx context.Context) error {
	n.stop()
	return n.start(ctx)
}

// tail returns the last lines of the node's log for failure reports.
func tail(path string, lines int) string {
	f, err := os.Open(path)
	if err != nil {
		return ""
	}
	defer f.Close()
	var ring []string
	s := bufio.NewScanner(f)
	s.Buffer(make([]byte, 1<<20), 1<<20)
	for s.Scan() {
		ring = append(ring, s.Text())
		if len(ring) > lines {
			ring = ring[1:]
		}
	}
	return strings.Join(ring, "\n")
}

type clusterConfig struct {
	dir       string
	logDir    string
	terrabase string
	issuer    string
	caFile    string
}

// cluster is gru-1 (home, A) and sjc-1 (replica, B), plus each region's second node gru-2 and sjc-2, which hold
// their partner's local writes (PIVOT R10). Each peers file lists all four nodes, each region's founder first; gru-1 and sjc-1 see
// each other at a forwarder address, so the harness can partition the internal (peer) path in both directions.
type cluster struct {
	a, b      *node
	a2, b2    *node
	nats      *nats
	toA, toB  *forwarder // b→a and a→b peer paths
	natsToken string
}

const (
	ipA     = "127.0.0.2" // gru-1 internal listener
	ipB     = "127.0.0.3" // sjc-1 internal listener
	ipAviaB = "127.0.0.4" // gru-1 as sjc-1 sees it (forwarder)
	ipBviaA = "127.0.0.5" // sjc-1 as gru-1 sees it (forwarder)
	ipA2    = "127.0.0.6" // gru-2 internal listener
	ipB2    = "127.0.0.7" // sjc-2 internal listener
)

// nodes are the cluster's started or configured nodes, region by region.
func (c *cluster) nodes() []*node {
	var all []*node
	for _, n := range []*node{c.a, c.a2, c.b, c.b2} {
		if n != nil {
			all = append(all, n)
		}
	}
	return all
}

func newCluster(ctx context.Context, cfg clusterConfig) (*cluster, error) {
	c := &cluster{natsToken: randomHex(24)}
	var err error
	if c.nats, err = newNATS(cfg.dir, cfg.logDir, c.natsToken); err != nil {
		return c, err
	}
	internal, err := sharedPort(ipA, ipB, ipAviaB, ipBviaA, ipA2, ipB2)
	if err != nil {
		return c, fmt.Errorf("internal port (needs 127.0.0.2-7 on loopback): %w", err)
	}
	if c.toA, err = startForwarder(net.JoinHostPort(ipAviaB, itoa(internal)), net.JoinHostPort(ipA, itoa(internal))); err != nil {
		return c, err
	}
	if c.toB, err = startForwarder(net.JoinHostPort(ipBviaA, itoa(internal)), net.JoinHostPort(ipB, itoa(internal))); err != nil {
		c.toA.close()
		return c, err
	}
	natsB := c.nats.url()
	if peerDelay > 0 {
		port, err := freePort("127.0.0.1")
		if err != nil {
			return c, err
		}
		if _, err := startForwarder(net.JoinHostPort("127.0.0.1", itoa(port)), net.JoinHostPort("127.0.0.1", itoa(c.nats.port))); err != nil {
			return c, err
		}
		natsB = "nats://127.0.0.1:" + itoa(port)
	}
	natsURL := func(region string) string {
		if region == "sjc" {
			return natsB
		}
		return c.nats.url()
	}
	secrets := map[string]string{"peer-secret": randomHex(32), "nats-token": c.natsToken}
	masterKey := make([]byte, 32)
	if _, err := rand.Read(masterKey); err != nil {
		return c, err
	}
	secrets["master-key"] = base64.StdEncoding.EncodeToString(masterKey)
	for name, value := range secrets {
		if err := writeFile0600(filepath.Join(cfg.dir, name), []byte(value)); err != nil {
			return c, err
		}
	}
	root, err := dotnetRoot()
	if err != nil {
		return c, err
	}
	argv := []string{cfg.terrabase}
	if strings.HasSuffix(cfg.terrabase, ".dll") {
		argv = []string{filepath.Join(root, "dotnet"), cfg.terrabase}
	}
	mk := func(name, region, self string, peerIPs map[string]string) (*node, error) {
		dir := filepath.Join(cfg.dir, name)
		if err := os.MkdirAll(filepath.Join(dir, "data"), 0o700); err != nil {
			return nil, err
		}
		peers := filepath.Join(dir, "peers")
		var lines []byte
		for _, peer := range []string{"gru-1", "gru-2", "sjc-1", "sjc-2"} {
			ip := peerIPs[peer]
			if peer == name {
				ip = self
			}
			lines = fmt.Appendf(lines, "%s %s %s\n", peer, peer[:3], ip)
		}
		if err := writeFile0600(peers, lines); err != nil {
			return nil, err
		}
		n := &node{name: name, region: region, ip: self, dir: dir, logPath: filepath.Join(cfg.logDir, name+".log"), argv: argv}
		ports := map[string]int{}
		for _, key := range []string{"PORT", "METRICS_PORT", "POSTGRES_PORT"} {
			if ports[key], err = freePort("0.0.0.0"); err != nil {
				return nil, err
			}
		}
		n.clientPort = ports["PORT"]
		n.env = []string{
			"PATH=" + os.Getenv("PATH"),
			"HOME=" + dir,
			"DOTNET_ENVIRONMENT=Development",
			"DOTNET_CLI_TELEMETRY_OPTOUT=1",
			"SSL_CERT_FILE=" + cfg.caFile,
			"TS_REGION=" + region,
			"TS_NODE=" + name,
			"TS_PEERS_FILE=" + peers,
			"TS_NATS_URL=" + natsURL(region),
			"TERRABASE_NATS_TOKEN_FILE=" + filepath.Join(cfg.dir, "nats-token"),
			"TERRABASE_PEER_SECRET_FILE=" + filepath.Join(cfg.dir, "peer-secret"),
			"TERRABASE_MASTER_KEY_FILE=" + filepath.Join(cfg.dir, "master-key"),
			"TERRABASE_PORT=" + itoa(ports["PORT"]),
			"TERRABASE_INTERNAL_PORT=" + itoa(internal),
			"TERRABASE_METRICS_PORT=" + itoa(ports["METRICS_PORT"]),
			"TERRABASE_POSTGRES_PORT=" + itoa(ports["POSTGRES_PORT"]),
			"TERRABASE_DATA_DIRECTORY=" + filepath.Join(dir, "data"),
			"TERRABASE_AUTH_ISSUER=" + cfg.issuer,
			"TERRABASE_AUTH_AUDIENCE=" + platformAud,
		}
		n.env = append(n.env, "DOTNET_ROOT="+root)
		return n, nil
	}
	direct := map[string]string{"gru-1": ipA, "gru-2": ipA2, "sjc-1": ipB, "sjc-2": ipB2}
	with := func(name, ip string) map[string]string {
		peers := maps.Clone(direct)
		peers[name] = ip
		return peers
	}
	if c.a, err = mk("gru-1", "gru", ipA, with("sjc-1", ipBviaA)); err != nil {
		return c, err
	}
	if c.a2, err = mk("gru-2", "gru", ipA2, direct); err != nil {
		return c, err
	}
	if c.b, err = mk("sjc-1", "sjc", ipB, with("gru-1", ipAviaB)); err != nil {
		return c, err
	}
	if c.b2, err = mk("sjc-2", "sjc", ipB2, direct); err != nil {
		return c, err
	}
	if err := c.nats.start(ctx); err != nil {
		return c, err
	}
	for _, n := range c.nodes() {
		if err := n.start(ctx); err != nil {
			return c, err
		}
	}
	return c, nil
}

// dotnetRoot is the install root of the newest Microsoft.NETCore.App that `dotnet` lists. Dev hosts wrap dotnet
// (nix, mise), so an inherited DOTNET_ROOT can name a root without the runtime TerraBase targets.
func dotnetRoot() (string, error) {
	out, err := exec.Command("dotnet", "--list-runtimes").Output()
	if err != nil {
		return "", fmt.Errorf("dotnet --list-runtimes: %w", err)
	}
	var root string
	for line := range strings.Lines(string(out)) {
		if rest, ok := strings.CutPrefix(line, "Microsoft.NETCore.App "); ok {
			if _, path, ok := strings.Cut(rest, "["); ok {
				root = filepath.Dir(filepath.Dir(strings.TrimSpace(strings.TrimSuffix(strings.TrimSpace(path), "]"))))
			}
		}
	}
	if root == "" {
		return "", errors.New("dotnet lists no Microsoft.NETCore.App runtime")
	}
	return root, nil
}

func (c *cluster) close() {
	if c == nil {
		return
	}
	for _, n := range slices.Backward(c.nodes()) {
		n.stop()
	}
	if c.nats != nil {
		c.nats.stop()
	}
	for _, f := range []*forwarder{c.toA, c.toB} {
		if f != nil {
			f.close()
		}
	}
}

// poll calls check until it reports done, fails, or the deadline passes. It backs off from 2 ms to 100 ms.
func poll(ctx context.Context, timeout time.Duration, check func() (bool, error)) error {
	return pollEvery(ctx, timeout, 0, check)
}

// pollEvery polls at a fixed interval; a zero interval backs off from 2 ms to 100 ms.
func pollEvery(ctx context.Context, timeout, interval time.Duration, check func() (bool, error)) error {
	ctx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	wait := cmp.Or(interval, 2*time.Millisecond)
	for {
		ok, err := check()
		if err != nil {
			return err
		}
		if ok {
			return nil
		}
		select {
		case <-ctx.Done():
			return fmt.Errorf("not reached within %s", timeout)
		case <-time.After(wait):
		}
		if interval == 0 {
			wait = min(wait*2, 100*time.Millisecond)
		}
	}
}
