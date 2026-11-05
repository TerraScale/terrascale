package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"errors"
	"io"
	"log"
	"math/big"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"
)

// Fixed public origins of the ts CLI (sdk/go v1.DefaultBaseURL, tsauth.DefaultIssuer).
const (
	apiHost  = "api.terrascale.tech"
	authHost = "auth.terrascale.tech"
)

// authority is the per-run CA. The CLI (Go) and TerraBase (.NET on OpenSSL) trust it through SSL_CERT_FILE.
type authority struct {
	cert *x509.Certificate
	key  *ecdsa.PrivateKey
	pem  []byte

	mu     sync.Mutex
	leaves map[string]*tls.Certificate
}

func newAuthority() (*authority, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	template := &x509.Certificate{
		SerialNumber:          serial(),
		Subject:               pkix.Name{CommonName: "region-sim run CA"},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(24 * time.Hour),
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature,
		BasicConstraintsValid: true,
		IsCA:                  true,
		MaxPathLenZero:        true,
	}
	der, err := x509.CreateCertificate(rand.Reader, template, template, &key.PublicKey, key)
	if err != nil {
		return nil, err
	}
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		return nil, err
	}
	return &authority{cert: cert, key: key, pem: pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
		leaves: map[string]*tls.Certificate{}}, nil
}

func serial() *big.Int {
	n, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 120))
	if err != nil {
		panic(err)
	}
	return n
}

// leaf returns a server certificate for names (DNS names or IPs), cached by the joined names.
func (a *authority) leaf(names ...string) (*tls.Certificate, error) {
	cacheKey := strings.Join(names, ",")
	a.mu.Lock()
	defer a.mu.Unlock()
	if c, ok := a.leaves[cacheKey]; ok {
		return c, nil
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	template := &x509.Certificate{
		SerialNumber: serial(),
		Subject:      pkix.Name{CommonName: names[0]},
		NotBefore:    time.Now().Add(-time.Hour),
		NotAfter:     time.Now().Add(24 * time.Hour),
		KeyUsage:     x509.KeyUsageDigitalSignature,
		ExtKeyUsage:  []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
	}
	for _, n := range names {
		if ip := net.ParseIP(n); ip != nil {
			template.IPAddresses = append(template.IPAddresses, ip)
		} else {
			template.DNSNames = append(template.DNSNames, n)
		}
	}
	der, err := x509.CreateCertificate(rand.Reader, template, a.cert, &key.PublicKey, a.key)
	if err != nil {
		return nil, err
	}
	c := &tls.Certificate{Certificate: [][]byte{der, a.cert.Raw}, PrivateKey: key}
	a.leaves[cacheKey] = c
	return c, nil
}

// regionProxy is an HTTPS_PROXY for one region: CONNECT to api.terrascale.tech terminates TLS with a run-CA
// certificate and reverse-proxies plain HTTP/1 to that region's TerraBase client listener (TLS ends at ingress in
// production too); CONNECT to auth.terrascale.tech is served by the ts-auth stand-in. Any other host is refused.
type regionProxy struct {
	ca   *authority
	api  http.Handler
	auth http.Handler
	srv  *http.Server
	ln   net.Listener
}

func startRegionProxy(ca *authority, clientPort int, auth http.Handler) (*regionProxy, error) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	target := &url.URL{Scheme: "http", Host: net.JoinHostPort("127.0.0.1", itoa(clientPort))}
	rp := &httputil.ReverseProxy{
		Rewrite: func(r *httputil.ProxyRequest) {
			r.SetURL(target)
			r.Out.Host = r.In.Host
		},
		FlushInterval: -1,
		ErrorLog:      log.New(io.Discard, "", 0),
	}
	p := &regionProxy{ca: ca, api: rp, auth: auth, ln: ln}
	p.srv = &http.Server{Handler: http.HandlerFunc(p.connect), ReadHeaderTimeout: 10 * time.Second, ErrorLog: log.New(io.Discard, "", 0)}
	go p.srv.Serve(ln)
	return p, nil
}

func (p *regionProxy) url() string { return "http://" + p.ln.Addr().String() }

func (p *regionProxy) close() { p.srv.Close() }

func (p *regionProxy) connect(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodConnect {
		http.Error(w, "CONNECT only", http.StatusMethodNotAllowed)
		return
	}
	host, _, err := net.SplitHostPort(r.Host)
	if err != nil {
		host = r.Host
	}
	var handler http.Handler
	switch host {
	case apiHost:
		handler = p.api
	case authHost:
		handler = p.auth
	default:
		http.Error(w, "region-sim proxies only "+apiHost+" and "+authHost, http.StatusForbidden)
		return
	}
	cert, err := p.ca.leaf(host)
	if err != nil {
		http.Error(w, err.Error(), http.StatusInternalServerError)
		return
	}
	hj, ok := w.(http.Hijacker)
	if !ok {
		http.Error(w, "hijack unsupported", http.StatusInternalServerError)
		return
	}
	conn, buf, err := hj.Hijack()
	if err != nil {
		return
	}
	if _, err := conn.Write([]byte("HTTP/1.1 200 Connection Established\r\n\r\n")); err != nil || buf.Reader.Buffered() > 0 {
		conn.Close()
		return
	}
	// HTTP/1.1 only: the CLI forces an HTTP/2 attempt, ALPN settles on http/1.1.
	tlsConn := tls.Server(conn, &tls.Config{Certificates: []tls.Certificate{*cert}, NextProtos: []string{"http/1.1"}, MinVersion: tls.VersionTLS12})
	srv := &http.Server{Handler: handler, ReadHeaderTimeout: 30 * time.Second, ErrorLog: log.New(io.Discard, "", 0)}
	srv.Serve(newOneConnListener(tlsConn))
}

