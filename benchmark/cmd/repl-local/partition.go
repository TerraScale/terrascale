package main

import (
	"bytes"
	"context"
	"encoding/json/jsontext"
	"encoding/json/v2"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
	"terrascale.tech/ts-benchmark/internal/repllocal"
)

// Every proof PUT is sent once: retrying 503 would hide ambiguous client outcomes.
func partitionProof(parent context.Context, c *repllocal.Cluster, metrics map[string]float64) error {
	ctx, cancel := context.WithTimeout(parent, 2*time.Minute)
	defer cancel()
	clients := map[string]*replbench.Region{}
	for name := range c.Nodes {
		client, err := replbench.NewRegion(name, c.Targets(), 4)
		if err != nil {
			return err
		}
		clients[name] = client
	}
	collection := "proof-" + strings.ToLower(ulid.Make().String())
	response, err := clients["gru"].Do(ctx, http.MethodPost, "/collections", []byte(`{"name":"`+collection+`"}`), "application/json")
	if err != nil || response.Status/100 != 2 {
		return fmt.Errorf("proof collection: HTTP %d; call failed=%t", response.Status, err != nil)
	}
	read := func(client *replbench.Region, path string, status int, value []byte) error {
		for {
			response, err := client.Do(ctx, http.MethodGet, path, nil, "")
			if err != nil {
				return err
			}
			if response.Status == status {
				if status != http.StatusOK {
					return nil
				}
				var body struct {
					Document jsontext.Value `json:"document"`
				}
				if err := json.Unmarshal(response.Body, &body); err != nil {
					return err
				}
				got, err := replbench.Canonical(body.Document)
				if err != nil || !bytes.Equal(got, value) {
					return errors.New("proof document differs")
				}
				return nil
			}
			if response.Status != http.StatusNotFound && response.Status != http.StatusServiceUnavailable {
				return fmt.Errorf("proof read: HTTP %d", response.Status)
			}
			select {
			case <-ctx.Done():
				return ctx.Err()
			case <-time.After(10 * time.Millisecond):
			}
		}
	}
	// Require explicit remote durability in both directions before cutting the only peer path.
	for name, client := range clients {
		id := ulid.Make().String()
		path, value := replbench.DocPath(collection, id), []byte(`{"proof":"`+id+`"}`)
		response, err := client.Do(ctx, http.MethodPut, path, value, "application/json")
		if err != nil {
			return err
		}
		if err := requireOutcome(response, http.StatusCreated, "remote_durable", false); err != nil {
			return err
		}
		for _, replica := range clients {
			if err := read(replica, path, http.StatusOK, value); err != nil {
				return err
			}
		}
		metrics[name+".remote_durable_probe"] = 1
	}
	id := ulid.Make().String()
	path, value := replbench.DocPath(collection, id), []byte(`{"proof":"`+id+`"}`)
	dir := filepath.Join(c.Work, "sjc")
	hold := filepath.Join(dir, "ack.hold")
	if err := os.WriteFile(hold, []byte(id), 0o600); err != nil {
		return err
	}
	defer os.Remove(hold)
	type result struct {
		response replbench.Response
		err      error
	}
	pending := make(chan result, 1)
	go func() {
		response, err := clients["gru"].Do(ctx, http.MethodPut, path, value, "application/json")
		pending <- result{response, err}
	}()
	for {
		b, err := os.ReadFile(filepath.Join(dir, "ack.reached"))
		if err == nil && string(b) == strconv.Itoa(c.Nodes["sjc"].Target.PID) {
			break
		}
		select {
		case outcome := <-pending:
			return fmt.Errorf("held ACK returned early: HTTP %d; call failed=%t", outcome.response.Status, outcome.err != nil)
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
	cut := time.Now()
	for _, node := range c.Nodes {
		node.Proxy.Suspend()
	}
	// Keep the ACK held until the client outcome is known, including across stream cancellation.
	outcome := <-pending
	metrics["partition.post_commit_response_ms"] = float64(time.Since(cut).Microseconds()) / 1000
	if outcome.err != nil {
		return outcome.err
	}
	if err := requireOutcome(outcome.response, http.StatusServiceUnavailable, "write_outcome_unknown", false); err != nil {
		return err
	}
	metrics["partition.unknown"] = 1
	for name, client := range clients {
		if err := read(client, path, http.StatusOK, value); err != nil {
			return err
		}
		metrics[name+".unknown_copy_present"] = 1
	}
	for name, client := range clients {
		absent := replbench.DocPath(collection, ulid.Make().String())
		started := time.Now()
		response, err := client.Do(ctx, http.MethodPut, absent, []byte(`{"refused":true}`), "application/json")
		metrics[name+".partition_refusal_ms"] = float64(time.Since(started).Microseconds()) / 1000
		if err != nil {
			return err
		}
		if err := requireOutcome(response, http.StatusServiceUnavailable, "remote_region_unavailable", true); err != nil {
			return err
		}
		for _, replica := range clients {
			if err := read(replica, absent, http.StatusNotFound, nil); err != nil {
				return err
			}
		}
		metrics[name+".refused_without_execution"] = 1
	}
	if err := os.Remove(hold); err != nil {
		return err
	}
	healed := time.Now()
	for _, node := range c.Nodes {
		if err := node.Proxy.Target(node.Target.PeerURL); err != nil {
			return err
		}
	}
	// Reads may succeed before the links heal; a fresh acknowledged write proves confirmation resumes.
	path = replbench.DocPath(collection, ulid.Make().String())
	for {
		response, err := clients["gru"].Do(ctx, http.MethodPut, path, value, "application/json")
		if err != nil {
			return err
		}
		if response.Status == http.StatusCreated || response.Status == http.StatusOK {
			if err := requireOutcome(response, response.Status, "remote_durable", false); err != nil {
				return err
			}
			break
		}
		if err := requireOutcome(response, http.StatusServiceUnavailable, "remote_region_unavailable", true); err != nil {
			return err
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
	metrics["partition.confirmation_resumed_ms"] = float64(time.Since(healed).Microseconds()) / 1000
	return nil
}

func requireOutcome(response replbench.Response, status int, code string, retryAfter bool) error {
	var body struct {
		Durability string `json:"durability"`
		Error      struct {
			Code string `json:"code"`
		} `json:"error"`
	}
	if err := json.Unmarshal(response.Body, &body); err != nil {
		return err
	}
	got := body.Durability
	if status == http.StatusServiceUnavailable {
		got = body.Error.Code
		if body.Durability != "" {
			return errors.New("refusal claims durability")
		}
	}
	if response.Status != status || got != code || response.HasRetryAfter != retryAfter {
		return fmt.Errorf("proof outcome: HTTP %d, code %q, Retry-After=%t; want %d/%s/%t", response.Status, got, response.HasRetryAfter, status, code, retryAfter)
	}
	return nil
}
