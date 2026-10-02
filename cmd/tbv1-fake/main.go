// Command tbv1-fake serves the in-memory TerraBase v1 contract fake
// (terrascale.tech/sdk/v1/v1test) over TLS for local development and
// contract tests.
//
// It is NOT tb-server: state lives in memory and is lost on exit, and there
// is no persistence, replication, rate limiting, row-filter enforcement or
// TTL reaper. Acceptance tests must run against the real tb-server.
//
// Usage:
//
//	tbv1-fake [--listen 127.0.0.1:9465] [--edge-listen 127.0.0.1:0]
//	          [--cert-dir .tbv1-fake] [--hosts localhost,127.0.0.1,::1]
//	          --control-key-out FILE | --print-control-key
//	          [--seed [--data-key-out FILE]]
//
// Secrets are written only to the 0600 files named by --control-key-out and
// --data-key-out; they reach stdout only with --print-control-key.
package main

import (
	"context"
	"errors"
	"flag"
	"fmt"
	"io"
	"io/fs"
	"log"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"

	"terrascale.tech/sdk/v1/v1test"
)

func main() {
	if err := run(os.Args[1:], os.Stdout, os.Stderr); err != nil {
		fmt.Fprintln(os.Stderr, "tbv1-fake:", err)
		os.Exit(1)
	}
}

func run(args []string, stdout, stderr io.Writer) error {
	fl := flag.NewFlagSet("tbv1-fake", flag.ContinueOnError)
	fl.SetOutput(stderr)
	listen := fl.String("listen", "127.0.0.1:9465", "origin TLS listen address")
	edgeListen := fl.String("edge-listen", "127.0.0.1:0", "edge-proxy TLS listen address (shape tokens)")
	certDir := fl.String("cert-dir", ".tbv1-fake", "directory holding ca.pem, server.pem and server-key.pem; generated when absent")
	hosts := fl.String("hosts", strings.Join(v1test.DefaultHosts, ","), "names and IPs for a generated server certificate")
	controlOut := fl.String("control-key-out", "", "write the bootstrap control key to this file (mode 0600)")
	printControl := fl.Bool("print-control-key", false, "print the control key (and seeded data key) to stdout")
	seed := fl.Bool("seed", false, `seed tenant "Acme", database "main", the SDK test collections and a data key`)
	dataOut := fl.String("data-key-out", "", "with --seed, write the seeded data key to this file (mode 0600)")
	if err := fl.Parse(args); err != nil {
		return err
	}
	if fl.NArg() > 0 {
		return fmt.Errorf("unexpected arguments: %q", fl.Args())
	}
	if *controlOut == "" && !*printControl {
		return errors.New("the control key would be unreachable: pass --control-key-out FILE or --print-control-key")
	}
	if *dataOut != "" && !*seed {
		return errors.New("--data-key-out needs --seed")
	}
	certs, generated, err := loadOrCreateCerts(*certDir, strings.Split(*hosts, ","))
	if err != nil {
		return err
	}
	ln, err := net.Listen("tcp", *listen)
	if err != nil {
		return err
	}
	edgeLn, err := net.Listen("tcp", *edgeListen)
	if err != nil {
		ln.Close()
		return err
	}
	srv, err := v1test.Start(v1test.Options{Seed: *seed, Listener: ln, EdgeListener: edgeLn, Certificates: certs,
		ErrorLog: log.New(stderr, "tbv1-fake: ", 0)})
	if err != nil {
		return err
	}
	defer srv.Close()
	if *controlOut != "" {
		if err := writeSecret(*controlOut, srv.ControlKey); err != nil {
			return err
		}
	}
	if *dataOut != "" {
		if err := writeSecret(*dataOut, srv.Seed.DataKey); err != nil {
			return err
		}
	}

	caPath, _ := filepath.Abs(filepath.Join(*certDir, "ca.pem"))
	fmt.Fprintln(stderr, "tbv1-fake: DEVELOPMENT FAKE of the TerraBase v1 API - not tb-server.")
	fmt.Fprintln(stderr, "tbv1-fake: state is in memory and lost on exit; acceptance tests must use the real tb-server.")
	fmt.Fprintf(stderr, "tbv1-fake: origin %s\n", srv.URL)
	fmt.Fprintf(stderr, "tbv1-fake: edge %s\n", srv.EdgeURL)
	if generated {
		fmt.Fprintf(stderr, "tbv1-fake: generated a development CA at %s\n", caPath)
	}
	fmt.Fprintf(stderr, "tbv1-fake: ca %s\n", caPath)
	if *controlOut != "" {
		fmt.Fprintf(stderr, "tbv1-fake: control key written to %s\n", *controlOut)
	}
	if *seed {
		fmt.Fprintf(stderr, "tbv1-fake: seeded tenant %s, database %s\n", srv.Seed.TenantID, srv.Seed.Database)
		if *dataOut != "" {
			fmt.Fprintf(stderr, "tbv1-fake: data key written to %s\n", *dataOut)
		}
	}
	if *printControl {
		fmt.Fprintf(stdout, "TB_CONTROL_KEY=%s\n", srv.ControlKey)
		if *seed {
			fmt.Fprintf(stdout, "TB_DATA_KEY=%s\n", srv.Seed.DataKey)
		}
	}
	fmt.Fprintf(stderr, "tbv1-fake: ready (GET %s/v1/readyz)\n", srv.URL)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	<-ctx.Done()
	fmt.Fprintln(stderr, "tbv1-fake: shutting down; all state is discarded")
	return nil
}

