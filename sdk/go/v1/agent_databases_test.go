package v1_test

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/url"
	"os"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

type agentRecording struct {
	Name          string
	Method        string
	URL           string
	Authorization string
	Body          json.RawMessage
	Form          url.Values
	Status        int
	Response      json.RawMessage
	Headers       map[string]string
}

type agentContract struct {
	Project  string
	Database string
	Source   string
	APIKey   string
	Happy    []agentRecording
	Failure  []agentRecording
}

type agentTransport func(*http.Request) (*http.Response, error)

func (f agentTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func loadAgentContract(t *testing.T) agentContract {
	t.Helper()
	data, err := os.ReadFile("../contracts/terrabase/agent/databases.json")
	if err != nil {
		t.Fatal(err)
	}
	var out agentContract
	if err := json.Unmarshal(data, &out); err != nil {
		t.Fatal(err)
	}
	return out
}

func agentHTTPClient(t *testing.T, records []agentRecording) (*http.Client, *atomic.Int32) {
	t.Helper()
	calls := new(atomic.Int32)
	hc := &http.Client{Transport: agentTransport(func(r *http.Request) (*http.Response, error) {
		i := int(calls.Add(1)) - 1
		if i >= len(records) {
			t.Errorf("unexpected request %s %s", r.Method, r.URL)
			return nil, errors.New("unexpected request")
		}
		want := records[i]
		if r.Method != want.Method || r.URL.String() != want.URL || r.Header.Get("Authorization") != want.Authorization {
			t.Errorf("%s: unexpected method, URL or authorization", want.Name)
		}
		if r.Context().Err() != nil {
			t.Errorf("%s: request already canceled", want.Name)
		}
		if _, ok := r.Context().Deadline(); !ok {
			t.Errorf("%s: missing request deadline", want.Name)
		}
		var data []byte
		if r.Body != nil {
			var err error
			data, err = io.ReadAll(r.Body)
			if err != nil {
				t.Error(err)
			}
		}
		if want.Form != nil {
			form, err := url.ParseQuery(string(data))
			if err != nil || !reflect.DeepEqual(form, want.Form) {
				t.Errorf("%s: unexpected token exchange form", want.Name)
			}
		} else if string(want.Body) == "null" {
			if len(data) != 0 {
				t.Errorf("%s: expected no body", want.Name)
			}
		} else {
			var gotBody, wantBody any
			if err := json.Unmarshal(data, &gotBody); err != nil {
				t.Error(err)
			}
			if err := json.Unmarshal(want.Body, &wantBody); err != nil {
				t.Error(err)
				return nil, err
			}
			if !reflect.DeepEqual(gotBody, wantBody) || r.Header.Get("Content-Type") != "application/json" {
				t.Errorf("%s: unexpected JSON body or content type", want.Name)
			}
		}
		header := http.Header{"Content-Type": {"application/json"}}
		for k, v := range want.Headers {
			header.Set(k, v)
		}
		body := string(want.Response)
		if want.Status == http.StatusNoContent {
			body = ""
		}
		return &http.Response{StatusCode: want.Status, Header: header, Body: io.NopCloser(strings.NewReader(body)), Request: r}, nil
	})}
	t.Cleanup(func() {
		if got := int(calls.Load()); got != len(records) {
			t.Errorf("requests=%d, want %d", got, len(records))
		}
	})
	return hc, calls
}

func TestAgentDatabaseLifecycleContract(t *testing.T) {
	f := loadAgentContract(t)
	hc, _ := agentHTTPClient(t, f.Happy)
	c, err := tb.New(tb.Config{APIKey: f.APIKey, HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	ttl := int64(3600)
	instant := time.Date(2026, 11, 4, 12, 0, 0, 0, time.UTC)
	for _, step := range []struct {
		name string
		call func() (*tb.ManagedDatabase, error)
	}{
		{"create", func() (*tb.ManagedDatabase, error) {
			return c.CreateDatabase(t.Context(), f.Project, "agent-run", tb.CreateDatabaseOptions{TTLSeconds: &ttl})
		}},
		{"create-permanent", func() (*tb.ManagedDatabase, error) {
			return c.CreateDatabase(t.Context(), f.Project, "permanent", tb.CreateDatabaseOptions{})
		}},
		{"fork", func() (*tb.ManagedDatabase, error) {
			return c.ForkDatabase(t.Context(), f.Project, f.Source, "agent-fork", tb.ForkDatabaseOptions{TTLSeconds: &ttl})
		}},
		{"fork-permanent", func() (*tb.ManagedDatabase, error) {
			return c.ForkDatabase(t.Context(), f.Project, "agent-run", "agent-fork", tb.ForkDatabaseOptions{})
		}},
		{"restore", func() (*tb.ManagedDatabase, error) {
			return c.ForkDatabase(t.Context(), f.Project, f.Source, "agent-restore", tb.ForkDatabaseOptions{PointInTime: &instant, TTLSeconds: &ttl})
		}},
		{"set-ttl", func() (*tb.ManagedDatabase, error) { return c.SetDatabaseTTL(t.Context(), f.Project, f.Database, ttl) }},
		{"clear-ttl", func() (*tb.ManagedDatabase, error) { return c.ClearDatabaseTTL(t.Context(), f.Project, "agent-run") }},
	} {
		got, err := step.call()
		if err != nil {
			t.Fatalf("%s: %v", step.name, err)
		}
		for _, recording := range f.Happy {
			if recording.Name == step.name {
				var want tb.ManagedDatabase
				if err := json.Unmarshal(recording.Response, &want); err != nil {
					t.Fatal(err)
				}
				if !reflect.DeepEqual(got, &want) {
					t.Errorf("%s: database metadata does not match fixture", step.name)
				}
			}
		}
	}
	key, err := c.CreateScopedKey(t.Context(), tb.ScopedKeyRequest{Project: f.Project, Database: f.Database, Access: tb.ScopedKeyReadWrite, TTLSeconds: &ttl})
	if err != nil {
		t.Fatal(err)
	}
	var want tb.ScopedKey
	if err := json.Unmarshal(f.Happy[8].Response, &want); err != nil || !reflect.DeepEqual(key, &want) {
		t.Fatal("scoped key does not match fixture")
	}
	readKey, err := c.CreateScopedKey(t.Context(), tb.ScopedKeyRequest{Database: f.Database, Access: tb.ScopedKeyRead})
	if err != nil || readKey.Access != tb.ScopedKeyRead {
		t.Fatalf("default scoped key: %v", err)
	}
	agent, err := tb.New(tb.Config{APIKey: key.Key, HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	rows, err := agent.Database(f.Project, f.Database).Query(t.Context(), "SELECT 1")
	if err != nil || len(rows) != 1 {
		t.Fatalf("agent query: %v", err)
	}
	if got, err := rows[0].Int64("1"); err != nil || got != 1 {
		t.Fatalf("agent query value=%d err=%v", got, err)
	}
	if err := c.DeleteDatabase(t.Context(), f.Project, f.Database); err != nil {
		t.Fatal(err)
	}
}

func TestAgentDatabaseRefusalsContract(t *testing.T) {
	f := loadAgentContract(t)
	hc, calls := agentHTTPClient(t, f.Failure)
	c, err := tb.New(tb.Config{APIKey: f.APIKey, HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	for _, ttl := range []int64{0, 59, 31536001} {
		if _, err := c.CreateDatabase(t.Context(), f.Project, "agent-run", tb.CreateDatabaseOptions{TTLSeconds: &ttl}); err == nil {
			t.Fatal("accepted invalid database TTL")
		}
		if _, err := c.ForkDatabase(t.Context(), f.Project, f.Source, "agent-fork", tb.ForkDatabaseOptions{TTLSeconds: &ttl}); err == nil {
			t.Fatal("accepted invalid fork TTL")
		}
		if _, err := c.SetDatabaseTTL(t.Context(), f.Project, f.Database, ttl); err == nil {
			t.Fatal("accepted invalid TTL update")
		}
	}
	for _, ttl := range []int64{0, 59, 2592001} {
		if _, err := c.CreateScopedKey(t.Context(), tb.ScopedKeyRequest{Database: f.Database, Access: tb.ScopedKeyRead, TTLSeconds: &ttl}); err == nil {
			t.Fatal("accepted invalid key TTL")
		}
	}
	for _, req := range []tb.ScopedKeyRequest{{Database: "agent-run", Access: tb.ScopedKeyRead}, {Database: f.Database, Access: "admin"}, {Project: "project-name", Database: f.Database, Access: tb.ScopedKeyRead}} {
		if _, err := c.CreateScopedKey(t.Context(), req); err == nil {
			t.Fatal("accepted invalid scoped key request")
		}
	}
	if calls.Load() != 0 {
		t.Fatal("invalid input made a request")
	}
	ttl := int64(3600)
	for _, fixture := range f.Failure[:4] {
		_, err := c.CreateScopedKey(t.Context(), tb.ScopedKeyRequest{Project: f.Project, Database: f.Database, Access: tb.ScopedKeyReadWrite, TTLSeconds: &ttl})
		var apiErr *tb.APIError
		if !errors.As(err, &apiErr) || apiErr.Status != fixture.Status || !errors.Is(err, tb.ErrorCode(fixture.Name)) {
			t.Fatalf("%s: lost HTTP error details", fixture.Name)
		}
		if fixture.Status == 429 && (apiErr.RetryAfter != 7*time.Second || apiErr.Header.Get("Retry-After") != "7") {
			t.Fatal("lost Retry-After")
		}
	}
	tokenClient, err := tb.New(tb.Config{Token: "header.project.signature", HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	if err := tokenClient.DeleteDatabase(t.Context(), f.Project, f.Database); !errors.Is(err, tb.ErrNotFound) {
		t.Fatalf("expired database: %v", err)
	}
	if _, err := tokenClient.CreateScopedKey(t.Context(), tb.ScopedKeyRequest{Database: f.Database, Access: tb.ScopedKeyRead}); err == nil {
		t.Fatal("minted a key without an API key")
	}
}
