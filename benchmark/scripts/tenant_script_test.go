// Package scripts tests the shell scripts beside it against fake flyctl, gh and curl (testdata/fakes); nothing reaches
// Fly, GitHub or TerraBase.
package scripts

import (
	"bytes"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
)

const secret = "tsk_FAKE_SECRET_never_shown"

var peerSecret = strings.Repeat("b", 64)

type tenant struct {
	t    *testing.T
	dir  string // fake Fly, GitHub and TerraBase state
	work string // the script's working directory and HOME
	log  string
}

func newTenant(t *testing.T) *tenant {
	t.Helper()
	for _, tool := range []string{"bash", "jq", "date", "od"} {
		if _, err := exec.LookPath(tool); err != nil {
			t.Fatalf("%s is needed to run the script: %v", tool, err)
		}
	}
	base := t.TempDir()
	tn := &tenant{t: t, dir: filepath.Join(base, "state"), work: filepath.Join(base, "work"), log: filepath.Join(base, "calls.log")}
	for _, d := range []string{tn.dir, tn.work} {
		if err := os.MkdirAll(d, 0o755); err != nil {
			t.Fatal(err)
		}
	}
	return tn
}

// run runs one command of the script and answers its stdout and stderr together.
func (tn *tenant) run(command string) string {
	tn.t.Helper()
	out, err := tn.runWithPeer(command, peerSecret)
	if err != nil {
		tn.t.Fatalf("%s: %v\n%s", command, err, out)
	}
	return out
}

func (tn *tenant) runWithPeer(command, peer string) (string, error) {
	tn.t.Helper()
	fakes, _ := filepath.Abs("testdata/fakes")
	script, _ := filepath.Abs("repl-fly-tenant.sh")
	cmd := exec.Command("bash", "-x", script, command)
	cmd.Dir = tn.work
	cmd.Env = append(os.Environ(), "PATH="+fakes+":"+os.Getenv("PATH"), "HOME="+tn.work, "FAKE_DIR="+tn.dir, "FAKE_LOG="+tn.log, "FAKE_SECRET="+secret, "TERRABASE_PEER_SECRET="+peer, "TMPDIR="+tn.work)
	var out bytes.Buffer
	cmd.Stdout, cmd.Stderr = &out, &out
	err := cmd.Run()
	return out.String(), err
}

func (tn *tenant) calls() string {
	b, _ := os.ReadFile(tn.log)
	return string(b)
}

func (tn *tenant) variable(name string) string {
	b, _ := os.ReadFile(filepath.Join(tn.dir, "vars", name))
	return string(b)
}

// noSecretOutside fails if the key appears anywhere but the fake Fly secret store: output, argv log or any file.
func (tn *tenant) noSecretOutside(output string) {
	tn.t.Helper()
	if strings.Contains(output, secret) || strings.Contains(tn.calls(), secret) || strings.Contains(output, peerSecret) || strings.Contains(tn.calls(), peerSecret) {
		tn.t.Fatal("the key appeared in output or on a command line")
	}
	_ = filepath.Walk(filepath.Dir(tn.dir), func(path string, info os.FileInfo, err error) error {
		if err == nil && !info.IsDir() && path != filepath.Join(tn.dir, "fly-secrets") {
			if b, _ := os.ReadFile(path); bytes.Contains(b, []byte(secret)) || bytes.Contains(b, []byte(peerSecret)) {
				tn.t.Fatalf("the key was written to %s", path)
			}
		}
		return nil
	})
}

func TestDryRunChangesNothing(t *testing.T) {
	t.Parallel()
	tn := newTenant(t)
	out := tn.run("dry-run")
	if !strings.Contains(out, "dry run: nothing was changed") || !strings.Contains(out, "TS_BENCH_API_KEY on app ts-bench") {
		t.Fatalf("dry-run output:\n%s", out)
	}
	for _, mutation := range []string{"secrets import", "secrets unset", "apps create", "variable set", "variable delete", "curl POST", "curl DELETE", "flyctl proxy"} {
		if strings.Contains(tn.calls(), mutation) {
			t.Fatalf("dry-run made %q:\n%s", mutation, tn.calls())
		}
	}
}

