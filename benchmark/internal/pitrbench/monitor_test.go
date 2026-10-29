package pitrbench

import (
	"math"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

func TestMonitorStopsAtEachG1Limit(t *testing.T) {
	t.Parallel()
	g1 := limits{stored: MonitorMaxStoredBytes, usd: MonitorMaxUSD, run: MonitorMaxRunBytes, rss: MonitorMaxRSSBytes}
	ok := MonitorSample{StoredBytes: 4_499_999_999, USD: 0.79, RunBytes: 27_999_999_999, NodeRSSMax: 7_999_999_999}
	if b := g1.breach(ok); b != "" {
		t.Fatalf("below every limit: %s", b)
	}
	for name, s := range map[string]MonitorSample{
		"stored bytes":     {StoredBytes: 4_500_000_000},
		"first-month cost": {USD: 0.80},
		"node RSS":         {NodeRSSMax: 8_000_000_000},
		"run base":         {RunBytes: 28_000_000_000},
	} {
		if b := g1.breach(s); !strings.Contains(b, name) {
			t.Fatalf("%s: breach %q", name, b)
		}
	}
}

func TestMonitorCostEstimate(t *testing.T) {
	t.Parallel()
	// 4 GiB stored, 1,000 objects in 2 generations, 100 MiB of snapshots uploaded, 3 failed passes, 4 drills; the
	// monitor listed 6 pages, the harness 10 and it read 2,000 objects.
	s := MonitorSample{StoredBytes: 4 << 30, Objects: 1000, MonitorLists: 6,
		Node: map[string]float64{"generations": 2, "uploaded_snapshot_bytes": 100 << 20, "failures": 3, "drills": 4}}
	cost(&s, 10, 2000)
	wantA := int64(1000 + (20 + 2) + 5*3 + 6 + 10 + 4) // objects, parts, failed passes' attempts, lists, drill lists
	wantB := int64(2000 + 4*500)                       // harness gets, drills reading a generation each
	wantUSD := 4*0.02 + float64(wantA)*0.005/1000 + float64(wantB)*0.0005/1000
	if s.ClassA != wantA || s.ClassB != wantB || math.Abs(s.USD-wantUSD) > 1e-12 {
		t.Fatalf("estimate A %d B %d $%f, want A %d B %d $%f", s.ClassA, s.ClassB, s.USD, wantA, wantB, wantUSD)
	}
	// A retry storm alone reaches the cost limit: 32,000 failed passes are 160,000 class A requests.
	storm := MonitorSample{Node: map[string]float64{"failures": 32_000}}
	cost(&storm, 0, 0)
	if storm.USD < MonitorMaxUSD {
		t.Fatalf("a retry storm is estimated at $%f", storm.USD)
	}
}

func TestMonitorCountersSurviveNodeRestarts(t *testing.T) {
	t.Parallel()
	var c counters
	c.add(map[string]float64{"failures": 2, "drills": 1})
	got := c.add(map[string]float64{"failures": 5, "drills": 1})
	if got["failures"] != 5 {
		t.Fatalf("%v", got)
	}
	got = c.add(map[string]float64{"failures": 1, "drills": 0}) // a restart resets the node's counters
	if got["failures"] != 6 || got["drills"] != 1 {
		t.Fatalf("after a restart: %v, want failures 6, drills 1", got)
	}
}

func TestMonitorPassesBucketSettingsButNeverTheMasterKey(t *testing.T) {
	t.Parallel()
	path := filepath.Join(t.TempDir(), "node.env")
	if err := os.WriteFile(path, []byte("AWS_ENDPOINT_URL_S3=https://127.0.0.1:1\nBUCKET_NAME=pitr\nAWS_ACCESS_KEY_ID=k\nAWS_SECRET_ACCESS_KEY=s\nTERRABASE_MASTER_KEY=secret\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	env, err := inventoryEnvFrom(path, []string{"AWS_PROFILE=someone-else"})
	if err != nil {
		t.Fatal(err)
	}
	if slices.ContainsFunc(env, func(kv string) bool {
		return strings.HasPrefix(kv, "TERRABASE_MASTER_KEY=") || strings.HasPrefix(kv, "AWS_PROFILE=")
	}) ||
		!slices.Contains(env, "BUCKET_NAME=pitr") || !slices.Contains(env, "AWS_SECRET_ACCESS_KEY=s") {
		t.Fatalf("inventory environment: %d entries, master key or foreign AWS setting present, or bucket settings missing", len(env))
	}
	if err := os.Chmod(path, 0o644); err != nil {
		t.Fatal(err)
	}
	if _, err := inventoryEnvFrom(path, []string{"AWS_PROFILE=someone-else"}); err == nil {
		t.Fatal("a world-readable env file was accepted")
	}
}
