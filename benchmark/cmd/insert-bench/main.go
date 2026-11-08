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
	"encoding/json"
	"encoding/pem"
	"errors"
	"flag"
	"fmt"
	"io"
	"math"
	"math/big"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"runtime"
	"sort"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/jackc/pgx/v5"
	"github.com/oklog/ulid/v2"
)

const (
	apiHost     = "api.terrascale.tech"
	platformAud = "https://" + apiHost
	allScopes   = "databases:read databases:write sql:read sql:write"
	tokenLife   = time.Hour
	nodeName    = "gru-1"
	s3Bucket    = "insertbench"
	shapesHost  = "6cd3f3639e928077593d9b470765efeb.r2.cloudflarestorage.com"
	dbName      = "insertbench"
)

type s3fake struct {
	server  *http.Server
	addr    string
	mu      sync.Mutex
	objects map[string][]byte
}

type singleConnListener struct {
	conn net.Conn
	done chan struct{}
}

func (l *singleConnListener) Accept() (net.Conn, error) {
	select {
	case <-l.done:
		return nil, net.ErrClosed
	default:
		close(l.done)
		return l.conn, nil
	}
}

func (l *singleConnListener) Close() error { return nil }

func (l *singleConnListener) Addr() net.Addr { return l.conn.LocalAddr() }

func startEgressProxy(handler http.Handler, cert tls.Certificate) (*http.Server, string, error) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, "", err
	}
	server := &http.Server{Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodConnect {
			w.WriteHeader(http.StatusBadGateway)
			return
		}
		hijacker, ok := w.(http.Hijacker)
		if !ok {
			w.WriteHeader(http.StatusBadGateway)
			return
		}
		conn, _, err := hijacker.Hijack()
		if err != nil {
			return
		}
		if _, err := conn.Write([]byte("HTTP/1.1 200 Connection Established\r\n\r\n")); err != nil {
			conn.Close()
			return
		}
		tlsConn := tls.Server(conn, &tls.Config{Certificates: []tls.Certificate{cert}, MinVersion: tls.VersionTLS12})
		if err := tlsConn.Handshake(); err != nil {
			conn.Close()
			return
		}
		_ = (&http.Server{Handler: handler}).Serve(&singleConnListener{conn: tlsConn, done: make(chan struct{})})
	})}
	go server.Serve(ln)
	return server, ln.Addr().String(), nil
}

func s3Handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/"), "/")
		if len(parts) < 2 {
			writeS3Error(w, 400, "InvalidRequest")
			return
		}
		key := strings.Join(parts[1:], "/")
		bucket := parts[0]
		objects.s3(w, r, bucket, key)
	})
	return mux
}

var objects = &s3fake{objects: map[string][]byte{}}

func (f *s3fake) s3(w http.ResponseWriter, r *http.Request, bucket, key string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	switch {
	case r.Method == http.MethodPut && key != "":
		data, err := io.ReadAll(io.LimitReader(r.Body, 1<<30))
		if err != nil {
			w.WriteHeader(400)
			return
		}
		f.objects[bucket+"/"+key] = data
		w.Header().Set("ETag", `"`+fmt.Sprintf("%x", sha256.Sum256(data))[:32]+`"`)
		w.WriteHeader(200)
	case r.Method == http.MethodGet && key != "":
		data, ok := f.objects[bucket+"/"+key]
		if !ok {
			writeS3Error(w, 404, "NoSuchKey")
			return
		}
		w.Header().Set("ETag", `"`+fmt.Sprintf("%x", sha256.Sum256(data))[:32]+`"`)
		w.WriteHeader(200)
		_, _ = w.Write(data)
	case r.Method == http.MethodDelete && key != "":
		delete(f.objects, bucket+"/"+key)
		w.WriteHeader(204)
	case r.Method == http.MethodGet && key == "" && r.URL.Query().Get("list-type") == "2":
		prefix := r.URL.Query().Get("prefix")
		start := r.URL.Query().Get("start-after")
		keys := make([]string, 0, len(f.objects))
		for k := range f.objects {
			if strings.HasPrefix(k, bucket+"/"+prefix) && strings.TrimPrefix(k, bucket+"/") > start {
				keys = append(keys, k)
			}
		}
		sort.Strings(keys)
		w.Header().Set("Content-Type", "application/xml")
		fmt.Fprintf(w, "<?xml version=\"1.0\"?><ListBucketResult><Name>%s</Name><Prefix>%s</Prefix><KeyCount>%d</KeyCount><MaxKeys>1000</MaxKeys><IsTruncated>false</IsTruncated>",
			bucket, prefix, len(keys))
		for _, k := range keys {
			fmt.Fprintf(w, "<Contents><Key>%s</Key><LastModified>2026-01-01T00:00:00.000Z</LastModified><ETag>&quot;x&quot;</ETag><Size>%d</Size><StorageClass>STANDARD</StorageClass></Contents>",
				strings.TrimPrefix(k, bucket+"/"), len(f.objects[k]))
		}
		fmt.Fprint(w, "</ListBucketResult>")
	case r.Method == http.MethodGet && key == "" && r.URL.Query().Has("uploads"):
		w.Header().Set("Content-Type", "application/xml")
		fmt.Fprintf(w, "<?xml version=\"1.0\"?><ListMultipartUploadsResult><Bucket>%s</Bucket><IsTruncated>false</IsTruncated></ListMultipartUploadsResult>", bucket)
	default:
		writeS3Error(w, 400, "InvalidRequest")
	}
}

