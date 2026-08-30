package tbep

import (
	"bufio"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/pem"
	"fmt"
	"math/big"
	"net"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"

	terrabasev1 "git.terrascale.tech/TerraScale/TerraBase/go/tbep/gen/terrabase/v1"
	"google.golang.org/grpc"
)

// The cross-language production row (ADR 0028): the terrabase-go client
// against the real Rust engine behind the production mTLS listener.
//
// The test builds the engine's tbep-production-harness binary (cargo,
// dev profile — cached after the first build), drives a throwaway PKI,
// and proves the bind/write/read/health lifecycle end to end. It is
// skipped in -short mode and when cargo is unavailable.

type pki struct {
	serverCert []byte
	serverKey  []byte
	clientCert []byte
	clientKey  []byte
	root       []byte
}

func throwawayPKI(t *testing.T) pki {
	t.Helper()
	rootKey, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatalf("the root key generates: %v", err)
	}
	rootTemplate := &x509.Certificate{
		SerialNumber:          big.NewInt(1),
		Subject:               pkix.Name{CommonName: "tbep-e2e-root"},
		NotBefore:             time.Now().Add(-time.Minute),
		NotAfter:              time.Now().Add(time.Hour),
		IsCA:                  true,
		KeyUsage:              x509.KeyUsageCertSign | x509.KeyUsageDigitalSignature,
		BasicConstraintsValid: true,
	}
	rootDER, err := x509.CreateCertificate(rand.Reader, rootTemplate, rootTemplate, &rootKey.PublicKey, rootKey)
	if err != nil {
		t.Fatalf("the root certificate creates: %v", err)
	}
	root := &x509.Certificate{Raw: rootDER}

	leaf := func(commonName string, dnsNames []string, serial int64, usage x509.ExtKeyUsage) ([]byte, []byte) {
		key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
		if err != nil {
			t.Fatalf("the leaf key generates: %v", err)
		}
		template := &x509.Certificate{
			SerialNumber: big.NewInt(serial),
			Subject:      pkix.Name{CommonName: commonName},
			NotBefore:    time.Now().Add(-time.Minute),
			NotAfter:     time.Now().Add(time.Hour),
			DNSNames:     dnsNames,
			IPAddresses:  []net.IP{net.ParseIP("127.0.0.1")},
			KeyUsage:     x509.KeyUsageDigitalSignature,
			ExtKeyUsage:  []x509.ExtKeyUsage{usage},
		}
		der, err := x509.CreateCertificate(rand.Reader, template, root, &key.PublicKey, rootKey)
		if err != nil {
			t.Fatalf("the leaf certificate creates: %v", err)
		}
		keyDER, err := x509.MarshalECPrivateKey(key)
		if err != nil {
			t.Fatalf("the leaf key marshals: %v", err)
		}
		return pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}),
			pem.EncodeToMemory(&pem.Block{Type: "EC PRIVATE KEY", Bytes: keyDER})
	}

	serverCert, serverKey := leaf("tbep-e2e-engine", []string{"localhost"}, 2, x509.ExtKeyUsageServerAuth)
	clientCert, clientKey := leaf("tbep-e2e-client", nil, 3, x509.ExtKeyUsageClientAuth)
	return pki{
		serverCert: serverCert,
		serverKey:  serverKey,
		clientCert: clientCert,
		clientKey:  clientKey,
		root:       pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: rootDER}),
	}
}

func repoRoot(t *testing.T) string {
	t.Helper()
	working, err := os.Getwd()
	if err != nil {
		t.Fatalf("the working directory resolves: %v", err)
	}
	root, err := filepath.Abs(filepath.Join(working, "..", ".."))
	if err != nil {
		t.Fatalf("the repository root resolves: %v", err)
	}
	return root
}

