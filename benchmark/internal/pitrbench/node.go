package pitrbench

import (
	"bufio"
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
)

// NodeState is node.json: the running node of a run directory. Ports are chosen once and kept across restarts.
type NodeState struct {
	PID          int    `json:"pid"`
	Port         int    `json:"port"`
	InternalPort int    `json:"internal_port"`
	MetricsPort  int    `json:"metrics_port"`
	DataDir      string `json:"data_dir"`
	Log          string `json:"log"`
	StartedMs    int64  `json:"started_ms"`
	Starts       int    `json:"starts"`
	// PeerSecret is the node's TERRABASE_PEER_SECRET: the internal listener refuses every request without its
	// X-TerraBase-Peer-Credential value (src/TerraBase/Features/Replication/PeerCredential.cs). node.json holds it
	// beside the data directory it protects; it is never printed.
	PeerSecret string `json:"peer_secret,omitempty"`
}

// envNames are the only variables the env file may set: the backup bucket (S3-compatible names), the master key, and
// SSL_CERT_FILE for the local S3 emulator's certificate. Values are passed to the node and never printed.
var envNames = map[string]bool{
	"AWS_ENDPOINT_URL_S3": true, "AWS_REGION": true, "BUCKET_NAME": true, "AWS_ACCESS_KEY_ID": true,
	"AWS_SECRET_ACCESS_KEY": true, "TERRABASE_MASTER_KEY": true, "SSL_CERT_FILE": true,
}

// nodeDir holds node.json, the node's data directory and logs: the run directory, or another directory for a second,
// isolated node (a restore target) driven against the same oracle.
func (r *Run) nodeDir() string {
	if r.NodeDir != "" {
		return r.NodeDir
	}
	return r.Dir
}

// LoadNode reads node.json.
func LoadNode(r *Run) (*NodeState, error) {
	var n NodeState
	b, err := os.ReadFile(filepath.Join(r.nodeDir(), "node.json"))
	if err != nil {
		return nil, err
	}
	if err := jsonUnmarshal(b, &n); err != nil {
		return nil, err
	}
	return &n, nil
}

// Client is the internal-listener client of the run's node.
func (r *Run) Client(timeout time.Duration) (*Client, error) {
	n, err := LoadNode(r)
	if err != nil {
		return nil, err
	}
	return NewClient(n.InternalPort, n.PeerSecret, r.Owner, timeout), nil
}

// StartNode starts the pinned binary on the run's data directory, detached (it outlives this command), at nice 19,
// and waits for /healthz. The binary's sha256 must still match run.json.
func StartNode(ctx context.Context, r *Run) (*NodeState, error) {
	n, err := LoadNode(r)
	if errors.Is(err, os.ErrNotExist) {
		n = &NodeState{DataDir: filepath.Join(r.nodeDir(), "data"), Log: filepath.Join(r.nodeDir(), "logs", "node.log")}
		for _, p := range []*int{&n.Port, &n.InternalPort, &n.MetricsPort} {
			if *p, err = freePort(); err != nil {
				return nil, err
			}
		}
	} else if err != nil {
		return nil, err
	}
	if n.PID != 0 && alive(n.PID) {
		return nil, fmt.Errorf("node pid %d is still running", n.PID)
	}
	if n.PeerSecret == "" {
		secret := make([]byte, 32)
		if _, err := rand.Read(secret); err != nil {
			return nil, err
		}
		n.PeerSecret = hex.EncodeToString(secret)
	}
	if sum, err := FileSHA256(r.Binary); err != nil || sum != r.BinarySHA256 {
		return nil, fmt.Errorf("binary %s does not match the pinned sha256 (%v)", r.Binary, err)
	}
	env, err := nodeEnv(r, n)
	if err != nil {
		return nil, err
	}
	for _, d := range []string{n.DataDir, filepath.Dir(n.Log)} {
		if err := os.MkdirAll(d, 0o700); err != nil {
			return nil, err
		}
	}
	logf, err := os.OpenFile(n.Log, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600)
	if err != nil {
		return nil, err
	}
	defer logf.Close()
	fmt.Fprintf(logf, "\n=== pitr-bench: node start %d at %s ===\n", n.Starts+1, time.Now().UTC().Format(time.RFC3339Nano))
	// The node outlives this command: it must not inherit any descriptor but its output (a lock fd held by a wrapping
	// flock, say, would stay held for the node's whole life).
	if fds, err := os.ReadDir("/proc/self/fd"); err == nil {
		for _, fd := range fds {
			if n, err := strconv.Atoi(fd.Name()); err == nil && n > 2 {
				syscall.CloseOnExec(n)
			}
		}
	}
	cmd := exec.Command("nice", "-n", "19", r.Binary)
	cmd.Env, cmd.Stdout, cmd.Stderr, cmd.Dir = env, logf, logf, r.nodeDir()
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	if err := cmd.Start(); err != nil {
		return nil, err
	}
	n.PID, n.StartedMs, n.Starts = cmd.Process.Pid, time.Now().UnixMilli(), n.Starts+1
	_ = cmd.Process.Release()
	if err := writeJSON(filepath.Join(r.nodeDir(), "node.json"), n); err != nil {
		return nil, err
	}
	hc := &http.Client{Timeout: 2 * time.Second}
	deadline := time.Now().Add(10 * time.Minute) // a start snapshots every database before it serves? measured, not assumed
	for {
		if !alive(n.PID) {
			return nil, fmt.Errorf("node exited during start; see %s", n.Log)
		}
		resp, err := hc.Get("http://127.0.0.1:" + strconv.Itoa(n.Port) + "/healthz")
		if err == nil {
			resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return n, nil
			}
		}
		if time.Now().After(deadline) {
			return nil, fmt.Errorf("node did not answer /healthz within 10 minutes; see %s", n.Log)
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(200 * time.Millisecond):
		}
	}
}