func startS3(cert tls.Certificate) (*s3fake, error) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	f := objects
	f.server = &http.Server{Handler: s3Handler(), TLSConfig: &tls.Config{Certificates: []tls.Certificate{cert},
		MinVersion: tls.VersionTLS12}}
	go f.server.ServeTLS(ln, "", "")
	f.addr = ln.Addr().String()
	return f, nil
}

func (f *s3fake) close() { f.server.Close() }

func writeS3Error(w http.ResponseWriter, status int, code string) {
	w.Header().Set("Content-Type", "application/xml")
	w.WriteHeader(status)
	fmt.Fprintf(w, "<?xml version=\"1.0\"?><Error><Code>%s</Code><Message>%s</Message><Resource></Resource><RequestId>insertbench</RequestId></Error>", code, code)
}

type issuer struct {
	addr    string
	key     *ecdsa.PrivateKey
	kid     string
	user    string
	org     string
	project string
	apiKey  string
	server  *http.Server
}

func randomHex(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}

func b64(raw []byte) string { return base64.RawURLEncoding.EncodeToString(raw) }

func writeFile0600(path string, data []byte) error { return os.WriteFile(path, data, 0o600) }

func freePort(host string) (int, error) {
	ln, err := net.Listen("tcp", host+":0")
	if err != nil {
		return 0, err
	}
	defer ln.Close()
	return ln.Addr().(*net.TCPAddr).Port, nil
}

func (a *issuer) url() string { return "https://" + a.addr + "/terrascale" }

func (a *issuer) mint(claims map[string]any) string {
	now := time.Now()
	claims["iss"] = a.url()
	claims["aud"] = platformAud
	claims["iat"] = now.Unix()
	claims["nbf"] = now.Add(-time.Minute).Unix()
	claims["exp"] = now.Add(tokenLife).Unix()
	claims["jti"] = ulid.Make().String()
	header, _ := json.Marshal(map[string]string{"alg": "ES256", "typ": "at+jwt", "kid": a.kid})
	payload, _ := json.Marshal(claims)
	signing := b64(header) + "." + b64(payload)
	digest := sha256.Sum256([]byte(signing))
	rr, ss, err := ecdsa.Sign(rand.Reader, a.key, digest[:])
	if err != nil {
		panic(err)
	}
	sig := make([]byte, 64)
	rr.FillBytes(sig[:32])
	ss.FillBytes(sig[32:])
	return signing + "." + b64(sig)
}

func (a *issuer) projectToken() string {
	return a.mint(map[string]any{"sub": a.user, "client_id": "insert-bench", "project_id": a.project,
		"org_id": a.org, "scope": allScopes})
}

