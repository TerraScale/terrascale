package main

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
	"encoding/base64"
	"encoding/hex"
	"encoding/json/v2"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"math/big"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"

	"github.com/oklog/ulid/v2"
)

const (
	platformAuthProject   = "00000000000000000000000002"
	platformAuthDatabase  = "ts-auth"
	crockfordLower        = "0123456789abcdefghjkmnpqrstvwxyz"
	linuxClockTicksPerSec = 100.0
)

type stackTarget struct {
	terrabaseURL string
	authURL      string
	dashboardURL string
	apiKey       string
	project      string
}

type resourceTelemetry struct {
	CurrentRSSMiB float64
	PeakRSSMiB    float64
	CPUTimeMs     float64
	DiskBytes     int64
}

type managedProc struct {
	name    string
	cmd     *exec.Cmd
	logFile *os.File
	logPath string
	done    chan struct{}
	exitErr error
}

type localStack struct {
	target    stackTarget
	tempDir   string
	tbDataDir string
	tbProc    *managedProc
	tlsProxy  *http.Server
	procs     []*managedProc
}

func startLocalStack(ctx context.Context, cfg config) (*localStack, error) {
	repoRoot := filepath.Dir(benchmarkRoot())
	tbPath, err := resolveServiceBinary(cfg.terrabaseBin, repoRoot, []string{
		"src/TerraBase/bin/Release/net11.0/terrabase",
		"src/TerraBase/bin/Release/net11.0/terrabase.dll",
		"src/TerraBase/bin/Debug/net11.0/terrabase",
		"src/TerraBase/bin/Debug/net11.0/terrabase.dll",
		"src/TerraBase/bin/Release/net11.0/linux-x64/terrabase",
		"src/TerraBase/bin/Release/net11.0/linux-arm64/terrabase",
	})
	if err != nil {
		return nil, fmt.Errorf("locate TerraBase binary: %w", err)
	}
	authPath, err := resolveServiceBinary(cfg.authDLL, repoRoot, []string{
		"src/TerraScale.Authentication/bin/Release/net11.0/ts-auth.dll",
		"src/TerraScale.Authentication/bin/Release/net11.0/ts-auth",
		"src/TerraScale.Authentication/bin/Debug/net11.0/ts-auth.dll",
		"src/TerraScale.Authentication/bin/Debug/net11.0/ts-auth",
	})
	if err != nil {
		return nil, fmt.Errorf("locate ts-auth binary: %w", err)
	}
	dashPath, err := resolveServiceBinary(cfg.dashboardDLL, repoRoot, []string{
		"src/TerraScale.Dashboard/bin/Release/net11.0/TerraScale.Dashboard.dll",
		"src/TerraScale.Dashboard/bin/Release/net11.0/TerraScale.Dashboard",
		"src/TerraScale.Dashboard/bin/Debug/net11.0/TerraScale.Dashboard.dll",
		"src/TerraScale.Dashboard/bin/Debug/net11.0/TerraScale.Dashboard",
	})
	if err != nil {
		return nil, fmt.Errorf("locate TerraScale.Dashboard binary: %w", err)
	}

	tempDir, err := os.MkdirTemp("", "ts-bench-*")
	if err != nil {
		return nil, fmt.Errorf("create temp dir: %w", err)
	}
	stack := &localStack{tempDir: tempDir}
	ok := false
	defer func() {
		if !ok {
			stack.close()
		}
	}()

	ports, err := reservePorts(8)
	if err != nil {
		return nil, err
	}
	tbPublic, tbInternal, tbMetrics, tbPostgres := ports[0], ports[1], ports[2], ports[3]
	authHTTP, authMetrics, authTLS, dashHTTP := ports[4], ports[5], ports[6], ports[7]

	peerSecret, err := randomHex(32)
	if err != nil {
		return nil, err
	}
	tbMasterKey, err := randomBase64(32)
	if err != nil {
		return nil, err
	}
	authMasterKey, err := randomBase64(32)
	if err != nil {
		return nil, err
	}
	authAuditKey, err := randomBase64(32)
	if err != nil {
		return nil, err
	}
	dashClientSecret, err := randomHex(32)
	if err != nil {
		return nil, err
	}
	dashSessionKey, err := randomBase64(32)
	if err != nil {
		return nil, err
	}
	stripeSecretHex, err := randomHex(24)
	if err != nil {
		return nil, err
	}
	stripeWebhookHex, err := randomHex(24)
	if err != nil {
		return nil, err
	}
	stripeSecretKey := "sk_test_" + stripeSecretHex
	stripeWebhookSecret := "whsec_" + stripeWebhookHex

	caPath := filepath.Join(tempDir, "ca.pem")
	tlsCert, err := generateLoopbackPKI(caPath)
	if err != nil {
		return nil, fmt.Errorf("generate ephemeral loopback PKI: %w", err)
	}

	authHTTPURL := fmt.Sprintf("http://127.0.0.1:%d", authHTTP)
	authTLSOrigin := fmt.Sprintf("https://127.0.0.1:%d", authTLS)
	tbPublicURL := fmt.Sprintf("http://127.0.0.1:%d", tbPublic)
	tbInternalURL := fmt.Sprintf("http://127.0.0.1:%d", tbInternal)
	dashHTTPURL := fmt.Sprintf("http://127.0.0.1:%d", dashHTTP)

	proxyServer, err := startAuthTLSProxy(authTLS, authHTTPURL, tlsCert)
	if err != nil {
		return nil, fmt.Errorf("start auth TLS proxy: %w", err)
	}
	stack.tlsProxy = proxyServer

	tbDataDir := filepath.Join(tempDir, "terrabase")
	if err := os.MkdirAll(tbDataDir, 0o700); err != nil {
		return nil, err
	}
	stack.tbDataDir = tbDataDir

	fmt.Fprintf(os.Stderr, "ts-bench: starting TerraBase on %s (internal %s)...\n", tbPublicURL, tbInternalURL)
	tbProc, err := startProcess("terrabase", tbPath, nil, mergeEnv(
		"DOTNET_ENVIRONMENT=Development",
		"TERRABASE_DATA_DIRECTORY="+tbDataDir,
		"TERRABASE_PORT="+strconv.Itoa(tbPublic),
		"TERRABASE_INTERNAL_PORT="+strconv.Itoa(tbInternal),
		"TERRABASE_METRICS_PORT="+strconv.Itoa(tbMetrics),
		"TERRABASE_POSTGRES_PORT="+strconv.Itoa(tbPostgres),
		"TERRABASE_PEER_SECRET="+peerSecret,
		"TERRABASE_MASTER_KEY="+tbMasterKey,
		"TERRABASE_AUTH_ISSUER="+authTLSOrigin+"/terrascale",
		"TERRABASE_AUTH_AUDIENCE=https://api.terrascale.tech",
		"SSL_CERT_FILE="+caPath,
	), filepath.Join(tempDir, "terrabase.log"))
	if err != nil {
		return nil, err
	}
	stack.tbProc = tbProc
	stack.procs = append(stack.procs, tbProc)
	if err := waitReady(ctx, tbPublicURL+"/readyz", tbProc, 60*time.Second); err != nil {
		return nil, err
	}

	fmt.Fprintf(os.Stderr, "ts-bench: starting ts-auth on %s (issuer %s/terrascale)...\n", authHTTPURL, authTLSOrigin)
	authProc, err := startProcess("ts-auth", authPath, nil, mergeEnv(
		"TS_AUTH_ENVIRONMENT=Development",
		"TS_AUTH_MASTER_KEY="+authMasterKey,
		"TS_AUTH_AUDIT_SIGNING_KEY="+authAuditKey,
		"TERRABASE_PEER_SECRET="+peerSecret,
		"TS_AUTH_TERRABASE_URL="+tbInternalURL,
		"TS_AUTH_LISTEN="+authHTTPURL,
		"TS_AUTH_METRICS_LISTEN="+fmt.Sprintf("http://127.0.0.1:%d", authMetrics),
		"TS_AUTH_PUBLIC_ORIGIN="+authTLSOrigin,
		"TS_AUTH_DASHBOARD_CLIENT_SECRET="+dashClientSecret,
		"TS_AUTH_DASHBOARD_REDIRECT_URIS="+dashHTTPURL+"/login/callback",
		"TS_AUTH_DASHBOARD_POST_LOGOUT_REDIRECT_URIS="+dashHTTPURL+"/login",
		"TS_AUTH_RATE_LIMIT=100000",
	), filepath.Join(tempDir, "ts-auth.log"))
	if err != nil {
		return nil, err
	}
	stack.procs = append(stack.procs, authProc)
	if err := waitReady(ctx, authHTTPURL+"/readyz", authProc, 60*time.Second); err != nil {
		return nil, err
	}

	fmt.Fprintf(os.Stderr, "ts-bench: starting TerraScale.Dashboard on %s...\n", dashHTTPURL)
	dashArgs := []string{"--contentRoot", filepath.Dir(dashPath)}
	dashProc, err := startProcess("dashboard", dashPath, dashArgs, mergeEnv(
		"ASPNETCORE_ENVIRONMENT=Development",
		"ASPNETCORE_URLS="+dashHTTPURL,
		"TERRABASE_PEER_SECRET="+peerSecret,
		"TS_AUTH_DASHBOARD_CLIENT_SECRET="+dashClientSecret,
		"TS_DASHBOARD_SESSION_ENCRYPTION_KEY="+dashSessionKey,
		"STRIPE_SECRET_KEY="+stripeSecretKey,
		"STRIPE_WEBHOOK_SECRET="+stripeWebhookSecret,
		"TS_DASHBOARD_TERRABASE_URL="+tbInternalURL,
	), filepath.Join(tempDir, "dashboard.log"))
	if err != nil {
		return nil, err
	}
	stack.procs = append(stack.procs, dashProc)
	if err := waitReady(ctx, dashHTTPURL+"/healthz", dashProc, 60*time.Second); err != nil {
		return nil, err
	}

	projectID, apiKey, accessToken, err := bootstrapProjectAndAPIKey(ctx, tbInternalURL, authHTTPURL, peerSecret)
	if err != nil {
		return nil, err
	}
	if err := waitTerraBaseAcceptsToken(ctx, tbPublicURL, projectID, accessToken, tbProc, 30*time.Second); err != nil {
		return nil, err
	}

	stack.target = stackTarget{
		terrabaseURL: tbPublicURL,
		authURL:      authHTTPURL,
		dashboardURL: dashHTTPURL,
		apiKey:       apiKey,
		project:      projectID,
	}
	ok = true
	return stack, nil
}

