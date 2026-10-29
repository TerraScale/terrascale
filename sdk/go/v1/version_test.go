package v1_test

// Version tokens are opaque and newer response members decode: checked with a
// scripted transport, since the server only issues tbv1 versions.

import (
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

// scriptTransport answers each request with the next scripted response and
// records the requests.
type scriptTransport struct {
	responses []scripted
	requests  []*http.Request
}

type scripted struct {
	status int
	header http.Header
	body   string
}

func (s *scriptTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	s.requests = append(s.requests, r)
	if len(s.responses) == 0 {
		return nil, errors.New("no scripted response")
	}
	next := s.responses[0]
	s.responses = s.responses[1:]
	h := http.Header{"Content-Type": {"application/json"}}
	for k, v := range next.header {
		h[k] = v
	}
	return &http.Response{StatusCode: next.status, Header: h, Body: io.NopCloser(strings.NewReader(next.body)), Request: r}, nil
}

func scriptedUsers(t *testing.T, responses ...scripted) (*v1.Collection, *v1.Database, *scriptTransport) {
	t.Helper()
	tr := &scriptTransport{responses: responses}
	c, err := v1.New(v1.Config{BaseURL: "https://tb.example", Token: testToken, HTTPClient: &http.Client{Transport: tr}})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(testProject, "main")
	return db.Collection("users"), db, tr
}

func TestOpaqueVersions(t *testing.T) {
	const id = "01J2KE8Z1VFMQ8Y0000000006G"
	for _, version := range []string{"tbv1-0123456789abcdef0123456789abcdef", "tbv2-AQID.Bf_x-9~"} {
		t.Run(version[:4], func(t *testing.T) {
			busy := scripted{status: http.StatusTooManyRequests, header: http.Header{"Retry-After": {"0"}}, body: `{"error":{"code":"rate_limited","message":"slow down"}}`}
			written := `{"id":"` + id + `","version":"` + version + `","offset":"7","durability":"region_durable","document":{"a":1}}`
			users, db, tr := scriptedUsers(t,
				// If-Match writes stay retryable.
				busy, scripted{status: http.StatusOK, header: http.Header{"Etag": {`"` + version + `"`}}, body: written},
				busy, scripted{status: http.StatusOK, body: `{"offset":"8","durability":"region_durable","results":[{"id":"` + id + `","version":"` + version + `"}]}`},
				scripted{status: http.StatusNotModified, header: http.Header{"Etag": {`"` + version + `"`}}},
				// commit_uncertain on a mutation is returned, not replayed.
				scripted{status: http.StatusServiceUnavailable, body: `{"error":{"code":"commit_uncertain","message":"unknown"}}`},
			)
			ctx := t.Context()

			res, err := users.Patch(ctx, id, v1.MergePatch(map[string]any{"a": 1}), v1.IfMatch(version))
			if err != nil || res.Version != version || res.Durability != "region_durable" {
				t.Fatalf("Patch = %+v, %v", res, err)
			}
			tx, err := db.Tx().Delete("users", id, v1.IfMatch(version)).Commit(ctx)
			if err != nil || tx.Results[0].Version != version {
				t.Fatalf("Commit = %+v, %v", tx, err)
			}
			var apiErr *v1.APIError
			if _, err := users.Get(ctx, id, v1.IfNoneMatch(version)); !errors.Is(err, v1.ErrNotModified) || !errors.As(err, &apiErr) || apiErr.ETag != version {
				t.Fatalf("Get = %v", err)
			}
			if _, err := users.Delete(ctx, id, v1.IfMatch(version)); !errors.Is(err, v1.ErrCommitUncertain) {
				t.Fatalf("Delete = %v", err)
			}

			if len(tr.requests) != 6 {
				t.Fatalf("%d requests, want 6", len(tr.requests))
			}
			for i, h := range []string{"If-Match", "If-Match", "", "", "If-None-Match", "If-Match"} {
				if h != "" && tr.requests[i].Header.Get(h) != `"`+version+`"` {
					t.Errorf("request %d %s = %q", i, h, tr.requests[i].Header.Get(h))
				}
			}
		})
	}
}

func TestNewResponseMembers(t *testing.T) {
	const id = "01J2KE8Z1VFMQ8Y0000000006G"
	users, _, _ := scriptedUsers(t,
		scripted{status: http.StatusOK, body: `{"id":"` + id + `","version":"tbv2-x","state":"conflict","causal":{"clock":"opaque"},"siblings":[
			{"event":{"replica":"` + testProject + `","incarnation":"0123456789abcdef0123456789abcdef","sequence":"1","hlc":"9"},"deleted":false,"document":{"n":1},"origin":"gru"},
			{"event":{"replica":"` + testOrg + `","incarnation":"0123456789abcdef0123456789abcdef","sequence":"2"},"deleted":true}]}`},
		scripted{status: http.StatusOK, body: `{"id":"` + id + `","version":"tbv2-y","state":"merged","document":{"n":2}}`},
	)
	ctx := t.Context()
	doc, err := users.Get(ctx, id)
	if err != nil || doc.State != v1.StateConflict || len(doc.Siblings) != 2 || !doc.Siblings[1].Deleted || string(doc.Siblings[0].Document) != `{"n":1}` {
		t.Fatalf("conflict Get = %+v, %v", doc, err)
	}
	doc, err = users.Get(ctx, id)
	if err != nil || doc.State != "merged" || doc.Version != "tbv2-y" {
		t.Fatalf("unknown state Get = %+v, %v", doc, err)
	}
}

func TestWriteOutcomeUnknownIsNeverReplayed(t *testing.T) {
	for _, conditional := range []bool{false, true} {
		name := "unconditional"
		if conditional {
			name = "conditional"
		}
		t.Run(name, func(t *testing.T) {
			const body = `{"error":{"code":"write_outcome_unknown","message":"no remote acknowledgement"}}`
			users, _, tr := scriptedUsers(t, scripted{status: http.StatusServiceUnavailable, header: http.Header{"Retry-After": {"0"}}, body: body})
			var options []v1.WriteOption
			if conditional {
				options = append(options, v1.IfMatch("tbv1-current"))
			}
			_, err := users.Put(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2", map[string]any{"n": 1}, options...)
			var apiErr *v1.APIError
			if !errors.Is(err, v1.ErrWriteOutcomeUnknown) || !errors.Is(err, v1.ErrCommitUncertain) || !errors.As(err, &apiErr) {
				t.Fatalf("uncertain write error = %v", err)
			}
			if apiErr.Code != v1.ErrWriteOutcomeUnknown || string(apiErr.Body) != body || len(tr.requests) != 1 {
				t.Fatalf("error=%+v requests=%d", apiErr, len(tr.requests))
			}
		})
	}
}

func TestCurrentDurabilityAcknowledgements(t *testing.T) {
	users, db, _ := scriptedUsers(t,
		scripted{status: http.StatusOK, body: `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","version":"tbv1-next","offset":"7","durability":"remote_durable"}`},
		scripted{status: http.StatusOK, body: `{"offset":"7","durability":"unchanged","results":[{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD2","asserted":true}]}`},
	)
	written, err := users.Put(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FD2", map[string]any{"n": 1})
	if err != nil || written.Durability != v1.RemoteDurable {
		t.Fatalf("write=%+v error=%v", written, err)
	}
	asserted, err := db.Tx().AssertAbsent("users", "01ARZ3NDEKTSV4RRFFQ69G5FD2").Commit(t.Context())
	if err != nil || asserted.Durability != v1.Unchanged {
		t.Fatalf("assertion=%+v error=%v", asserted, err)
	}
}
