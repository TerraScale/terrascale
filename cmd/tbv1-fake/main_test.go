package main_test

// Fast-suite integration test: builds tbv1-fake, runs it as a process and
// talks to it over TLS with the CA it generated.

import (
	"bufio"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"
	"testing"
	"time"
)

func TestBinaryServesTLSWithGeneratedCA(t *testing.T) {
	dir := t.TempDir()
	bin := filepath.Join(dir, "tbv1-fake")
	if out, err := exec.Command("go", "build", "-o", bin, ".").CombinedOutput(); err != nil {
		t.Fatalf("build: %v\n%s", err, out)
	}
	if out, err := exec.Command(bin, "--listen", "127.0.0.1:0").CombinedOutput(); err == nil || !strings.Contains(string(out), "--control-key-out") {
		t.Fatalf("missing key destination should fail: %v %s", err, out)
	}

	certDir := filepath.Join(dir, "certs")
	keyFile := filepath.Join(dir, "control.key")
	dataFile := filepath.Join(dir, "data.key")
	cmd := exec.Command(bin, "--listen", "127.0.0.1:0", "--cert-dir", certDir, "--control-key-out", keyFile, "--seed", "--data-key-out", dataFile)
	var stdout strings.Builder
	cmd.Stdout = &stdout
	stderr, err := cmd.StderrPipe()
	if err != nil {
		t.Fatal(err)
	}
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { cmd.Process.Kill(); cmd.Wait() })

	var origin string
	var banner []string
	lines := bufio.NewScanner(stderr)
	deadline := time.AfterFunc(20*time.Second, func() { cmd.Process.Kill() })
	for lines.Scan() {
		line := lines.Text()
		banner = append(banner, line)
		if u, ok := strings.CutPrefix(line, "tbv1-fake: origin "); ok {
			origin = u
		}
		if strings.Contains(line, "ready (GET") {
			break
		}
	}
	deadline.Stop()
	go io.Copy(io.Discard, stderr)
	if origin == "" || !strings.Contains(strings.Join(banner, "\n"), "DEVELOPMENT FAKE") {
		t.Fatalf("banner: %q", banner)
	}

	for _, f := range []string{keyFile, dataFile, filepath.Join(certDir, "server-key.pem")} {
		info, err := os.Stat(f)
		if err != nil || info.Mode().Perm() != 0o600 {
			t.Fatalf("%s: mode %v, %v", f, info.Mode(), err)
		}
	}
	control, _ := os.ReadFile(keyFile)
	data, _ := os.ReadFile(dataFile)
	controlKey, dataKey := strings.TrimSpace(string(control)), strings.TrimSpace(string(data))
	if !strings.HasPrefix(controlKey, "tbk1.") || !strings.HasPrefix(dataKey, "tbk1.") {
		t.Fatalf("keys: %q %q", controlKey, dataKey)
	}
	if all := stdout.String() + strings.Join(banner, "\n"); strings.Contains(all, controlKey) || strings.Contains(all, dataKey) {
		t.Fatal("a secret reached stdout or stderr without --print-control-key")
	}

	caPEM, err := os.ReadFile(filepath.Join(certDir, "ca.pem"))
	if err != nil {
		t.Fatal(err)
	}
	pool := x509.NewCertPool()
	if !pool.AppendCertsFromPEM(caPEM) {
		t.Fatal("ca.pem is not a certificate")
	}
	client := &http.Client{Transport: &http.Transport{TLSClientConfig: &tls.Config{RootCAs: pool}, ForceAttemptHTTP2: true}}
	get := func(path, token string) (int, map[string]any, string) {
		req, _ := http.NewRequest("GET", origin+path, nil)
		if token != "" {
			req.Header.Set("Authorization", "Bearer "+token)
		}
		res, err := client.Do(req)
		if err != nil {
			t.Fatal(err)
		}
		defer res.Body.Close()
		var body map[string]any
		json.NewDecoder(res.Body).Decode(&body)
		return res.StatusCode, body, res.Proto
	}
	if status, _, proto := get("/v1/readyz", ""); status != 200 || proto != "HTTP/2.0" {
		t.Fatalf("readyz: %d over %s", status, proto)
	}
	if status, body, _ := get("/v1/dashboard/tenants", controlKey); status != 200 || len(body["items"].([]any)) != 1 {
		t.Fatalf("tenants: %d %v", status, body)
	}
	if status, body, _ := get("/v1/databases/main/collections/users/documents/0192f0c4-7a1e-7c3d-9b2a-5e8f1d2c3b4a", dataKey); status != 404 {
		t.Fatalf("data key: %d %v", status, body)
	}

	if err := cmd.Process.Signal(syscall.SIGTERM); err != nil {
		t.Fatal(err)
	}
	done := make(chan error, 1)
	go func() { done <- cmd.Wait() }()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("exit: %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("did not exit on SIGTERM")
	}

	// A restart reuses the certificates instead of regenerating them.
	out, err := exec.Command(bin, "--listen", "127.0.0.1:0", "--cert-dir", certDir, "--print-control-key", "--edge-listen", "bogus").CombinedOutput()
	if err == nil || strings.Contains(string(out), "generated") {
		t.Fatalf("restart with a bad edge address: %v %s", err, out)
	}
	if again, _ := os.ReadFile(filepath.Join(certDir, "ca.pem")); string(again) != string(caPEM) {
		t.Fatal("ca.pem was regenerated")
	}
}