func startIssuer(org, project, apiKey string) (*issuer, []byte, tls.Certificate, error) {
	caKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	caTemplate := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: "insert-bench-ca"},
		NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(2 * time.Hour), IsCA: true,
		KeyUsage: x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature, BasicConstraintsValid: true}
	caDER, err := x509.CreateCertificate(rand.Reader, caTemplate, caTemplate, &caKey.PublicKey, caKey)
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	caCert, err := x509.ParseCertificate(caDER)
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	leafKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	leafTemplate := &x509.Certificate{SerialNumber: big.NewInt(2), Subject: pkix.Name{CommonName: "localhost"},
		NotBefore: time.Now().Add(-time.Minute), NotAfter: time.Now().Add(2 * time.Hour),
		DNSNames:    []string{"localhost", shapesHost},
		IPAddresses: []net.IP{net.ParseIP("127.0.0.1")}, KeyUsage: x509.KeyUsageDigitalSignature,
		ExtKeyUsage: []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth}}
	leafDER, err := x509.CreateCertificate(rand.Reader, leafTemplate, caCert, &leafKey.PublicKey, caKey)
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	keyDER, err := x509.MarshalECPrivateKey(leafKey)
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	cert, err := tls.X509KeyPair(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: leafDER}),
		pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER}))
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, nil, tls.Certificate{}, err
	}
	signingKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		ln.Close()
		return nil, nil, tls.Certificate{}, err
	}
	a := &issuer{addr: ln.Addr().String(), key: signingKey, kid: ulid.Make().String(), user: ulid.Make().String(),
		org: org, project: project, apiKey: apiKey}
	write := func(w http.ResponseWriter, status int, body any) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_ = json.NewEncoder(w).Encode(body)
	}
	mux := http.NewServeMux()
	mux.HandleFunc("/terrascale/.well-known/openid-configuration", func(w http.ResponseWriter, r *http.Request) {
		write(w, 200, map[string]any{"issuer": a.url(), "jwks_uri": a.url() + "/.well-known/jwks.json",
			"token_endpoint": a.url() + "/oauth/token"})
	})
	mux.HandleFunc("/terrascale/.well-known/jwks.json", func(w http.ResponseWriter, r *http.Request) {
		point := elliptic.Marshal(elliptic.P256(), a.key.PublicKey.X, a.key.PublicKey.Y)
		write(w, 200, map[string]any{"keys": []any{map[string]any{"kty": "EC", "crv": "P-256", "kid": a.kid, "alg": "ES256",
			"x": b64(point[1:33]), "y": b64(point[33:65])}}})
	})
	mux.HandleFunc("/terrascale/oauth/token", func(w http.ResponseWriter, r *http.Request) {
		if r.ParseForm() != nil {
			write(w, 400, map[string]string{"error": "invalid_request"})
			return
		}
		user, pass, ok := r.BasicAuth()
		if !ok || user != a.apiKey[:26] || pass != a.apiKey || r.PostForm.Get("grant_type") != "client_credentials" {
			write(w, 400, map[string]string{"error": "invalid_grant"})
			return
		}
		write(w, 200, map[string]any{"access_token": a.projectToken(), "token_type": "Bearer",
			"expires_in": int(tokenLife.Seconds()), "scope": allScopes})
	})
	a.server = &http.Server{Handler: mux, TLSConfig: &tls.Config{Certificates: []tls.Certificate{cert},
		MinVersion: tls.VersionTLS12}}
	go a.server.ServeTLS(ln, "", "")
	return a, pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: caDER}), cert, nil
}

func (a *issuer) close() { a.server.Close() }

type process struct {
	cmd  *exec.Cmd
	log  *os.File
	done chan error
}

func startProcess(name, logPath string, env []string, argv ...string) (*process, error) {
	log, err := os.OpenFile(logPath, os.O_CREATE|os.O_WRONLY|os.O_TRUNC, 0o600)
	if err != nil {
		return nil, err
	}
	cmd := exec.Command(argv[0], argv[1:]...)
	cmd.Env = env
	cmd.Stdout, cmd.Stderr = log, log
	if err := cmd.Start(); err != nil {
		log.Close()
		return nil, fmt.Errorf("start %s: %w", name, err)
	}
	p := &process{cmd: cmd, log: log, done: make(chan error, 1)}
	go func() { p.done <- cmd.Wait() }()
	return p, nil
}