func (s *localStack) captureTelemetry() (resourceTelemetry, error) {
	if s.tbProc == nil || s.tbProc.cmd == nil || s.tbProc.cmd.Process == nil {
		return resourceTelemetry{}, errors.New("terrabase process not running")
	}
	pid := s.tbProc.cmd.Process.Pid
	currentRSSMiB, peakRSSMiB, err := readProcMemoryMiB(pid)
	if err != nil {
		return resourceTelemetry{}, err
	}
	cpuTimeMs, err := readProcCPUTimeMs(pid)
	if err != nil {
		return resourceTelemetry{}, err
	}
	diskBytes, err := dirSizeBytes(s.tbDataDir)
	if err != nil {
		return resourceTelemetry{}, err
	}
	return resourceTelemetry{
		CurrentRSSMiB: currentRSSMiB,
		PeakRSSMiB:    peakRSSMiB,
		CPUTimeMs:     cpuTimeMs,
		DiskBytes:     diskBytes,
	}, nil
}

func readProcMemoryMiB(pid int) (float64, float64, error) {
	data, err := os.ReadFile(fmt.Sprintf("/proc/%d/status", pid))
	if err != nil {
		return 0, 0, fmt.Errorf("read /proc/%d/status: %w", pid, err)
	}
	var currentKB, peakKB uint64
	var haveCurrent, havePeak bool
	for line := range strings.SplitSeq(string(data), "\n") {
		if rest, ok := strings.CutPrefix(line, "VmRSS:"); ok {
			val, err := parseProcKB(rest)
			if err != nil {
				return 0, 0, fmt.Errorf("parse VmRSS: %w", err)
			}
			currentKB = val
			haveCurrent = true
		} else if rest, ok := strings.CutPrefix(line, "VmHWM:"); ok {
			val, err := parseProcKB(rest)
			if err != nil {
				return 0, 0, fmt.Errorf("parse VmHWM: %w", err)
			}
			peakKB = val
			havePeak = true
		}
	}
	if !haveCurrent || !havePeak {
		return 0, 0, fmt.Errorf("/proc/%d/status missing VmRSS or VmHWM", pid)
	}
	return float64(currentKB) / 1024.0, float64(peakKB) / 1024.0, nil
}

