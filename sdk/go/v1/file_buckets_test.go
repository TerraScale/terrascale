package v1_test

import (
	"io"
	"net/http"
	"reflect"
	"strings"
	"sync/atomic"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const (
	bucketProject = "01JA0000000000000000000001"
	bucketID      = "01JA0000000000000000000002"
)

type bucketFixture struct {
	name   string
	method string
	path   string
	body   string
	status int
	reply  string
}

func bucketClient(t *testing.T, fixtures []bucketFixture) *tb.Client {
	t.Helper()
	calls := new(atomic.Int32)
	hc := &http.Client{Transport: bucketTransport(func(r *http.Request) (*http.Response, error) {
		i := int(calls.Add(1)) - 1
		if i >= len(fixtures) {
			t.Errorf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, http.ErrAbortHandler
		}
		want := fixtures[i]
		data := ""
		if r.Body != nil {
			raw, err := io.ReadAll(r.Body)
			if err != nil {
				t.Fatalf("%s: read body: %v", want.name, err)
			}
			data = string(raw)
		}
		if r.Method != want.method || r.URL.Path != want.path || data != want.body || r.Header.Get("Authorization") != "Bearer fixture.jwt.sig" {
			t.Errorf("%s: request %s %s %q, want %s %s %q", want.name, r.Method, r.URL.Path, data, want.method, want.path, want.body)
		}
		header := http.Header{"Content-Type": {"application/json"}}
		return &http.Response{StatusCode: want.status, Header: header, Body: io.NopCloser(strings.NewReader(want.reply)), Request: r}, nil
	})}
	t.Cleanup(func() {
		if int(calls.Load()) != len(fixtures) {
			t.Errorf("requests=%d, want %d", calls.Load(), len(fixtures))
		}
	})
	c, err := tb.New(tb.Config{Token: "fixture.jwt.sig", HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

type bucketTransport func(*http.Request) (*http.Response, error)

func (f bucketTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestFileBucketLifecycle(t *testing.T) {
	rules := tb.FileAccessRules{
		Read:  tb.FileAccessPolicy{Roles: []string{"reader"}},
		Write: tb.FileAccessPolicy{Roles: []string{"writer"}},
	}
	bucketJSON := `{"id":"` + bucketID + `","name":"exports","region":"gru-1","rules":{"owner":null,"read":{"owner":false,"public":false,"roles":["reader"]},"write":{"owner":false,"public":false,"roles":["writer"]}}}`
	c := bucketClient(t, []bucketFixture{
		{name: "create", method: http.MethodPost, path: "/v1/projects/" + bucketProject + "/buckets",
			body:   `{"name":"exports","rules":{"owner":null,"read":{"owner":false,"public":false,"roles":["reader"]},"write":{"owner":false,"public":false,"roles":["writer"]}}}`,
			status: http.StatusCreated, reply: bucketJSON},
		{name: "list", method: http.MethodGet, path: "/v1/projects/" + bucketProject + "/buckets",
			status: http.StatusOK, reply: `{"items":[` + bucketJSON + `],"next":null}`},
		{name: "delete", method: http.MethodDelete, path: "/v1/projects/" + bucketProject + "/buckets/" + bucketID,
			status: http.StatusNoContent},
	})
	ctx := t.Context()
	created, err := c.CreateBucket(ctx, bucketProject, "exports", rules)
	if err != nil || created.ID != bucketID || created.Name != "exports" || created.Region != "gru-1" {
		t.Fatalf("create = %+v, %v", created, err)
	}
	if !reflect.DeepEqual(created.Rules, rules) {
		t.Fatalf("create rules = %+v, want %+v", created.Rules, rules)
	}
	page, err := c.ListBuckets(ctx, bucketProject, tb.PageOptions{})
	if err != nil || len(page.Items) != 1 || page.Items[0].ID != bucketID || page.Next != "" {
		t.Fatalf("list = %+v, %v", page, err)
	}
	if err := c.DeleteBucket(ctx, bucketProject, bucketID); err != nil {
		t.Fatalf("delete = %v", err)
	}
}