// nodeEnv is this process's environment without any TerraBase, AWS or TLS-trust settings, plus the run's
// whitelisted env file and the node's own settings.
func nodeEnv(r *Run, n *NodeState) ([]string, error) {
	var env []string
	for _, kv := range os.Environ() {
		name, _, _ := strings.Cut(kv, "=")
		if strings.HasPrefix(name, "TERRABASE_") || strings.HasPrefix(name, "AWS_") ||
			strings.HasPrefix(name, "SSL_CERT_") || name == "BUCKET_NAME" || name == "PRIMARY_REGION" ||
			name == "DOTNET_ENVIRONMENT" || name == "ASPNETCORE_ENVIRONMENT" {
			continue
		}
		env = append(env, kv)
	}
	if r.EnvFile != "" {
		extra, err := readEnvFile(r.EnvFile)
		if err != nil {
			return nil, err
		}
		env = append(env, extra...)
	}
	return append(env,
		"DOTNET_ENVIRONMENT="+r.Environment,
		"TERRABASE_DATA_DIRECTORY="+n.DataDir,
		"TERRABASE_PORT="+strconv.Itoa(n.Port),
		"TERRABASE_INTERNAL_PORT="+strconv.Itoa(n.InternalPort),
		"TERRABASE_METRICS_PORT="+strconv.Itoa(n.MetricsPort),
		"TERRABASE_POSTGRES_PORT=0",
		"TERRABASE_WIRE_PORT=0",
		"TERRABASE_PEER_SECRET="+n.PeerSecret,
	), nil
}

// readEnvFile reads NAME=value lines from a file only its owner can read. Errors name lines and settings, never values.
func readEnvFile(path string) ([]string, error) {
	info, err := os.Stat(path)
	if err != nil {
		return nil, err
	}
	if info.Mode().Perm()&0o077 != 0 {
		return nil, fmt.Errorf("env file %s must be readable by its owner only (chmod 600)", path)
	}
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	var out []string
	s := bufio.NewScanner(f)
	for line := 1; s.Scan(); line++ {
		text := strings.TrimSpace(s.Text())
		if text == "" || strings.HasPrefix(text, "#") {
			continue
		}
		name, _, ok := strings.Cut(text, "=")
		if !ok || !envNames[name] {
			return nil, fmt.Errorf("env file %s line %d: only %s may be set", path, line, strings.Join(sortedKeys(envNames), ", "))
		}
		out = append(out, text)
	}
	return out, s.Err()
}

// StopNode asks the node to shut down (SIGTERM: it ships what is left) and waits up to grace.
func StopNode(r *Run, grace time.Duration) error { return signalNode(r, syscall.SIGTERM, grace) }

// KillNode ends the node at once (SIGKILL: no shutdown, no flush).
func KillNode(r *Run) error { return signalNode(r, syscall.SIGKILL, time.Minute) }

func signalNode(r *Run, sig syscall.Signal, wait time.Duration) error {
	n, err := LoadNode(r)
	if err != nil {
		return err
	}
	if !alive(n.PID) {
		return fmt.Errorf("node pid %d is not running", n.PID)
	}
	if err := syscall.Kill(n.PID, sig); err != nil {
		return err
	}
	deadline := time.Now().Add(wait)
	for alive(n.PID) {
		if time.Now().After(deadline) {
			return fmt.Errorf("node pid %d still running %s after %s", n.PID, wait, sig)
		}
		time.Sleep(100 * time.Millisecond)
	}
	return nil
}

// alive reports a live, non-zombie process.
func alive(pid int) bool {
	b, err := os.ReadFile("/proc/" + strconv.Itoa(pid) + "/stat")
	if err != nil {
		return false
	}
	// The state follows the parenthesized command name.
	s := string(b)
	i := strings.LastIndexByte(s, ')')
	return i >= 0 && i+2 < len(s) && s[i+2] != 'Z'
}

func freePort() (int, error) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return 0, err
	}
	defer l.Close()
	return l.Addr().(*net.TCPAddr).Port, nil
}

// requireReconciled fails when the node restarted since the last passing reconcile. An unclean host crash
// (synchronous=NORMAL) can lose acknowledged commits, fences included, so nothing may be fenced, written or cut over
// them until Reconcile has read them back.
func requireReconciled(r *Run) error {
	n, err := LoadNode(r)
	if err != nil || n.Starts <= 1 {
		return err
	}
	var rec struct {
		Starts int `json:"starts"`
	}
	if b, err := os.ReadFile(filepath.Join(r.nodeDir(), "reconciled.json")); err == nil {
		if err := jsonUnmarshal(b, &rec); err != nil {
			return fmt.Errorf("reconciled.json: %w", err)
		}
	}
	if rec.Starts < n.Starts {
		return fmt.Errorf("the node restarted (start %d) since the last passing reconcile (start %d): run pitr-bench reconcile first", n.Starts, rec.Starts)
	}
	return nil
}