func parseProcKB(raw string) (uint64, error) {
	fields := strings.Fields(raw)
	if len(fields) == 0 {
		return 0, errors.New("empty value")
	}
	return strconv.ParseUint(fields[0], 10, 64)
}

func readProcCPUTimeMs(pid int) (float64, error) {
	data, err := os.ReadFile(fmt.Sprintf("/proc/%d/stat", pid))
	if err != nil {
		return 0, fmt.Errorf("read /proc/%d/stat: %w", pid, err)
	}
	_, after, ok := strings.CutLast(strings.TrimSpace(string(data)), ") ")
	if !ok {
		return 0, fmt.Errorf("malformed /proc/%d/stat", pid)
	}
	fields := strings.Fields(after)
	if len(fields) <= 12 {
		return 0, fmt.Errorf("incomplete /proc/%d/stat fields", pid)
	}
	utime, err := strconv.ParseUint(fields[11], 10, 64)
	if err != nil {
		return 0, fmt.Errorf("parse utime: %w", err)
	}
	stime, err := strconv.ParseUint(fields[12], 10, 64)
	if err != nil {
		return 0, fmt.Errorf("parse stime: %w", err)
	}
	return float64(utime+stime) * 1000.0 / linuxClockTicksPerSec, nil
}

func dirSizeBytes(root string) (int64, error) {
	var total int64
	err := filepath.WalkDir(root, func(_ string, d fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			if errors.Is(walkErr, os.ErrNotExist) {
				return nil
			}
			return walkErr
		}
		if d.IsDir() {
			return nil
		}
		info, err := d.Info()
		if err != nil {
			if errors.Is(err, os.ErrNotExist) {
				return nil
			}
			return err
		}
		total += info.Size()
		return nil
	})
	if err != nil {
		return 0, fmt.Errorf("walk %s: %w", root, err)
	}
	return total, nil
}

