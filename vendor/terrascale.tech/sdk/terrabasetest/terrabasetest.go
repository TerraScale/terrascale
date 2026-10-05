// Package terrabasetest starts the pinned TerraBase release executable for
// tests. Every test of a TerraBase client runs against this real server, never
// a fake.
//
// The executable is the release Release of terrabase commit Commit, verified
// against SHA256. It is cached in $TERRABASE_CACHE (default: the user cache
// directory) and downloaded on first use with TERRABASE_TOKEN, a token that
// can read TerraScale/terrabase. Without the executable and a token, Start
// skips the test; TERRABASE_REQUIRE=1 (set in CI) fails it instead.
// Integrity, download and filesystem errors always fail the test.
//
// To move the pin, take the ci-<sha>-run-<id> prerelease of a terrabase main
// commit, update the three constants and re-pin v1/testdata/openapi.json (see
// v1/integration_test.go).
package terrabasetest

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math/big"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"
)

// The pinned TerraBase release.
const (
	Commit  = "ff256067015433c8c40b59879f1282d2a40480ce"
	Release = "ci-" + Commit + "-run-13258"
	SHA256  = "e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13"
)

const downloadURL = "https://git.terrascale.tech/TerraScale/terrabase/releases/download/" + Release + "/terrabase-linux-amd64"

// Server is a running TerraBase with a fresh data directory.
type Server struct {
	// URL is the HTTPS client listener (documents and Management API).
	URL string
	// AdminURL is the loopback administrative listener (API keys, OpenAPI).
	AdminURL string
	// Credential is the dashboard service credential for the Management API.
	Credential string
	// Client trusts the server's certificate. Copy it to wrap its Transport.
	Client *http.Client
}

var (
	errPrerequisiteUnavailable = errors.New("TerraBase prerequisite unavailable")
	binOnce                    sync.Once
	binPath                    string
	binErr                     error
)

// Binary returns the path of the verified pinned executable, downloading it
// into the cache when it is missing.
func Binary() (string, error) {
	binOnce.Do(func() { binPath, binErr = binary() })
	return binPath, binErr
}

func binary() (string, error) {
	if runtime.GOOS != "linux" || runtime.GOARCH != "amd64" {
		return "", fmt.Errorf("%w: the release is built for linux/amd64 only, not %s/%s", errPrerequisiteUnavailable, runtime.GOOS, runtime.GOARCH)
	}
	dir := os.Getenv("TERRABASE_CACHE")
	if dir == "" {
		cache, err := os.UserCacheDir()
		if err != nil {
			return "", err
		}
		dir = filepath.Join(cache, "terrascale", "terrabase")
	}
	path := filepath.Join(dir, "terrabase-"+Commit)
	if data, err := os.ReadFile(path); err == nil {
		if digest(data) != SHA256 {
			return "", fmt.Errorf("%s does not match the pinned SHA-256; delete it", path)
		}
		return path, nil
	} else if !errors.Is(err, os.ErrNotExist) {
		return "", fmt.Errorf("read TerraBase cache: %w", err)
	}
	token := os.Getenv("TERRABASE_TOKEN")
	if token == "" {
		return "", fmt.Errorf("%w: %s is not cached and TERRABASE_TOKEN is not set", errPrerequisiteUnavailable, path)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, downloadURL, nil)
	if err != nil {
		return "", err
	}
	req.Header.Set("Authorization", "token "+token)
	res, err := http.DefaultClient.Do(req)
	if err != nil {
		return "", fmt.Errorf("download TerraBase %s: %w", Release, err)
	}
	defer res.Body.Close()
	if res.StatusCode != http.StatusOK {
		return "", fmt.Errorf("download TerraBase %s: %s", Release, res.Status)
	}
	data, err := io.ReadAll(res.Body)
	if err != nil {
		return "", fmt.Errorf("download TerraBase %s: %w", Release, err)
	}
	if digest(data) != SHA256 {
		return "", fmt.Errorf("download TerraBase %s: SHA-256 mismatch", Release)
	}
	if err := os.MkdirAll(dir, 0o755); err != nil {
		return "", err
	}
	// Rename publishes the executable atomically to parallel test processes.
	tmp, err := os.CreateTemp(dir, ".terrabase-*")
	if err != nil {
		return "", err
	}
	defer os.Remove(tmp.Name())
	if _, err := tmp.Write(data); err != nil {
		tmp.Close()
		return "", err
	}
	if err := tmp.Close(); err != nil {
		return "", err
	}
	if err := os.Chmod(tmp.Name(), 0o755); err != nil {
		return "", err
	}
	return path, os.Rename(tmp.Name(), path)
}

func digest(data []byte) string {
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}