func (p *process) stop(grace time.Duration) {
	_ = p.cmd.Process.Signal(syscall.SIGTERM)
	select {
	case <-p.done:
	case <-time.After(grace):
		_ = p.cmd.Process.Kill()
		<-p.done
	}
	p.log.Close()
}

func (p *process) exited() bool {
	select {
	case <-p.done:
		return true
	default:
		return false
	}
}

func poll(deadline time.Duration, check func() (bool, error)) error {
	end := time.Now().Add(deadline)
	for {
		ok, err := check()
		if err != nil {
			return err
		}
		if ok {
			return nil
		}
		if time.Now().After(end) {
			return errors.New("deadline exceeded")
		}
		time.Sleep(200 * time.Millisecond)
	}
}

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

type proxy struct {
	server *http.Server
}

type stack struct {
	egress     *http.Server
	dir        string
	nats       *process
	node       *process
	issuer     *issuer
	store      *s3fake
	clientPort int
	pgPort     int
	internal   int
	peerSecret string
	org        string
	project    string
	apiKey     string
	token      string
}

func startStack(terrabase, logDir string) (*stack, error) {
	dir, err := os.MkdirTemp("", "insert-bench-")
	if err != nil {
		return nil, err
	}
	if err := os.MkdirAll(filepath.Join(dir, "data"), 0o700); err != nil {
		return nil, err
	}
	natsToken := randomHex(24)
	s := &stack{dir: dir, org: ulid.Make().String(), project: ulid.Make().String(),
		apiKey: ulid.Make().String() + "_" + randomHex(24), peerSecret: randomHex(32)}
	natsPort, err := freePort("127.0.0.1")
	if err != nil {
		return nil, err
	}
	natsConf := fmt.Sprintf("listen: \"127.0.0.1:%d\"\nauthorization { token: %q }\njetstream { store_dir: %q }\n",
		natsPort, natsToken, filepath.Join(dir, "jetstream"))
	if err := writeFile0600(filepath.Join(dir, "nats.conf"), []byte(natsConf)); err != nil {
		return nil, err
	}
	masterKey := make([]byte, 32)
	if _, err := rand.Read(masterKey); err != nil {
		return nil, err
	}
	for name, value := range map[string]string{"peer-secret": s.peerSecret, "nats-token": natsToken,
		"master-key": base64.StdEncoding.EncodeToString(masterKey)} {
		if err := writeFile0600(filepath.Join(dir, name), []byte(value)); err != nil {
			return nil, err
		}
	}
	peers := filepath.Join(dir, "peers")
	if err := writeFile0600(peers, []byte(nodeName+" gru 127.0.0.1\n")); err != nil {
		return nil, err
	}
	if err := os.MkdirAll(logDir, 0o700); err != nil {
		return nil, err
	}
	if s.nats, err = startProcess("nats-server", filepath.Join(logDir, "nats.log"),
		[]string{"PATH=" + os.Getenv("PATH")}, "nats-server", "-c", filepath.Join(dir, "nats.conf")); err != nil {
		return nil, err
	}
	if err := poll(15*time.Second, func() (bool, error) {
		if s.nats.exited() {
			return false, errors.New("nats-server exited (see " + filepath.Join(logDir, "nats.log") + ")")
		}
		c, err := net.DialTimeout("tcp", fmt.Sprintf("127.0.0.1:%d", natsPort), time.Second)
		if err == nil {
			c.Close()
		}
		return err == nil, nil
	}); err != nil {
		return nil, err
	}
	auth, caPEM, cert, err := startIssuer(s.org, s.project, s.apiKey)
	if err != nil {
		return nil, err
	}
	egress, egressAddr, err := startEgressProxy(s3Handler(), cert)
	if err != nil {
		return nil, err
	}
	s.egress = egress
	bucket, err := startS3(cert)
	if err != nil {
		return nil, err
	}
	s.store = bucket
	s.issuer = auth
	s.token = auth.projectToken()
	if err := writeFile0600(filepath.Join(dir, "ca.pem"), caPEM); err != nil {
		return nil, err
	}
	root, err := dotnetRoot()
	if err != nil {
		return nil, err
	}
	argv := []string{terrabase}
	if strings.HasSuffix(terrabase, ".dll") {
		argv = []string{"dotnet", terrabase}
	}
	if s.clientPort, err = freePort("127.0.0.1"); err != nil {
		return nil, err
	}
	if s.pgPort, err = freePort("127.0.0.1"); err != nil {
		return nil, err
	}
	if s.internal, err = freePort("127.0.0.1"); err != nil {
		return nil, err
	}
	metrics, err := freePort("127.0.0.1")
	if err != nil {
		return nil, err
	}
	env := []string{"PATH=" + os.Getenv("PATH"), "HOME=" + dir, "DOTNET_ENVIRONMENT=Development",
		"DOTNET_CLI_TELEMETRY_OPTOUT=1", "SSL_CERT_FILE=" + filepath.Join(dir, "ca.pem"),
		"TS_REGION=gru", "TS_NODE=" + nodeName, "TS_PEERS_FILE=" + peers,
		"TS_NATS_URL=" + fmt.Sprintf("nats://127.0.0.1:%d", natsPort),
		"TERRABASE_NATS_TOKEN_FILE=" + filepath.Join(dir, "nats-token"),
		"TERRABASE_PEER_SECRET_FILE=" + filepath.Join(dir, "peer-secret"),
		"TERRABASE_MASTER_KEY_FILE=" + filepath.Join(dir, "master-key"),
		"TERRABASE_PORT=" + fmt.Sprint(s.clientPort), "TERRABASE_INTERNAL_PORT=" + fmt.Sprint(s.internal),
		"TERRABASE_METRICS_PORT=" + fmt.Sprint(metrics), "TERRABASE_POSTGRES_PORT=" + fmt.Sprint(s.pgPort),
		"TERRABASE_DATA_DIRECTORY=" + filepath.Join(dir, "data"), "TERRABASE_AUTH_ISSUER=" + s.issuer.url(),
		"TERRABASE_AUTH_AUDIENCE=" + platformAud, "DOTNET_ROOT=" + root,
		"AWS_ENDPOINT_URL_S3=https://" + s.store.addr, "AWS_REGION=auto", "BUCKET_NAME=" + s3Bucket,
		"AWS_ACCESS_KEY_ID=insertbench", "AWS_SECRET_ACCESS_KEY=" + randomHex(16),
		"TERRABASE_SHAPES_ACCESS_KEY_ID=insertbench", "TERRABASE_SHAPES_SECRET_ACCESS_KEY=" + randomHex(16),
		"TERRABASE_SHAPES_CDN_API_TOKEN=" + randomHex(16),
		"HTTPS_PROXY=http://" + egressAddr, "HTTP_PROXY=http://" + egressAddr, "NO_PROXY=localhost,127.0.0.1"}
	if s.node, err = startProcess(nodeName, filepath.Join(logDir, "node.log"), env, argv...); err != nil {
		return nil, err
	}
	hc := &http.Client{Timeout: 2 * time.Second}
	if err := poll(90*time.Second, func() (bool, error) {
		if s.node.exited() {
			return false, errors.New("node exited (see " + filepath.Join(logDir, "node.log") + ")")
		}
		res, err := hc.Get(fmt.Sprintf("http://127.0.0.1:%d/readyz", s.clientPort))
		if err != nil {
			return false, nil
		}
		res.Body.Close()
		return res.StatusCode == http.StatusOK, nil
	}); err != nil {
		return nil, err
	}
	return s, nil
}