func (s *localStack) close() {
	if s.tlsProxy != nil {
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		_ = s.tlsProxy.Shutdown(shutdownCtx)
		cancel()
	}
	for i := len(s.procs) - 1; i >= 0; i-- {
		s.procs[i].stop()
	}
	if s.tempDir != "" {
		_ = os.RemoveAll(s.tempDir)
	}
}

func (p *managedProc) stop() {
	if p.cmd != nil && p.cmd.Process != nil {
		_ = p.cmd.Process.Signal(syscall.SIGTERM)
		select {
		case <-p.done:
		case <-time.After(5 * time.Second):
			_ = p.cmd.Process.Kill()
			<-p.done
		}
	}
	if p.logFile != nil {
		_ = p.logFile.Close()
	}
}

func resolveServiceBinary(explicit, repoRoot string, candidates []string) (string, error) {
	if explicit != "" {
		if _, err := os.Stat(explicit); err != nil {
			return "", err
		}
		return explicit, nil
	}
	for _, rel := range candidates {
		full := filepath.Join(repoRoot, rel)
		if info, err := os.Stat(full); err == nil && !info.IsDir() {
			return full, nil
		}
	}
	return "", fmt.Errorf("checked %s (build the service first or pass the flag)", strings.Join(candidates, ", "))
}

