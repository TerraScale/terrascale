package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"syscall"
	"time"

	"github.com/nats-io/nats.go"
)

// homeFailover is last because it changes the database's home for the rest of the run.
func (s *sim) homeFailover() (string, string, error) {
	if err := s.api.mustExec(s.ctx, s.c.a, stmt{SQL: "CREATE TABLE failover (id INTEGER PRIMARY KEY)"}); err != nil {
		return "", "", err
	}
	if _, err := s.api.waitFor(s.ctx, s.c.b, visibleTimeout, visiblePoll, "[[0]]", "SELECT count(*) FROM failover"); err != nil {
		return "", "", err
	}
	nc, err := nats.Connect(s.c.nats.url(), nats.Token(s.c.natsToken), nats.Timeout(2*time.Second))
	if err != nil {
		return "", "", err
	}
	defer nc.Close()
	js, err := nc.JetStream()
	if err != nil {
		return "", "", err
	}
	var kv nats.KeyValue
	var before homeLease
	if err := pollEvery(s.ctx, 30*time.Second, 200*time.Millisecond, func() (bool, error) {
		var err error
		kv, err = js.KeyValue("terrabase-homes")
		if err != nil {
			return false, nil
		}
		before, err = readHome(kv, s.dbID)
		return err == nil && before.Home == "gru", nil
	}); err != nil {
		return "", "", fmt.Errorf("initial home lease: %w", err)
	}
	const confirmed = 5
	for id := 1; id <= confirmed; id++ {
		r, err := s.api.execDurable(s.ctx, s.c.a, "all", stmt{SQL: "INSERT INTO failover (id) VALUES (?)", Args: []any{id}})
		if err != nil || r.status != http.StatusOK {
			return "", "", fmt.Errorf("durable write %d: status %d, %s: %v", id, r.status, r.body, err)
		}
	}

	// A request may have an unknown outcome when the process dies; count it only if acknowledged.
	started := make(chan struct{})
	inflight := make(chan bool, 1)
	go func() {
		close(started)
		r, err := s.api.execDurable(s.ctx, s.c.a, "all", stmt{SQL: "INSERT INTO failover (id) VALUES (?)", Args: []any{confirmed + 1}})
		inflight <- err == nil && r.status == http.StatusOK
	}()
	<-started
	killed := time.Now()
	if err := syscall.Kill(-s.c.a.proc.cmd.Process.Pid, syscall.SIGKILL); err != nil {
		return "", "", err
	}
	<-s.c.a.proc.done
	acked := confirmed
	if <-inflight {
		acked++
	}

	err = pollEvery(s.ctx, 60*time.Second, 200*time.Millisecond, func() (bool, error) {
		r, err := s.api.exec(s.ctx, s.c.b, stmt{SQL: "INSERT OR IGNORE INTO failover (id) VALUES (1000)"})
		return err == nil && r.status == http.StatusOK, nil
	})
	if err != nil {
		return "", "", fmt.Errorf("sjc did not become writable: %w", err)
	}
	takeover := time.Since(killed)
	after, err := readHome(kv, s.dbID)
	if err != nil || after.Home != "sjc" || after.Epoch <= before.Epoch {
		return "", "", fmt.Errorf("takeover did not advance the shared home epoch: %v", err)
	}
	want := fmt.Sprintf("[[%d]]", acked)
	if _, err := s.api.waitFor(s.ctx, s.c.b, visibleTimeout, visiblePoll, want, "SELECT count(*) FROM failover WHERE id <= ?", acked); err != nil {
		return "", "", err
	}
	if err := s.c.a.start(s.ctx); err != nil {
		return "", "", err
	}
	if _, err := s.api.waitFor(s.ctx, s.c.a, 60*time.Second, visiblePoll, "[[1]]", "SELECT count(*) FROM failover WHERE id = 1000"); err != nil {
		return "", "", fmt.Errorf("returned gru did not follow the new home: %w", err)
	}
	returned, err := readHome(kv, s.dbID)
	if err != nil || returned != after {
		return "", "", fmt.Errorf("restarted gru changed the new home's lease: %v", err)
	}
	return ms(takeover), fmt.Sprintf("SIGKILL gru-1 during a request; %d acknowledged durability=all rows retained; sjc accepts writes; restarted gru follows", acked), nil
}

type homeLease struct {
	Home  string `json:"home"`
	Epoch int64  `json:"epoch"`
}

func readHome(kv nats.KeyValue, database string) (homeLease, error) {
	entry, err := kv.Get("home." + database)
	if err != nil {
		return homeLease{}, err
	}
	var lease homeLease
	err = json.Unmarshal(entry.Value(), &lease)
	return lease, err
}