func TestInstallHandsTheKeyOnlyToFlyAndRotatesOnTheNextRun(t *testing.T) {
	t.Parallel()
	tn := newTenant(t)
	out := tn.run("install")
	tn.noSecretOutside(out)
	stored, _ := os.ReadFile(filepath.Join(tn.dir, "fly-secrets"))
	if string(stored) != "TS_BENCH_API_KEY="+secret+"-1\n" {
		t.Fatalf("Fly secret store holds %q", stored)
	}
	for _, name := range []string{"OWNER", "ORGANIZATION", "PROJECT", "DATABASE_ID", "KEY_ID", "KEY_EXPIRES"} {
		if tn.variable("TS_BENCH_FLY_"+name) == "" {
			t.Fatalf("variable TS_BENCH_FLY_%s not set", name)
		}
	}
	calls := tn.calls()
	if strings.Count(calls, "curl POST /v1/management/commits") != 1 || !strings.Contains(calls, "flyctl apps create ts-bench") ||
		!strings.Contains(calls, "flyctl secrets import --app ts-bench --stage") {
		t.Fatalf("install calls:\n%s", calls)
	}
	first, organization := tn.variable("TS_BENCH_FLY_KEY_ID"), tn.variable("TS_BENCH_FLY_ORGANIZATION")

	out = tn.run("install")
	tn.noSecretOutside(out)
	calls = tn.calls()
	if strings.Count(calls, "curl POST /v1/management/commits") != 1 || tn.variable("TS_BENCH_FLY_ORGANIZATION") != organization {
		t.Fatal("a second install must reuse the tenant")
	}
	if tn.variable("TS_BENCH_FLY_KEY_ID") == first || !strings.Contains(calls, "curl DELETE /v1/management/organizations/"+organization+"/projects/"+tn.variable("TS_BENCH_FLY_PROJECT")+"/api-keys/"+first) {
		t.Fatalf("a second install must mint a new key and revoke %s:\n%s", first, calls)
	}
}

func TestStatusCleanupAndRevoke(t *testing.T) {
	t.Parallel()
	tn := newTenant(t)
	if out := tn.run("status"); !strings.Contains(out, "not installed") {
		t.Fatalf("status before install:\n%s", out)
	}
	tn.run("install")
	out := tn.run("status")
	if !strings.Contains(out, "active, expires") || !strings.Contains(out, "d1g3st") || !strings.Contains(out, "2 bench-* collections") {
		t.Fatalf("status:\n%s", out)
	}
	tn.noSecretOutside(out)

	out = tn.run("cleanup")
	calls := tn.calls()
	if !strings.Contains(out, "dropped 2") || !strings.Contains(calls, "collections/bench-01aa") || !strings.Contains(calls, "collections/bench-01bb") ||
		strings.Contains(calls, "DELETE /v1/projects/"+tn.variable("TS_BENCH_FLY_PROJECT")+"/databases/bench/collections/keep-me") {
		t.Fatalf("cleanup must drop only bench-* collections:\n%s\n%s", out, calls)
	}

	key := tn.variable("TS_BENCH_FLY_KEY_ID")
	tn.run("revoke")
	if !strings.Contains(tn.calls(), "/api-keys/"+key) || tn.variable("TS_BENCH_FLY_KEY_ID") != "" {
		t.Fatal("revoke must revoke the key and forget its id")
	}
	if _, err := os.Stat(filepath.Join(tn.dir, "fly-secrets")); !os.IsNotExist(err) {
		t.Fatal("revoke must remove the Fly secret")
	}
}

func TestMissingOrMalformedPeerCredentialFailsBeforeProxy(t *testing.T) {
	t.Parallel()
	for _, peer := range []string{"", "short", strings.Repeat("g", 64), strings.Repeat("a", 63) + "\n"} {
		tn := newTenant(t)
		out, err := tn.runWithPeer("install", peer)
		if err == nil || !strings.Contains(out, "TERRABASE_PEER_SECRET must contain 64 hexadecimal characters") {
			t.Fatalf("invalid peer credential did not fail closed: %v", err)
		}
		if strings.Contains(tn.calls(), "flyctl proxy") || strings.Contains(tn.calls(), "curl") {
			t.Fatal("invalid credential reached the internal listener")
		}
		tn.noSecretOutside(out)
	}
}