func startProcess(name, path string, args, env []string, logPath string) (*managedProc, error) {
	logFile, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
	if err != nil {
		return nil, err
	}
	var cmd *exec.Cmd
	if strings.HasSuffix(path, ".dll") {
		cmd = exec.Command("dotnet", append([]string{path}, args...)...)
	} else {
		cmd = exec.Command(path, args...)
	}
	cmd.Env = env
	cmd.Stdout = logFile
	cmd.Stderr = logFile
	if err := cmd.Start(); err != nil {
		_ = logFile.Close()
		return nil, fmt.Errorf("start %s: %w", name, err)
	}
	proc := &managedProc{
		name:    name,
		cmd:     cmd,
		logFile: logFile,
		logPath: logPath,
		done:    make(chan struct{}),
	}
	go func() {
		proc.exitErr = cmd.Wait()
		close(proc.done)
	}()
	return proc, nil
}

func mergeEnv(overrides ...string) []string {
	envMap := make(map[string]string)
	for _, entry := range os.Environ() {
		if k, v, ok := strings.Cut(entry, "="); ok {
			envMap[k] = v
		}
	}
	for _, entry := range overrides {
		if k, v, ok := strings.Cut(entry, "="); ok {
			envMap[k] = v
		}
	}
	out := make([]string, 0, len(envMap))
	for k, v := range envMap {
		out = append(out, k+"="+v)
	}
	return out
}

func waitReady(ctx context.Context, targetURL string, proc *managedProc, timeout time.Duration) error {
	client := &http.Client{Timeout: 2 * time.Second}
	deadline := time.Now().Add(timeout)
	ticker := time.NewTicker(100 * time.Millisecond)
	defer ticker.Stop()

	for {
		select {
		case <-proc.done:
			return fmt.Errorf("%s exited early (%v):\n%s", proc.name, proc.exitErr, readTail(proc.logPath, 4096))
		case <-ctx.Done():
			return ctx.Err()
		default:
		}
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, targetURL, nil)
		if err != nil {
			return err
		}
		resp, err := client.Do(req)
		if err == nil {
			_, _ = io.Copy(io.Discard, resp.Body)
			_ = resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return nil
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("%s did not become ready at %s within %s:\n%s", proc.name, targetURL, timeout, readTail(proc.logPath, 4096))
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-proc.done:
			return fmt.Errorf("%s exited early (%v):\n%s", proc.name, proc.exitErr, readTail(proc.logPath, 4096))
		case <-ticker.C:
		}
	}
}

func waitTerraBaseAcceptsToken(ctx context.Context, tbURL, projectID, token string, proc *managedProc, timeout time.Duration) error {
	client := &http.Client{Timeout: 2 * time.Second}
	checkURL := tbURL + "/v1/projects/" + projectID + "/databases"
	deadline := time.Now().Add(timeout)
	ticker := time.NewTicker(200 * time.Millisecond)
	defer ticker.Stop()

	for {
		select {
		case <-proc.done:
			return fmt.Errorf("terrabase exited early (%v):\n%s", proc.exitErr, readTail(proc.logPath, 4096))
		case <-ctx.Done():
			return ctx.Err()
		default:
		}
		req, err := http.NewRequestWithContext(ctx, http.MethodGet, checkURL, nil)
		if err != nil {
			return err
		}
		req.Header.Set("Authorization", "Bearer "+token)
		resp, err := client.Do(req)
		if err == nil {
			_, _ = io.Copy(io.Discard, resp.Body)
			_ = resp.Body.Close()
			if resp.StatusCode == http.StatusOK {
				return nil
			}
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("terrabase did not accept ts-auth token within %s:\n%s", timeout, readTail(proc.logPath, 4096))
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-ticker.C:
		}
	}
}

func readTail(path string, maxBytes int64) string {
	data, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	if int64(len(data)) > maxBytes {
		data = data[int64(len(data))-maxBytes:]
	}
	return string(data)
}