// oneConnListener serves exactly one connection, then reports closed once that connection closes.
type oneConnListener struct {
	conn net.Conn
	once sync.Once
	done chan struct{}
	used bool
}

type closeNotifyConn struct {
	net.Conn
	once sync.Once
	done chan struct{}
}

func (c *closeNotifyConn) Close() error {
	c.once.Do(func() { close(c.done) })
	return c.Conn.Close()
}

func newOneConnListener(c net.Conn) *oneConnListener {
	done := make(chan struct{})
	return &oneConnListener{conn: &closeNotifyConn{Conn: c, done: done}, done: done}
}

func (l *oneConnListener) Accept() (net.Conn, error) {
	if !l.used {
		l.used = true
		return l.conn, nil
	}
	<-l.done
	return nil, net.ErrClosed
}

func (l *oneConnListener) Close() error {
	l.once.Do(func() {})
	return nil
}

func (l *oneConnListener) Addr() net.Addr { return l.conn.LocalAddr() }

// forwarder is a TCP relay standing in for the network path from one node to a peer's internal listener. Paused,
// it black-holes traffic like a partition: bytes already read and new connections wait until resume (or close).
type forwarder struct {
	ln     net.Listener
	target string

	mu     sync.Mutex
	gate   chan struct{} // closed while open; replaced while paused
	conns  map[net.Conn]struct{}
	closed bool
}

var peerDelay time.Duration

func startForwarder(listen, target string) (*forwarder, error) {
	ln, err := net.Listen("tcp", listen)
	if err != nil {
		return nil, err
	}
	gate := make(chan struct{})
	close(gate)
	f := &forwarder{ln: ln, target: target, gate: gate, conns: map[net.Conn]struct{}{}}
	go f.serve()
	return f, nil
}

func (f *forwarder) open() <-chan struct{} {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.gate
}

func (f *forwarder) resume() {
	f.mu.Lock()
	defer f.mu.Unlock()
	select {
	case <-f.gate:
	default:
		close(f.gate)
	}
}

func (f *forwarder) track(c net.Conn) bool {
	f.mu.Lock()
	defer f.mu.Unlock()
	if f.closed {
		c.Close()
		return false
	}
	f.conns[c] = struct{}{}
	return true
}

func (f *forwarder) untrack(c net.Conn) {
	f.mu.Lock()
	defer f.mu.Unlock()
	delete(f.conns, c)
	c.Close()
}

func (f *forwarder) close() {
	f.ln.Close()
	f.mu.Lock()
	f.closed = true
	conns := f.conns
	f.conns = map[net.Conn]struct{}{}
	f.mu.Unlock()
	f.resume()
	for c := range conns {
		c.Close()
	}
}

func (f *forwarder) serve() {
	for {
		in, err := f.ln.Accept()
		if err != nil {
			return
		}
		go f.relay(in)
	}
}

func (f *forwarder) relay(in net.Conn) {
	if !f.track(in) {
		return
	}
	defer f.untrack(in)
	<-f.open()
	out, err := net.DialTimeout("tcp", f.target, 5*time.Second)
	if err != nil {
		return
	}
	if !f.track(out) {
		return
	}
	defer f.untrack(out)
	done := make(chan struct{}, 2)
	go func() { f.pipe(out, in); done <- struct{}{} }()
	go func() { f.pipe(in, out); done <- struct{}{} }()
	<-done
}

func (f *forwarder) pipe(dst, src net.Conn) {
	if peerDelay > 0 {
		f.delayed(dst, src)
		return
	}
	buf := make([]byte, 64<<10)
	for {
		n, err := src.Read(buf)
		if n > 0 {
			<-f.open()
			if _, werr := dst.Write(buf[:n]); werr != nil {
				return
			}
		}
		if err != nil {
			if !errors.Is(err, io.EOF) {
				return
			}
			if tc, ok := dst.(*net.TCPConn); ok {
				tc.CloseWrite()
			}
			return
		}
	}
}

func (f *forwarder) delayed(dst, src net.Conn) {
	type chunk struct {
		at time.Time
		b  []byte
	}
	q := make(chan chunk, 8192)
	done := make(chan struct{})
	go func() {
		defer close(done)
		for c := range q {
			time.Sleep(time.Until(c.at))
			<-f.open()
			if _, err := dst.Write(c.b); err != nil {
				for range q {
				}
				return
			}
		}
		if tc, ok := dst.(*net.TCPConn); ok {
			tc.CloseWrite()
		}
	}()
	buf := make([]byte, 64<<10)
	for {
		n, err := src.Read(buf)
		if n > 0 {
			q <- chunk{time.Now().Add(peerDelay), append([]byte(nil), buf[:n]...)}
		}
		if err != nil {
			close(q)
			<-done
			return
		}
	}
}

func writeFile0600(path string, data []byte) error {
	return os.WriteFile(path, data, 0o600)
}
