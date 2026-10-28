package main

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"

	"terrascale.tech/sdk/v1"
)

// parityLog is a JSONL sink the fixture handler can read while the runner writes.
type parityLog struct {
	mu   sync.Mutex
	data bytes.Buffer
}

func (l *parityLog) Write(p []byte) (int, error) {
	l.mu.Lock()
	defer l.mu.Unlock()
	return l.data.Write(p)
}

func (l *parityLog) rows(t *testing.T) []map[string]any {
	l.mu.Lock()
	defer l.mu.Unlock()
	var rows []map[string]any
	for scanner := bufio.NewScanner(bytes.NewReader(l.data.Bytes())); scanner.Scan(); {
		var row map[string]any
		if err := json.Unmarshal(scanner.Bytes(), &row); err != nil {
			t.Fatal(err)
		}
		rows = append(rows, row)
	}
	return rows
}

// parityService is a TerraBase stand-in for one synthetic database: create,
// collection, seed writes, pinned point reads and the exact-name delete.
type parityService struct {
	t                *testing.T
	project, name    string
	log              *parityLog
	hangCreate       bool
	mu               sync.Mutex
	documents        map[string][]byte
	versions         map[string]string
	requestsBeforeID int
	deletes          []string
}

func (s *parityService) serve(_ int64, w http.ResponseWriter, r *http.Request) {
	databases := "/v1/projects/" + s.project + "/databases"
	s.mu.Lock()
	defer s.mu.Unlock()
	if rows := s.log.rows(s.t); len(rows) == 0 || rows[0]["type"] != "owned" || rows[0]["database"] != s.name {
		s.requestsBeforeID++
	}
	switch {
	case r.Method == http.MethodPost && r.URL.Path == databases:
		if s.hangCreate {
			s.mu.Unlock()
			<-r.Context().Done()
			s.mu.Lock()
			return
		}
		w.WriteHeader(http.StatusCreated)
		_, _ = fmt.Fprintf(w, `{"id":%q,"projectId":%q,"name":%q,"branchedFrom":null,"createdAt":"2026-10-28T00:00:00Z","region":"fra","homeRegion":"fra","replicaRegions":[],"allRegions":true,"placementVersion":1,"replicas":[]}`, v1.NewID(), s.project, s.name)
	case r.Method == http.MethodPost && r.URL.Path == databases+"/"+s.name+"/collections":
		w.WriteHeader(http.StatusCreated)
		_, _ = fmt.Fprintf(w, `{"id":%q,"name":"bench","created_at":"1"}`, v1.NewID())
	case r.Method == http.MethodPut && r.URL.Path == databases+"/"+s.name+"/collections/bench/documents/by-key":
		id := r.URL.Query().Get("id")
		body, _ := io.ReadAll(r.Body)
		s.documents[id], s.versions[id] = body, "tbv2-"+v1.NewID()
		w.WriteHeader(http.StatusCreated)
		_, _ = fmt.Fprintf(w, `{"id":%q,"version":%q,"offset":"1","durability":"remote_durable"}`, id, s.versions[id])
	case r.Method == http.MethodGet && r.URL.Path == databases+"/"+s.name+"/collections/bench/documents/by-key" && s.documents[r.URL.Query().Get("id")] != nil:
		id := r.URL.Query().Get("id")
		w.Header().Set("X-TerraBase-Region", r.Header.Get("X-TerraScale-Region-Pin"))
		_, _ = fmt.Fprintf(w, `{"id":%q,"version":%q,"state":"value","document":%s}`, id, s.versions[id], s.documents[id])
	case r.Method == http.MethodDelete && strings.HasPrefix(r.URL.Path, databases+"/"):
		s.deletes = append(s.deletes, strings.TrimPrefix(r.URL.Path, databases+"/"))
		if len(s.documents) == 0 {
			http.Error(w, `{"error":{"code":"not_found"}}`, http.StatusNotFound)
			return
		}
		w.WriteHeader(http.StatusNoContent)
	default:
		http.Error(w, `{"error":{"code":"not_found"}}`, http.StatusNotFound)
	}
}