func reservePorts(count int) ([]int, error) {
	listeners := make([]net.Listener, 0, count)
	ports := make([]int, 0, count)
	defer func() {
		for _, ln := range listeners {
			_ = ln.Close()
		}
	}()
	for range count {
		ln, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			return nil, fmt.Errorf("reserve loopback port: %w", err)
		}
		listeners = append(listeners, ln)
		ports = append(ports, ln.Addr().(*net.TCPAddr).Port)
	}
	return ports, nil
}

func generateLoopbackPKI(caPath string) (tls.Certificate, error) {
	caKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	now := time.Now().UTC()
	caTmpl := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "ts-bench-ephemeral-ca"},
		NotBefore:             now.Add(-time.Hour),
		NotAfter:              now.Add(24 * time.Hour),
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageCRLSign,
		BasicConstraintsValid: true,
		IsCA:                  true,
	}
	caDER, err := x509.CreateCertificate(rand.Reader, caTmpl, caTmpl, &caKey.PublicKey, caKey)
	if err != nil {
		return tls.Certificate{}, err
	}
	caPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: caDER})
	if err := os.WriteFile(caPath, caPEM, 0o600); err != nil {
		return tls.Certificate{}, err
	}

	leafKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	leafTmpl := &x509.Certificate{
		SerialNumber: big.NewInt(2),
		Subject:      pkix.Name{CommonName: "127.0.0.1"},
		NotBefore:    now.Add(-time.Hour),
		NotAfter:     now.Add(24 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		IPAddresses:  []net.IP{net.ParseIP("127.0.0.1")},
		DNSNames:     []string{"localhost"},
	}
	leafDER, err := x509.CreateCertificate(rand.Reader, leafTmpl, caTmpl, &leafKey.PublicKey, caKey)
	if err != nil {
		return tls.Certificate{}, err
	}
	leafKeyDER, err := x509.MarshalECPrivateKey(leafKey)
	if err != nil {
		return tls.Certificate{}, err
	}
	leafCertPEM := pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: leafDER})
	leafKeyPEM := pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: leafKeyDER})
	return tls.X509KeyPair(leafCertPEM, leafKeyPEM)
}

func startAuthTLSProxy(port int, upstream string, cert tls.Certificate) (*http.Server, error) {
	target, err := url.Parse(upstream)
	if err != nil {
		return nil, err
	}
	proxy := httputil.NewSingleHostReverseProxy(target)
	origDirector := proxy.Director
	proxy.Director = func(req *http.Request) {
		origDirector(req)
		req.Header.Set("X-Forwarded-Proto", "https")
	}
	ln, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", port))
	if err != nil {
		return nil, err
	}
	tlsLn := tls.NewListener(ln, &tls.Config{
		MinVersion:   tls.VersionTLS12,
		Certificates: []tls.Certificate{cert},
	})
	srv := &http.Server{Handler: proxy}
	go func() {
		_ = srv.Serve(tlsLn)
	}()
	return srv, nil
}

type internalSQLStatement struct {
	SQL  string `json:"sql"`
	Args []any  `json:"args,omitempty"`
}

type internalSQLRequest struct {
	Statements []internalSQLStatement `json:"statements"`
}

type internalSQLResponse struct {
	Results []struct {
		Rows [][]any `json:"rows"`
	} `json:"results"`
}

func execInternalSQL(ctx context.Context, client *http.Client, tbInternalURL, peerSecret string, stmts []internalSQLStatement) (internalSQLResponse, error) {
	payload, err := json.Marshal(internalSQLRequest{Statements: stmts})
	if err != nil {
		return internalSQLResponse{}, err
	}
	endpoint := tbInternalURL + "/v1/projects/" + platformAuthProject + "/databases/" + platformAuthDatabase + "/sql/execute"
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, endpoint, bytes.NewReader(payload))
	if err != nil {
		return internalSQLResponse{}, err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-TerraBase-Peer-Credential", peerSecret)
	resp, err := client.Do(req)
	if err != nil {
		return internalSQLResponse{}, err
	}
	defer func() { _ = resp.Body.Close() }()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return internalSQLResponse{}, err
	}
	if resp.StatusCode != http.StatusOK {
		return internalSQLResponse{}, fmt.Errorf("internal SQL status %d: %s", resp.StatusCode, string(body))
	}
	var out internalSQLResponse
	if err := json.Unmarshal(body, &out); err != nil {
		return internalSQLResponse{}, err
	}
	return out, nil
}

