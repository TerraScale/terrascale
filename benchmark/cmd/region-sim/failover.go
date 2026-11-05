package main

import (
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptrace"
	"syscall"
	"time"

	natsclient "github.com/nats-io/nats.go"
)

func (s *sim) homeFailover() (string, string, error) {
	if err := s.api.mustExec(s.ctx, s.c.a, stmt{SQL: "CREATE TABLE failover (id INTEGER PRIMARY KEY)"}); err != nil {
		return "", "", err
	}
	if _, err := s.api.waitFor(s.ctx, s.c.b, visibleTimeout, visiblePoll, "[[0]]", "SELECT count(*) FROM failover"); err != nil {
		return "", "", err
	}
	nc, err := natsclient.Connect(s.c.nats.url(), natsclient.Token(s.c.natsToken), natsclient.Timeout(2*time.Second))
	if err != nil {
		return "", "", err
	}
	defer nc.Close()
	js, err := nc.JetStream()
	if err != nil {
		return "", "", err
	}
	var kv natsclient.KeyValue
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

	started := make(chan error, 1)
	inflight := make(chan bool, 1)
	requestContext := httptrace.WithClientTrace(s.ctx, &httptrace.ClientTrace{
		WroteRequest: func(info httptrace.WroteRequestInfo) { started <- info.Err },
	})
	go func() {
		r, err := s.api.execDurable(requestContext, s.c.a, "all", stmt{SQL: "INSERT INTO failover (id) VALUES (?)", Args: []any{confirmed + 1}})
		inflight <- err == nil && r.status == http.StatusOK
		if err != nil {
			select {
			case started <- err:
			default:
			}
		}
	}()
	if err := <-started; err != nil {
		return "", "", fmt.Errorf("send the in-flight write: %w", err)
	}
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
	if err != nil || after.Home != "sjc" || after.Epoch != before.Epoch+1 {
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

func readHome(kv natsclient.KeyValue, database string) (homeLease, error) {
	entry, err := kv.Get("home." + database)
	if err != nil {
		return homeLease{}, err
	}
	var lease homeLease
	err = json.Unmarshal(entry.Value(), &lease)
	return lease, err
}