func (s *stack) stop() {
	if s.node != nil {
		s.node.stop(30 * time.Second)
	}
	if s.issuer != nil {
		s.issuer.close()
	}
	if s.egress != nil {
		s.egress.Close()
	}
	if s.store != nil {
		s.store.close()
	}
	if s.nats != nil {
		s.nats.stop(5 * time.Second)
	}
	os.RemoveAll(s.dir)
}

func (s *stack) call(method, path string, body any) (int, []byte, error) {
	var reader io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return 0, nil, err
		}
		reader = bytes.NewReader(encoded)
	}
	request, err := http.NewRequest(method, fmt.Sprintf("http://127.0.0.1:%d%s", s.clientPort, path), reader)
	if err != nil {
		return 0, nil, err
	}
	request.Host = apiHost
	request.Header.Set("Authorization", "Bearer "+s.token)
	request.Header.Set("Content-Type", "application/json")
	response, err := (&http.Client{Timeout: 120 * time.Second}).Do(request)
	if err != nil {
		return 0, nil, err
	}
	defer response.Body.Close()
	payload, err := io.ReadAll(io.LimitReader(response.Body, 1<<20))
	return response.StatusCode, payload, err
}

func (s *stack) sql(statements ...map[string]any) (int, []byte, error) {
	return s.call("POST", fmt.Sprintf("/v1/projects/%s/databases/%s/sql/execute", s.project, dbName),
		map[string]any{"statements": statements})
}