// loadOrCreateCerts loads ca.pem, server.pem and server-key.pem from dir, or
// generates and writes a new development CA and leaf when any is missing.
func loadOrCreateCerts(dir string, hosts []string) (*v1test.Certificates, bool, error) {
	paths := map[string]string{"ca": filepath.Join(dir, "ca.pem"), "cert": filepath.Join(dir, "server.pem"), "key": filepath.Join(dir, "server-key.pem")}
	read := func(p string) ([]byte, error) { return os.ReadFile(p) }
	ca, errCA := read(paths["ca"])
	cert, errCert := read(paths["cert"])
	key, errKey := read(paths["key"])
	if errCA == nil && errCert == nil && errKey == nil {
		c := &v1test.Certificates{CACertPEM: ca, LeafCertPEM: cert, LeafKeyPEM: key}
		if _, err := c.TLSCertificate(); err != nil {
			return nil, false, fmt.Errorf("%s: %w", dir, err)
		}
		return c, false, nil
	}
	for _, err := range []error{errCA, errCert, errKey} {
		if err != nil && !errors.Is(err, fs.ErrNotExist) {
			return nil, false, err
		}
	}
	c, err := v1test.GenerateCertificates(hosts, 0)
	if err != nil {
		return nil, false, err
	}
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, false, err
	}
	files := []struct {
		path string
		data []byte
		mode os.FileMode
	}{
		{paths["ca"], c.CACertPEM, 0o644},
		{filepath.Join(dir, "ca-key.pem"), c.CAKeyPEM, 0o600},
		{paths["cert"], c.LeafCertPEM, 0o644},
		{paths["key"], c.LeafKeyPEM, 0o600},
	}
	for _, f := range files {
		if err := os.WriteFile(f.path, f.data, f.mode); err != nil {
			return nil, false, err
		}
	}
	return c, true, nil
}

// writeSecret writes a token followed by a newline with mode 0600, also
// tightening the mode of an existing file.
func writeSecret(path, secret string) error {
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_TRUNC, 0o600)
	if err != nil {
		return err
	}
	if err := f.Chmod(0o600); err != nil {
		f.Close()
		return err
	}
	if _, err := f.WriteString(secret + "\n"); err != nil {
		f.Close()
		return err
	}
	return f.Close()
}