// Start runs the pinned TerraBase until the test ends, with a TLS certificate
// and a dashboard credential file generated for it.
func Start(t testing.TB) *Server {
	t.Helper()
	bin, err := Binary()
	if err != nil {
		if errors.Is(err, errPrerequisiteUnavailable) && os.Getenv("TERRABASE_REQUIRE") != "1" {
			t.Skipf("TerraBase: %v", err)
		}
		t.Fatalf("TerraBase: %v", err)
	}
	dir := t.TempDir()
	certFile, keyFile, pool := writeCertificate(t, dir)
	credential := rand.Text() + rand.Text()
	credFile := filepath.Join(dir, "dashboard-credential")
	if err := os.WriteFile(credFile, []byte(credential+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	var log syncBuffer
	cmd := exec.Command(bin,
		"--admin-port=0", "--client-port=0",
		"--client-certificate="+certFile, "--client-private-key="+keyFile,
		"--dashboard-credential-file="+credFile, "--data-directory="+filepath.Join(dir, "data"))
	cmd.Dir, cmd.Stdout, cmd.Stderr = dir, &log, &log
	if err := cmd.Start(); err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		cmd.Process.Signal(os.Interrupt)
		done := make(chan struct{})
		go func() { cmd.Wait(); close(done) }()
		select {
		case <-done:
		case <-time.After(15 * time.Second):
			cmd.Process.Kill()
			<-done
		}
		if t.Failed() {
			t.Logf("terrabase output:\n%s", log.String())
		}
	})
	s := &Server{
		Credential: credential,
		Client: &http.Client{Transport: &http.Transport{
			TLSClientConfig:   &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS13},
			ForceAttemptHTTP2: true,
		}},
	}
	ctx, cancel := context.WithTimeout(t.Context(), 30*time.Second)
	defer cancel()
	for {
		// Port zero lets the server reserve its own ports without a bind race.
		for line := range strings.SplitSeq(log.String(), "\n") {
			if raw, ok := strings.CutPrefix(strings.TrimSpace(line), "Now listening on: "); ok {
				u, err := url.Parse(raw)
				if err != nil || u.Port() == "" {
					continue
				}
				switch u.Scheme {
				case "http":
					s.AdminURL = "http://127.0.0.1:" + u.Port()
				case "https":
					s.URL = "https://localhost:" + u.Port()
				}
			}
		}
		if s.AdminURL != "" && s.URL != "" {
			status, _, err := s.adminCall(ctx, http.MethodGet, "/healthz", nil)
			if err == nil && status == http.StatusOK {
				return s
			}
		}
		select {
		case <-ctx.Done():
			t.Fatalf("TerraBase did not become healthy: %v\n%s", ctx.Err(), log.String())
		case <-time.After(100 * time.Millisecond):
		}
	}
}

// APIKey issues a tb_ API key for an organization, narrowed to project when
// it is not empty, on the loopback administrative listener.
func (s *Server) APIKey(t testing.TB, org, project string, scopes ...string) string {
	t.Helper()
	req := map[string]any{"name": "sdk-test", "scopes": scopes, "organizationId": org}
	if project != "" {
		req["projectId"] = project
	}
	body, _ := json.Marshal(req)
	status, data, err := s.adminCall(t.Context(), http.MethodPost, "/admin/api-keys", body)
	if err != nil {
		t.Fatal(err)
	}
	if status != http.StatusCreated {
		t.Fatalf("create API key: %d %s", status, data)
	}
	var key struct{ Secret string }
	if err := json.Unmarshal(data, &key); err != nil {
		t.Fatal(err)
	}
	return key.Secret
}

// adminCall bounds both response headers and body reads, including during startup.
func (s *Server) adminCall(ctx context.Context, method, path string, body []byte) (int, []byte, error) {
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, method, s.AdminURL+path, bytes.NewReader(body))
	if err != nil {
		return 0, nil, err
	}
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	res, err := s.Client.Do(req)
	if err != nil {
		return 0, nil, err
	}
	defer res.Body.Close()
	data, err := io.ReadAll(res.Body)
	return res.StatusCode, data, err
}

// writeCertificate writes a self-signed certificate for localhost and returns
// the files and a pool trusting it.
func writeCertificate(t testing.TB, dir string) (certFile, keyFile string, pool *x509.CertPool) {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	tpl := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "localhost"},
		DNSNames:              []string{"localhost"},
		IPAddresses:           []net.IP{net.IPv4(127, 0, 0, 1)},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(24 * time.Hour),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		IsCA:                  true,
		BasicConstraintsValid: true,
	}
	der, err := x509.CreateCertificate(rand.Reader, tpl, tpl, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	keyDER, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		t.Fatal(err)
	}
	certFile, keyFile = filepath.Join(dir, "cert.pem"), filepath.Join(dir, "key.pem")
	certPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der})
	if err := os.WriteFile(certFile, certPEM, 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(keyFile, pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: keyDER}), 0o600); err != nil {
		t.Fatal(err)
	}
	pool = x509.NewCertPool()
	pool.AppendCertsFromPEM(certPEM)
	return certFile, keyFile, pool
}

// syncBuffer collects the server's output, written from exec's copy goroutine.
type syncBuffer struct {
	mu sync.Mutex
	b  bytes.Buffer
}

func (s *syncBuffer) Write(p []byte) (int, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.b.Write(p)
}

func (s *syncBuffer) String() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.b.String()
}