func TestTheRealEngineServesTheGoClientOverMtls(t *testing.T) {
	if testing.Short() {
		t.Skip("the cross-language row builds the Rust engine harness")
	}
	root := repoRoot(t)
	if _, err := exec.LookPath("cargo"); err != nil {
		t.Skip("cargo is unavailable")
	}
	build := exec.Command("cargo", "build", "-p", "terrabase-tbep", "--bin", "tbep-production-harness")
	build.Dir = root
	if output, err := build.CombinedOutput(); err != nil {
		t.Fatalf("the harness builds: %v\n%s", err, output)
	}
	harness := filepath.Join(root, "target", "debug", "tbep-production-harness")

	identity := throwawayPKI(t)
	directory := t.TempDir()
	write := func(name string, content []byte) string {
		path := filepath.Join(directory, name)
		if err := os.WriteFile(path, content, 0o600); err != nil {
			t.Fatalf("%s writes: %v", name, err)
		}
		return path
	}
	serverCert := write("server.pem", identity.serverCert)
	serverKey := write("server.key", identity.serverKey)
	clientRoot := write("root.pem", identity.root)
	storage := t.TempDir()

	process := exec.Command(harness, serverCert, serverKey, clientRoot, storage)
	stdout, err := process.StdoutPipe()
	if err != nil {
		t.Fatalf("the harness stdout pipes: %v", err)
	}
	stdin, err := process.StdinPipe()
	if err != nil {
		t.Fatalf("the harness stdin pipes: %v", err)
	}
	if err := process.Start(); err != nil {
		t.Fatalf("the harness starts: %v", err)
	}
	defer func() { _ = process.Wait() }()

	reader := bufio.NewReader(stdout)
	readyLine, err := reader.ReadString('\n')
	if err != nil {
		t.Fatalf("the harness announces: %v", err)
	}
	readyLine = strings.TrimSpace(readyLine)
	port, found := strings.CutPrefix(readyLine, "TBEP_PRODUCTION_LISTENER_READY ")
	if !found {
		t.Fatalf("the harness announced something unexpected: %q", readyLine)
	}

	credentials, err := TLS(identity.root, identity.clientCert, identity.clientKey)
	if err != nil {
		t.Fatalf("the client credentials build: %v", err)
	}
	client, err := Dial(
		fmt.Sprintf("127.0.0.1:%s", port),
		Identity{Principal: "operator", Tenant: "tenant-a"},
		WithGRPCDialOptions(
			grpc.WithTransportCredentials(credentials),
			grpc.WithAuthority("localhost"),
		),
	)
	if err != nil {
		t.Fatalf("the client dials: %v", err)
	}
	defer func() { _ = client.Close() }()

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	bound, err := client.Bind(ctx)
	if err != nil {
		t.Fatalf("the bind answers: %v", err)
	}
	if bound.ServingInstance != 1 {
		t.Fatalf("unexpected serving instance: %d", bound.ServingInstance)
	}

	body, err := ValueFromJSON([]byte(`{"total": 21}`))
	if err != nil {
		t.Fatalf("the body parses: %v", err)
	}
	written, err := client.WriteDocument(ctx, &terrabasev1.DocumentWriteRequest{
		Document: &terrabasev1.DocumentPut{Id: "d-1", Collection: "orders", Body: body},
		Expected: &terrabasev1.ExpectedVersion{
			Discipline: &terrabasev1.ExpectedVersion_Any{Any: &terrabasev1.ExpectedAny{}},
		},
		OperationId: "op-1",
	}, "op-1")
	if err != nil {
		t.Fatalf("the write commits against the real engine: %v", err)
	}
	if written.GetOutcome().GetCommitted() == nil {
		t.Fatalf("unexpected write outcome: %+v", written.GetOutcome())
	}

	read, err := client.ReadDocument(ctx, &terrabasev1.DocumentReadRequest{
		Collection: "orders", Document: "d-1",
	})
	if err != nil {
		t.Fatalf("the read answers from the real engine: %v", err)
	}
	if !read.Found || read.Document.Version != 1 {
		t.Fatalf("unexpected read answer: found=%v version=%d", read.Found, read.Document.GetVersion())
	}
	encoded, err := JSONFromValue(read.Document.Body)
	if err != nil {
		t.Fatalf("the body encodes: %v", err)
	}
	if string(encoded) != `{"total":21}` {
		t.Fatalf("unexpected body: %s", encoded)
	}

	health, err := client.Health(ctx)
	if err != nil {
		t.Fatalf("the health answers: %v", err)
	}
	if health.State != terrabasev1.HealthState_HEALTH_STATE_SERVING {
		t.Fatalf("unexpected health state: %v", health.State)
	}

	if _, err := stdin.Write([]byte("shutdown\n")); err != nil {
		t.Fatalf("the shutdown command sends: %v", err)
	}
}