func newParityService(t *testing.T) (*parityService, *parityFixture, *v1.Database) {
	s := &parityService{t: t, project: v1.NewID(), name: "api-perf-" + strings.ToLower(v1.NewID()), log: &parityLog{}, documents: map[string][]byte{}, versions: map[string]string{}}
	f := newParityFixture(t, s.serve)
	base := f.transport()
	t.Cleanup(base.CloseIdleConnections)
	client, err := v1.New(v1.Config{TokenSource: parityTestToken("a.b.c"), HTTPClient: &http.Client{Transport: paritySetupTransport{base, s.project, "fra"}}})
	if err != nil {
		t.Fatal(err)
	}
	return s, f, client.Database(s.project, s.name)
}

func TestParityRunnerMeasuresEveryRegionThenCleansUp(t *testing.T) {
	t.Parallel()
	s, f, database := newParityService(t)
	regions := []string{"fra", "gru", "sjc"}
	plan := parityPlan{Warmup: 20 * time.Millisecond, Measure: 50 * time.Millisecond, Drain: 10 * time.Second}
	err := parityOwn(context.Background(), json.NewEncoder(s.log), database, "fra", paritySetupDeadline, func(documents map[int]parityDocument) error {
		return parityRegionsRun(context.Background(), json.NewEncoder(s.log), parityTestToken("a.b.c"), s.project, s.name, regions, documents, f.transport, plan)
	})
	rows := s.log.rows(t)
	if err != nil || s.requestsBeforeID != 0 || len(rows) != 2+108 {
		t.Fatalf("err %v, %d requests before the owned row, %d rows", err, s.requestsBeforeID, len(rows))
	}
	seen := map[string]int{}
	for _, row := range rows[1 : len(rows)-1] {
		if row["type"] != "cell" || len(row["rejected"].([]any)) != 0 || row["latency_us"] == nil {
			t.Fatalf("cell %v", row)
		}
		seen[row["region"].(string)]++
	}
	if seen["fra"] != 36 || seen["gru"] != 36 || seen["sjc"] != 36 {
		t.Fatalf("cells per region %v", seen)
	}
	if last := rows[len(rows)-1]; last["deletion"] != "logical_accepted" || last["remote_erasure"] != "unverified" || last["create"] != "created" || len(s.deletes) != 1 || s.deletes[0] != s.name {
		t.Fatalf("cleanup %v, deletes %v", last, s.deletes)
	}
}

// A create that never answers is bounded by the setup deadline, and the exact
// owned name is still deleted; its absence is flagged for reconciliation.
func TestParityRunnerBoundsUncertainCreateAndStillCleansUp(t *testing.T) {
	t.Parallel()
	s, _, database := newParityService(t)
	s.hangCreate = true
	started := time.Now()
	err := parityOwn(context.Background(), json.NewEncoder(s.log), database, "fra", 300*time.Millisecond, func(map[int]parityDocument) error {
		t.Error("run after failed setup")
		return nil
	})
	rows := s.log.rows(t)
	last := rows[len(rows)-1]
	if err == nil || time.Since(started) > 10*time.Second || s.requestsBeforeID != 0 || len(s.deletes) != 1 || s.deletes[0] != s.name ||
		last["create"] != "uncertain" || last["deletion"] != "absent" || last["reconcile_required"] != true {
		t.Fatalf("err %v after %v, cleanup %v, deletes %v", err, time.Since(started), last, s.deletes)
	}
}

// A signal cancels the run before the create; cleanup still runs on its own context.
func TestParityRunnerCleansUpAfterCancellation(t *testing.T) {
	t.Parallel()
	s, _, database := newParityService(t)
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	err := parityOwn(ctx, json.NewEncoder(s.log), database, "fra", paritySetupDeadline, func(map[int]parityDocument) error { return nil })
	rows := s.log.rows(t)
	if err == nil || len(s.deletes) != 1 || rows[0]["type"] != "owned" || rows[len(rows)-1]["type"] != "cleanup" {
		t.Fatalf("err %v, deletes %v, rows %v", err, s.deletes, rows)
	}
}