func require(status int, payload []byte, expected int, operation string) error {
	if status != expected {
		return fmt.Errorf("%s: HTTP %d %s", operation, status, snippet(payload))
	}
	return nil
}

func snippet(data []byte) string {
	if len(data) > 300 {
		return string(data[:300])
	}
	return string(data)
}

func (s *stack) publishPlan() error {
	protocols := new(http.Protocols)
	protocols.SetUnencryptedHTTP2(true)
	transport := &http.Transport{Protocols: protocols}
	defer transport.CloseIdleConnections()
	address := fmt.Sprintf("http://127.0.0.1:%d/admin/organizations/%s/plan", s.internal, s.org)
	request, err := http.NewRequest(http.MethodPut, address, strings.NewReader(`{"plan":"enterprise"}`))
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-TerraBase-Peer-Credential", s.peerSecret)
	response, err := (&http.Client{Timeout: 10 * time.Second, Transport: transport}).Do(request)
	if err != nil {
		return fmt.Errorf("publish synthetic organization plan: %w", err)
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(io.LimitReader(response.Body, 1024))
	if response.StatusCode != http.StatusOK {
		return fmt.Errorf("publish synthetic organization plan: HTTP %d %s", response.StatusCode, body)
	}
	return nil
}

type sample struct {
	transport string
	rows      int
	ms        float64
}

func percentile(values []float64, q float64) float64 {
	sorted := append([]float64(nil), values...)
	sort.Float64s(sorted)
	return sorted[min(len(sorted)-1, int(math.Round(q*float64(len(sorted)-1))))]
}

func rowsPerSecond(size int, values []float64) float64 {
	total := 0.0
	for _, v := range values {
		total += v
	}
	return float64(size*len(values)) / (total / 1000)
}

func restBatch(s *stack, size int) func() (float64, error) {
	placeholders := strings.TrimSuffix(strings.Repeat("(?, ?, ?), ", size), ", ")
	sql := "INSERT INTO items (id, n, body) VALUES " + placeholders
	return func() (float64, error) {
		args := make([]any, 0, size*3)
		for i := range size {
			args = append(args, ulid.Make().String(), int64(i), strings.Repeat("x", 100))
		}
		for attempt := 0; ; attempt++ {
			started := time.Now()
			status, payload, err := s.sql(map[string]any{"sql": sql, "args": args})
			elapsed := float64(time.Since(started).Microseconds()) / 1000
			if err != nil {
				return 0, err
			}
			if status == 429 && attempt < 10 {
				time.Sleep(500 * time.Millisecond)
				continue
			}
			if err := require(status, payload, 200, "rest insert"); err != nil {
				return 0, err
			}
			return elapsed, nil
		}
	}
}

func wireBatch(ctx context.Context, conn *pgx.Conn, size int) func() (float64, error) {
	row := strings.TrimSuffix(strings.Repeat("($", 0), "")
	_ = row
	placeholders := make([]string, size)
	for i := range size {
		base := i * 3
		placeholders[i] = fmt.Sprintf("($%d, $%d, $%d)", base+1, base+2, base+3)
	}
	sql := "INSERT INTO items (id, n, body) VALUES " + strings.Join(placeholders, ", ")
	return func() (float64, error) {
		args := make([]any, 0, size*3)
		for i := range size {
			args = append(args, ulid.Make().String(), int64(i), strings.Repeat("x", 100))
		}
		for attempt := 0; ; attempt++ {
			started := time.Now()
			_, err := conn.Exec(ctx, sql, args...)
			if err != nil && attempt < 10 && strings.Contains(err.Error(), "tenant_busy") {
				time.Sleep(500 * time.Millisecond)
				continue
			}
			if err != nil {
				return 0, err
			}
			return float64(time.Since(started).Microseconds()) / 1000, nil
		}
	}
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "insert-bench:", err)
		os.Exit(1)
	}
}