func bootstrapProjectAndAPIKey(ctx context.Context, tbInternalURL, authHTTPURL, peerSecret string) (string, string, string, error) {
	protocols := new(http.Protocols)
	protocols.SetUnencryptedHTTP2(true)
	h2cTransport := &http.Transport{Protocols: protocols}
	defer h2cTransport.CloseIdleConnections()
	internalClient := &http.Client{
		Timeout:   10 * time.Second,
		Transport: h2cTransport,
	}
	authClient := &http.Client{Timeout: 10 * time.Second}

	ownerResp, err := execInternalSQL(ctx, internalClient, tbInternalURL, peerSecret, []internalSQLStatement{
		{SQL: "SELECT id FROM users WHERE instance_id = 'terrascale' ORDER BY id LIMIT 1"},
	})
	if err != nil {
		return "", "", "", fmt.Errorf("query platform owner: %w", err)
	}
	if len(ownerResp.Results) == 0 || len(ownerResp.Results[0].Rows) == 0 || len(ownerResp.Results[0].Rows[0]) == 0 {
		return "", "", "", errors.New("platform owner user not found in ts-auth database")
	}
	ownerID, ok := ownerResp.Results[0].Rows[0][0].(string)
	if !ok || ownerID == "" {
		return "", "", "", errors.New("invalid platform owner id")
	}

	orgID := ulid.Make().String()
	projectID := ulid.Make().String()
	keyID := ulid.Make().String()
	secretSuffix, err := randomAlphabet(crockfordLower, 52)
	if err != nil {
		return "", "", "", err
	}
	apiKey := keyID + "_" + secretSuffix
	digest := sha256.Sum256([]byte(apiKey))
	secretHash := hex.EncodeToString(digest[:])
	now := time.Now().UTC().Format("2006-01-02 15:04:05.0000000")
	projectName := "ts-bench-" + strings.ToLower(projectID[len(projectID)-6:])

	_, err = execInternalSQL(ctx, internalClient, tbInternalURL, peerSecret, []internalSQLStatement{
		{
			SQL: "INSERT INTO organizations (id, instance_id, name, created_by, created_at, owners, projects, require_mfa, version) VALUES (?, 'terrascale', ?, ?, ?, 1, 1, 0, ?)",
			Args: []any{
				orgID,
				"ts-bench",
				ownerID,
				now,
				ulid.Make().String(),
			},
		},
		{
			SQL: "INSERT INTO memberships (organization_id, user_id, instance_id, role, joined_at, version) VALUES (?, ?, 'terrascale', 'owner', ?, ?)",
			Args: []any{
				orgID,
				ownerID,
				now,
				ulid.Make().String(),
			},
		},
		{
			SQL: "INSERT INTO projects (id, instance_id, organization_id, name, name_key, created_by, created_at, version) VALUES (?, 'terrascale', ?, ?, ?, ?, ?, ?)",
			Args: []any{
				projectID,
				orgID,
				projectName,
				strings.ToUpper(projectName),
				ownerID,
				now,
				ulid.Make().String(),
			},
		},
		{
			SQL: "INSERT INTO api_keys (id, instance_id, organization_id, project_id, name, scopes, secret_hash, secret_bindings, previous_secret_bindings, allowed_cidrs, created_by, created_at, version) VALUES (?, 'terrascale', ?, ?, 'ts-bench', '[\"databases:read\",\"databases:write\",\"sql:read\",\"sql:write\"]', ?, '[]', '[]', '[]', ?, ?, ?)",
			Args: []any{
				keyID,
				orgID,
				projectID,
				secretHash,
				ownerID,
				now,
				ulid.Make().String(),
			},
		},
	})
	if err != nil {
		return "", "", "", fmt.Errorf("seed benchmark project and API key: %w", err)
	}

	if err := provisionOrganization(ctx, internalClient, tbInternalURL, peerSecret, orgID); err != nil {
		return "", "", "", fmt.Errorf("provision benchmark organization in TerraBase: %w", err)
	}

	if err := enrollProject(ctx, internalClient, tbInternalURL, peerSecret, projectID, orgID); err != nil {
		return "", "", "", fmt.Errorf("enroll benchmark project in TerraBase: %w", err)
	}

	accessToken, err := exchangeTokenDirect(ctx, authClient, authHTTPURL, keyID, apiKey, projectID)
	if err != nil {
		return "", "", "", fmt.Errorf("verify bootstrapped API key exchange: %w", err)
	}
	return projectID, apiKey, accessToken, nil
}

