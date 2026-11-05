// Command region-sim runs a two-region TerraBase cluster on one host (gru-1 home, sjc-1 replica) and drives it with
// the real ts CLI through an HTTPS_PROXY that maps the CLI's fixed origins to the selected region and a test-only
// ts-auth stand-in. It reports pass/fail and replica lag per scenario as a Markdown table. See
// benchmark/region-sim/README.md.
package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"slices"
	"strings"
	"syscall"
	"time"
)

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "region-sim:", err)
		os.Exit(1)
	}
}

type outcome struct {
	name string
	ok   bool
	lag  string
	note string
}

type sim struct {
	ctx     context.Context
	dir     string
	logDir  string
	tsBin   string
	ca      string
	home    string
	auth    *authStandIn
	proxies map[string]*regionProxy
	c       *cluster
	api     *sqlAPI
	org     string
	project string
	db      string
	dbID    string
	results []outcome
}

const (
	visibleTimeout = 30 * time.Second
	visiblePoll    = 5 * time.Millisecond
)

func run() error {
	terrabase := flag.String("terrabase", "", "built TerraBase binary or dll (make terrabase-build: src/TerraBase/bin/Debug/net11.0/terrabase)")
	tsBin := flag.String("ts", "", "ts CLI binary (go build ./cmd/ts in sdk/go)")
	out := flag.String("out", "", "also write the Markdown report to this file")
	logs := flag.String("logs", "", "directory to keep node, NATS and CLI logs in (default: removed with the run directory)")
	flag.DurationVar(&peerDelay, "peer-delay", 0, "one-way latency added to the peer path between the two nodes, e.g. 85ms for gru-sjc")
	lagWrites = flag.Int("lag-writes", 100, "single-row writes in the replica-lag scenario")
	flag.Parse()
	if *terrabase == "" || *tsBin == "" {
		flag.Usage()
		return errors.New("-terrabase and -ts are required")
	}
	for _, p := range []*string{terrabase, tsBin} {
		abs, err := filepath.Abs(*p)
		if err != nil {
			return err
		}
		if _, err := os.Stat(abs); err != nil {
			return err
		}
		*p = abs
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	dir, err := os.MkdirTemp("", "region-sim-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(dir)
	logDir := dir
	if *logs != "" {
		if logDir, err = filepath.Abs(*logs); err != nil {
			return err
		}
		if err := os.MkdirAll(logDir, 0o700); err != nil {
			return err
		}
	}

	s := &sim{ctx: ctx, dir: dir, tsBin: *tsBin, home: filepath.Join(dir, "home"), proxies: map[string]*regionProxy{}, db: "simdb"}
	started := time.Now()
	report, failed := s.execute(*terrabase, logDir)
	header := fmt.Sprintf("# region-sim %s\n\nTerraBase `%s`, ts `%s`; auth: region-sim ts-auth stand-in (test-only); run %s.\n\n",
		time.Now().UTC().Format(time.RFC3339), *terrabase, *tsBin, time.Since(started).Round(time.Second))
	fmt.Print(header + report)
	if *out != "" {
		if err := os.MkdirAll(filepath.Dir(*out), 0o755); err != nil {
			return err
		}
		if err := os.WriteFile(*out, []byte(header+report), 0o644); err != nil {
			return err
		}
	}
	if failed > 0 {
		return fmt.Errorf("%d scenario(s) failed", failed)
	}
	return nil
}

// execute sets up the run, runs every scenario and always tears down what it started.
func (s *sim) execute(terrabase, logDir string) (string, int) {
	defer func() {
		for _, p := range s.proxies {
			p.close()
		}
		if s.auth != nil {
			s.auth.close()
		}
		s.c.close()
	}()
	if err := s.setup(terrabase, logDir); err != nil {
		s.record("setup", false, "", err.Error())
		if s.c != nil {
			for _, n := range []*node{s.c.a, s.c.b} {
				if n != nil {
					fmt.Fprintf(os.Stderr, "--- %s log tail ---\n%s\n", n.name, tail(n.logPath, 60))
				}
			}
		}
		return s.table(), 1
	}
	steps := []struct {
		name string
		fn   func() (string, string, error)
	}{
		{"cli-login", s.login},
		{"create-project-db-replica-sjc", s.create},
		{"dml-in-A-visible-in-B", s.dml},
		{"restart-B-catches-up", s.restartB},
		{"replica-lag", s.replicaLag},
		{"home-failover-keeps-durable-writes", s.homeFailover},
	}
	prerequisite := map[string]bool{"cli-login": true, "create-project-db-replica-sjc": true}
	var blocked string
	for _, step := range steps {
		if blocked != "" {
			s.record(step.name, false, "", "not run: "+blocked+" failed")
			continue
		}
		if s.ctx.Err() != nil {
			s.record(step.name, false, "", "interrupted")
			continue
		}
		fmt.Fprintf(os.Stderr, "region-sim: %s\n", step.name)
		lag, note, err := step.fn()
		if err != nil {
			s.record(step.name, false, lag, strings.TrimSpace(note+" "+err.Error()))
			if prerequisite[step.name] {
				blocked = step.name
			}
			continue
		}
		s.record(step.name, true, lag, note)
	}
	failed := 0
	for _, r := range s.results {
		if !r.ok {
			failed++
		}
	}
	if failed > 0 {
		for _, n := range []*node{s.c.a, s.c.b} {
			fmt.Fprintf(os.Stderr, "--- %s log tail ---\n%s\n", n.name, tail(n.logPath, 40))
		}
	}
	return s.table(), failed
}

func (s *sim) setup(terrabase, logDir string) error {
	ca, err := newAuthority()
	if err != nil {
		return err
	}
	s.ca = filepath.Join(s.dir, "ca.pem")
	if err := os.WriteFile(s.ca, ca.pem, 0o644); err != nil {
		return err
	}
	if s.auth, err = startAuth(ca); err != nil {
		return err
	}
	clusterDir := filepath.Join(s.dir, "cluster")
	if err := os.MkdirAll(clusterDir, 0o700); err != nil {
		return err
	}
	s.logDir = logDir
	s.c, err = newCluster(s.ctx, clusterConfig{dir: clusterDir, logDir: logDir, terrabase: terrabase, issuer: s.auth.issuer, caFile: s.ca})
	if err != nil {
		return err
	}
	for region, n := range map[string]*node{"gru": s.c.a, "sjc": s.c.b} {
		if s.proxies[region], err = startRegionProxy(ca, n.clientPort, s.auth); err != nil {
			return err
		}
	}
	return os.MkdirAll(s.home, 0o700)
}

func (s *sim) record(name string, ok bool, lag, note string) {
	s.results = append(s.results, outcome{name: name, ok: ok, lag: lag, note: note})
	state := "PASS"
	if !ok {
		state = "FAIL"
	}
	fmt.Fprintf(os.Stderr, "region-sim: %s %s %s %s\n", state, name, lag, note)
}

func (s *sim) table() string {
	var b strings.Builder
	b.WriteString("| scenario | pass/fail | lag | notes |\n| --- | --- | --- | --- |\n")
	for _, r := range s.results {
		state := "pass"
		if !r.ok {
			state = "**fail**"
		}
		b.WriteString(fmt.Sprintf("| %s | %s | %s | %s |\n", r.name, state, cmpOr(r.lag, "—"), strings.ReplaceAll(cell(r.note), "|", "\\|")))
	}
	return b.String()
}

func cmpOr(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func cell(s string) string {
	return truncate(strings.Join(strings.Fields(s), " "), 400)
}

// ts runs the CLI against region with an isolated HOME/XDG configuration.
func (s *sim) ts(region string, args ...string) (string, string, error) {
	ctx, cancel := context.WithTimeout(s.ctx, 90*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, s.tsBin, args...)
	cmd.Env = []string{
		"PATH=" + os.Getenv("PATH"),
		"HOME=" + s.home,
		"XDG_CONFIG_HOME=" + filepath.Join(s.home, ".config"),
		"XDG_CACHE_HOME=" + filepath.Join(s.home, ".cache"),
		"XDG_DATA_HOME=" + filepath.Join(s.home, ".local", "share"),
		"XDG_STATE_HOME=" + filepath.Join(s.home, ".local", "state"),
		"HTTPS_PROXY=" + s.proxies[region].url(),
		"SSL_CERT_FILE=" + s.ca,
	}
	var stdout, stderr bytes.Buffer
	cmd.Stdout, cmd.Stderr, cmd.Stdin = &stdout, &stderr, strings.NewReader("")
	err := cmd.Run()
	{
		if f, ferr := os.OpenFile(filepath.Join(s.logDir, "cli.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0o600); ferr == nil {
			fmt.Fprintf(f, "$ [%s] ts %s\n%s%s(exit %v)\n", region, strings.Join(args, " "), stdout.String(), stderr.String(), err)
			f.Close()
		}
	}
	if err != nil {
		return stdout.String(), stderr.String(), fmt.Errorf("ts %s on %s: %v: %s", strings.Join(args, " "), region, err, truncate(strings.TrimSpace(stderr.String()), 300))
	}
	return stdout.String(), stderr.String(), nil
}

// exec runs relational SQL through `ts exec` in region and returns the CLI's printed rows.
func (s *sim) exec(region, sql string) (string, error) {
	out, _, err := s.ts(region, "sql", "-project", s.project, s.db, sql)
	return out, err
}

func ms(d time.Duration) string { return fmt.Sprintf("%.1f ms", float64(d.Microseconds())/1000) }

// --- scenarios: each returns (lag, note, error) ---

func (s *sim) login() (string, string, error) {
	out, _, err := s.ts("gru", "auth", "login")
	if err != nil {
		return "", "", err
	}
	if !strings.Contains(out, "Signed in.") {
		return "", "", fmt.Errorf("unexpected login output: %s", truncate(out, 200))
	}
	who, _, err := s.ts("gru", "whoami")
	if err != nil {
		return "", "", err
	}
	return "", "device flow auto-approved by stand-in; whoami " + strings.TrimSpace(who), nil
}

func (s *sim) create() (string, string, error) {
	out, _, err := s.ts("gru", "projects", "create", "region-sim", "-json")
	if err != nil {
		return "", "", err
	}
	var p project
	if err := json.Unmarshal([]byte(out), &p); err != nil || p.ID == "" {
		return "", "", fmt.Errorf("projects create printed no project: %s", truncate(out, 200))
	}
	s.org, s.project = p.OrganizationID, p.ID
	s.api = newSQLAPI(s.auth, s.org, s.project, s.db)
	out, _, err = s.ts("gru", "db", "create", s.db, "-replica-regions", "sjc", "-project", s.project, "-json")
	if err != nil {
		return "", "", err
	}
	var db struct {
		ID             string   `json:"id"`
		HomeRegion     string   `json:"homeRegion"`
		ReplicaRegions []string `json:"replicaRegions"`
	}
	json.Unmarshal([]byte(out), &db)
	s.dbID = db.ID
	note := fmt.Sprintf("project %s, db %s home %s replicas %v", s.project, db.ID, db.HomeRegion, db.ReplicaRegions)
	if db.HomeRegion != "" && db.HomeRegion != "gru" || db.ReplicaRegions != nil && !slices.Contains(db.ReplicaRegions, "sjc") {
		return "", note, errors.New("placement is not home gru with replica sjc")
	}
	return "", note, nil
}

func (s *sim) dml() (string, string, error) {
	statements := []string{
		"CREATE TABLE kv (id INTEGER PRIMARY KEY, v TEXT NOT NULL)",
		"INSERT INTO kv (id, v) VALUES (1, 'one'), (2, 'two'), (3, 'three'), (4, 'four')",
		"UPDATE kv SET v = 'TWO' WHERE id = 2",
		"DELETE FROM kv WHERE id = 3",
		"INSERT INTO kv (id, v) VALUES (5, 'five')",
	}
	for _, q := range statements {
		if _, err := s.exec("gru", q); err != nil {
			return "", "", err
		}
	}
	acked := time.Now()
	want := `[[1,"one"],[2,"TWO"],[4,"four"],[5,"five"]]`
	at, err := s.api.waitFor(s.ctx, s.c.b, visibleTimeout, visiblePoll, want, "SELECT id, v FROM kv ORDER BY id")
	if err != nil {
		return "", "", err
	}
	lag := at.Sub(acked)
	out, err := s.exec("sjc", "SELECT id, v FROM kv ORDER BY id")
	if err != nil {
		return ms(lag), "visible over HTTP but CLI read on sjc failed", err
	}
	if lines := strings.Count(strings.TrimSpace(out), "\n") + 1; lines != 4 || !strings.Contains(out, "TWO") || strings.Contains(out, "three") {
		return ms(lag), "", fmt.Errorf("CLI read on sjc printed %q", truncate(out, 200))
	}
	return ms(lag), "DDL+insert/update/delete via ts exec on gru; lag = last write ack to visible on sjc", nil
}

var lagWrites *int

func (s *sim) replicaLag() (string, string, error) {
	if err := s.api.mustExec(s.ctx, s.c.a, stmt{SQL: "CREATE TABLE lag (id INTEGER PRIMARY KEY)"}); err != nil {
		return "", "", err
	}
	if _, err := s.api.waitFor(s.ctx, s.c.b, visibleTimeout, visiblePoll, "[[0]]", "SELECT count(*) FROM lag"); err != nil {
		return "", "", err
	}
	var lags []float64
	slow := 0
	for i := 1; i <= *lagWrites; i++ {
		if err := s.api.mustExec(s.ctx, s.c.a, stmt{SQL: "INSERT INTO lag (id) VALUES (?)", Args: []any{i}}); err != nil {
			return "", "", err
		}
		acked := time.Now()
		for {
			before := time.Now()
			rows, r, err := s.api.query(s.ctx, s.c.b, "SELECT id FROM lag WHERE id = ?", i)
			if d := time.Since(before); d > 50*time.Millisecond {
				slow++
				fmt.Fprintf(os.Stderr, "region-sim: slow read write=%d after-ack=%s took=%s rows=%s timing=%q\n", i, before.Sub(acked), d, rows, r.timing)
			}
			if err == nil && r.status == 200 && rows != "[]" {
				break
			}
			if time.Since(acked) > visibleTimeout {
				return "", "", fmt.Errorf("write %d not visible on sjc", i)
			}
			time.Sleep(time.Millisecond)
		}
		lags = append(lags, float64(time.Since(acked).Microseconds())/1000)
	}
	slices.Sort(lags)
	p := func(q float64) float64 { return lags[min(len(lags)-1, int(float64(len(lags))*q))] }
	return fmt.Sprintf("p50 %.1f ms / p99 %.1f ms", p(0.5), p(0.99)), fmt.Sprintf("%d writes, peer delay %s one way, %d reads on sjc over 50 ms", len(lags), peerDelay, slow), nil
}

func (s *sim) restartB() (string, string, error) {
	if _, err := s.exec("gru", "CREATE TABLE down (id INTEGER PRIMARY KEY)"); err != nil {
		return "", "", err
	}
	s.c.b.stop()
	for i := range 50 {
		if err := s.api.mustExec(s.ctx, s.c.a, stmt{SQL: "INSERT INTO down (id) VALUES (?)", Args: []any{i}}); err != nil {
			return "", "", err
		}
	}
	if _, err := s.exec("gru", "INSERT INTO down (id) VALUES (1000)"); err != nil {
		return "", "", err
	}
	began := time.Now()
	if err := s.c.b.start(s.ctx); err != nil {
		return "", "", err
	}
	ready := time.Now()
	at, err := s.api.waitFor(s.ctx, s.c.b, visibleTimeout, visiblePoll, "[[51,2225]]", "SELECT count(*), sum(id) FROM down")
	if err != nil {
		return "", fmt.Sprintf("sjc-1 ready in %s", ready.Sub(began).Round(time.Millisecond)), err
	}
	return ms(at.Sub(ready)), fmt.Sprintf("51 writes while sjc-1 down; lag = ready to caught up; restart %s", ready.Sub(began).Round(time.Millisecond)), nil
}