func run() error {
	terrabase := flag.String("terrabase", "", "built TerraBase binary or dll (make terrabase-build)")
	out := flag.String("out", "", "Markdown results file to write (benchmark/bench/)")
	logs := flag.String("logs", "", "directory to keep nats and node logs in (default: removed with the run directory)")
	batches := flag.Int("batches", 20, "measured batches per size, repeat and transport")
	repeats := flag.Int("repeats", 3, "measured repeats")
	flag.Parse()
	if *terrabase == "" || *out == "" {
		flag.Usage()
		return errors.New("-terrabase and -out are required")
	}
	absolute, err := filepath.Abs(*terrabase)
	if err != nil {
		return err
	}
	if _, err := os.Stat(absolute); err != nil {
		return err
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	dir, err := os.MkdirTemp("", "insert-bench-run-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(dir)
	logDir := dir
	if *logs != "" {
		if logDir, err = filepath.Abs(*logs); err != nil {
			return err
		}
	}
	s, err := startStack(absolute, logDir)
	if err != nil {
		return err
	}
	defer s.stop()
	if err := s.publishPlan(); err != nil {
		return err
	}
	status, payload, err := s.call("POST", fmt.Sprintf("/v1/projects/%s/databases", s.project),
		map[string]any{"name": dbName, "homeRegion": "gru"})
	if err != nil {
		return err
	}
	if err := require(status, payload, 201, "create database"); err != nil {
		return err
	}
	status, payload, err = s.sql(map[string]any{"sql": "CREATE TABLE items (id TEXT PRIMARY KEY, n INTEGER, body TEXT)"})
	if err != nil {
		return err
	}
	if err := require(status, payload, 200, "create table"); err != nil {
		return err
	}
	sizes := []int{1, 50, 250, 1000}
	cells := map[string]map[int][]float64{"rest": {}, "pg_wire": {}}
	refused := map[int]bool{}
	for _, size := range sizes {
		batch := restBatch(s, size)
		if err := warm(batch); err != nil {
			return err
		}
		cells["rest"][size] = measure(batch, *batches)
		fmt.Printf(`{"transport":"rest","rows":%d,"p50_ms":%.2f}`+"\n", size, percentile(cells["rest"][size], .5))
	}
	pgURL := fmt.Sprintf("postgres://%s:%s@127.0.0.1:%d/%s/%s?sslmode=disable",
		"bench", url.QueryEscape(s.apiKey), s.pgPort, s.project, dbName)
	conn, err := pgx.Connect(ctx, pgURL)
	if err != nil {
		return fmt.Errorf("wire connect: %w", err)
	}
	defer conn.Close(ctx)
	for _, size := range sizes {
		batch := wireBatch(ctx, conn, size)
		if _, err := batch(); err != nil {
			if strings.Contains(err.Error(), "16384") {
				refused[size] = true
				continue
			}
			return err
		}
		if err := warm(batch); err != nil {
			return err
		}
		cells["pg_wire"][size] = measure(batch, *batches)
		fmt.Printf(`{"transport":"pg_wire","rows":%d,"p50_ms":%.2f}`+"\n", size, percentile(cells["pg_wire"][size], .5))
	}
	for repeat := 2; repeat <= *repeats; repeat++ {
		for _, size := range sizes {
			batch := restBatch(s, size)
			cells["rest"][size] = append(cells["rest"][size], measure(batch, *batches)...)
		}
		for _, size := range sizes {
			if refused[size] {
				continue
			}
			batch := wireBatch(ctx, conn, size)
			cells["pg_wire"][size] = append(cells["pg_wire"][size], measure(batch, *batches)...)
		}
		fmt.Printf(`{"repeat":%d,"done":true}`+"\n", repeat)
	}
	return writeReport(*out, absolute, sizes, cells, *batches, *repeats)
}

func warm(batch func() (float64, error)) error {
	for range 2 {
		if _, err := batch(); err != nil {
			return err
		}
	}
	return nil
}

func measure(batch func() (float64, error), count int) []float64 {
	values := make([]float64, 0, count)
	for range count {
		ms, err := batch()
		if err != nil {
			panic(err)
		}
		values = append(values, ms)
	}
	return values
}

func writeReport(out, terrabase string, sizes []int, cells map[string]map[int][]float64, batches, repeats int) error {
	commit, err := exec.Command("git", "rev-parse", "HEAD").Output()
	if err == nil {
		commit = bytes.TrimSpace(commit)
	} else {
		commit = []byte("unknown")
	}
	cpu := "unknown"
	if model, err := os.ReadFile("/proc/cpuinfo"); err == nil {
		for _, line := range strings.Split(string(model), "\n") {
			if strings.HasPrefix(line, "model name") {
				cpu = strings.TrimSpace(strings.SplitN(line, ":", 2)[1])
				break
			}
		}
	}
	var b strings.Builder
	fmt.Fprintf(&b, "# insert-batch: batched INSERT throughput, local single node\n\n")
	fmt.Fprintf(&b, "One parameterized multi-row `INSERT` per batch into `items (id TEXT PRIMARY KEY, n INTEGER, body TEXT)` over the SQL REST\n")
	fmt.Fprintf(&b, "endpoint (`POST /v1/projects/{project}/databases/{database}/sql/execute`) and the Postgres wire (extended protocol),\n")
	fmt.Fprintf(&b, "against a local TerraBase node started by `benchmark/cmd/insert-bench` with a test-only token issuer.\n\n")
	fmt.Fprintf(&b, "| Item | Value |\n| --- | --- |\n")
	fmt.Fprintf(&b, "| Host | %s, %s, %d cores |\n", hostname(), cpu, runtime.NumCPU())
	fmt.Fprintf(&b, "| Commit | `%s` |\n", commit)
	fmt.Fprintf(&b, "| TerraBase | `%s` |\n", terrabase)
	fmt.Fprintf(&b, "| Batches | %d measured per size, transport and repeat, 2 warmup batches first; %d repeats |\n", batches, repeats)
	fmt.Fprintf(&b, "| Row | ULID id, integer, ~100-byte body |\n\n")
	fmt.Fprintf(&b, "| Transport | Rows/batch | p50 ms | p95 ms | rows/s |\n| --- | --- | --- | --- | --- |\n")
	for _, transport := range []string{"rest", "pg_wire"} {
		for _, size := range sizes {
			if values := cells[transport][size]; values != nil {
				fmt.Fprintf(&b, "| %s | %d | %.2f | %.2f | %.0f |\n", transport, size,
					percentile(values, .5), percentile(values, .95), rowsPerSecond(size, values))
			} else {
				fmt.Fprintf(&b, "| %s | %d | refused | refused | refused |\n", transport, size)
			}
		}
	}
	fmt.Fprintf(&b, "\n")
	for _, transport := range []string{"rest", "pg_wire"} {
		fmt.Fprintf(&b, "- a 250-row batch takes %.0f ms over %s.\n", percentile(cells[transport][250], .5),
			map[string]string{"rest": "REST", "pg_wire": "the pg wire"}[transport])
	}
	for _, transport := range []string{"rest", "pg_wire"} {
		for _, size := range sizes {
			if cells[transport][size] == nil {
				fmt.Fprintf(&b, "\nThe %s transport refused the %d-row batch: the statement exceeds the 16384-byte SQL limit (%d placeholders).\n",
					transport, size, size*3)
			}
		}
	}
	if err := os.MkdirAll(filepath.Dir(out), 0o755); err != nil {
		return err
	}
	return os.WriteFile(out, []byte(b.String()), 0o644)
}

func hostname() string {
	name, err := os.Hostname()
	if err != nil {
		return "unknown"
	}
	return name
}