func provisionOrganization(ctx context.Context, client *http.Client, tbInternalURL, peerSecret, orgID string) error {
	payload, err := json.Marshal(struct {
		Plan string `json:"plan"`
	}{Plan: "enterprise"})
	if err != nil {
		return err
	}
	endpoint := tbInternalURL + "/admin/organizations/" + orgID + "/plan"
	req, err := http.NewRequestWithContext(ctx, http.MethodPut, endpoint, bytes.NewReader(payload))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-TerraBase-Peer-Credential", peerSecret)
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer func() { _ = resp.Body.Close() }()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return err
	}
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("organization plan status %d: %s", resp.StatusCode, string(body))
	}
	return nil
}

func enrollProject(ctx context.Context, client *http.Client, tbInternalURL, peerSecret, projectID, orgID string) error {
	payload, err := json.Marshal(struct {
		Organization string `json:"organization"`
	}{Organization: orgID})
	if err != nil {
		return err
	}
	endpoint := tbInternalURL + "/admin/projects/" + projectID + "/enrollment"
	req, err := http.NewRequestWithContext(ctx, http.MethodPut, endpoint, bytes.NewReader(payload))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("X-TerraBase-Peer-Credential", peerSecret)
	resp, err := client.Do(req)
	if err != nil {
		return err
	}
	defer func() { _ = resp.Body.Close() }()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return err
	}
	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("enrollment status %d: %s", resp.StatusCode, string(body))
	}
	return nil
}

func exchangeTokenDirect(ctx context.Context, client *http.Client, authURL, keyID, apiKey, projectID string) (string, error) {
	form := url.Values{
		"grant_type": {"client_credentials"},
		"project":    {projectID},
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, strings.TrimRight(authURL, "/")+"/terrascale/oauth/token", strings.NewReader(form.Encode()))
	if err != nil {
		return "", err
	}
	req.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	req.Header.Set("Authorization", "Basic "+base64.StdEncoding.EncodeToString([]byte(keyID+":"+apiKey)))
	resp, err := client.Do(req)
	if err != nil {
		return "", err
	}
	defer func() { _ = resp.Body.Close() }()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return "", err
	}
	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("token endpoint status %d: %s", resp.StatusCode, string(body))
	}
	var payload struct {
		AccessToken string `json:"access_token"`
	}
	if err := json.Unmarshal(body, &payload); err != nil {
		return "", err
	}
	if payload.AccessToken == "" {
		return "", errors.New("token endpoint returned empty access_token")
	}
	return payload.AccessToken, nil
}

func randomHex(nBytes int) (string, error) {
	buf := make([]byte, nBytes)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	return hex.EncodeToString(buf), nil
}

func randomBase64(nBytes int) (string, error) {
	buf := make([]byte, nBytes)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	return base64.StdEncoding.EncodeToString(buf), nil
}

func randomAlphabet(alphabet string, length int) (string, error) {
	buf := make([]byte, length)
	if _, err := rand.Read(buf); err != nil {
		return "", err
	}
	out := make([]byte, length)
	for i, b := range buf {
		out[i] = alphabet[int(b)%len(alphabet)]
	}
	return string(out), nil
}
