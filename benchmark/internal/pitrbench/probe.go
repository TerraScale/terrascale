package pitrbench

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/benchclient"
)

type ProbeStep struct {
	Name   string `json:"name"`
	Passed bool   `json:"passed"`
	Detail string `json:"detail"`

	Skipped bool `json:"skipped,omitempty"`
}

func Probe(ctx context.Context, r *Run) ([]ProbeStep, error) {
	c, err := r.Client(time.Minute)
	if err != nil {
		return nil, err
	}

	db, coll := "probe-"+strings.ToLower(ulid.Make().String()), "c"
	base := r.DatabasePath(db)
	commit := map[string]any{"id": ulid.Make().String(), "ops": []any{map[string]any{
		"op": "create_database", "organization": r.Organization, "project": r.Project, "name": db, "id": ulid.Make().String()}}}
	if _, err := c.JSON(ctx, http.MethodPost, "/v1/management/commits", commit, nil); err != nil {
		return nil, fmt.Errorf("probe database: %w", err)
	}
	if _, err := c.JSON(ctx, http.MethodPost, base+"/collections", map[string]string{"name": coll}, nil); err != nil {
		return nil, fmt.Errorf("probe collection: %w", err)
	}
	var steps []ProbeStep
	check := func(name string, ok bool, format string, args ...any) {
		steps = append(steps, ProbeStep{Name: name, Passed: ok, Detail: fmt.Sprintf(format, args...)})
	}
	tx := func(ops ...map[string]any) (int, []string) {
		for _, op := range ops {
			op["collection"] = coll
		}
		resp, err := c.Do(ctx, http.MethodPost, base+"/transactions", mustJSON(map[string]any{"ops": ops}))
		if err != nil {
			return 0, nil
		}
		var out struct {
			Results []struct {
				Version string `json:"version"`
			} `json:"results"`
		}
		_ = json.Unmarshal(resp.Body, &out)
		var versions []string
		for _, res := range out.Results {
			versions = append(versions, res.Version)
		}
		return resp.Status, versions
	}
	get := func(id string) (int, string, []byte) {
		resp, err := c.Do(ctx, http.MethodGet, base+"/collections/"+coll+"/documents/by-key?id="+url.QueryEscape(id), nil)
		if err != nil {
			return 0, "", nil
		}
		var doc struct {
			Version  string          `json:"version"`
			Document json.RawMessage `json:"document"`
		}
		_ = json.Unmarshal(resp.Body, &doc)
		version := doc.Version
		if resp.Status == http.StatusNotFound {
			version = resp.ETag
		}
		return resp.Status, version, doc.Document
	}
	x, y, z := DocID(r.Seed^0xfeed, 1), DocID(r.Seed^0xfeed, 2), DocID(r.Seed^0xfeed, 3)
	body := func(rev int) json.RawMessage { return Body(r.Seed, 1, rev) }

	st, v := tx(map[string]any{"op": "create", "id": x, "document": body(1)})
	v1 := first(v)
	check("create answers 200 with a tbv1 version", st == 200 && isVersion(v1), "HTTP %d version %q", st, v1)
	st, _ = tx(map[string]any{"op": "create", "id": x, "document": body(1)})
	gs, gv, _ := get(x)
	check("a retried create answers 412 and changes nothing", st == 412 && gs == 200 && gv == v1, "HTTP %d, then GET %d %q", st, gs, gv)
	st, _ = tx(map[string]any{"op": "put", "id": x, "if_match": "tbv1-" + strings.Repeat("0", 32), "document": body(2)})
	check("put with a stale if_match answers 412", st == 412, "HTTP %d", st)
	putV1 := map[string]any{"op": "put", "id": x, "if_match": v1, "document": body(2)}
	st, v = tx(putV1)
	v2 := first(v)
	check("put if_match the current version answers 200", st == 200 && isVersion(v2) && v2 != v1, "HTTP %d version %q", st, v2)
	st, _ = tx(map[string]any{"op": "put", "id": x, "if_match": v1, "document": body(2)})
	gs, gv, _ = get(x)
	check("a retried conditional put answers 412 and changes nothing", st == 412 && gv == v2, "HTTP %d, then GET %d %q", st, gs, gv)
	st, v = tx(map[string]any{"op": "delete", "id": x, "if_match": v2})
	vt := first(v)
	gs, gv, _ = get(x)
	check("conditional delete answers its tombstone version, and GET answers 404 with that version", st == 200 && isVersion(vt) && gs == 404 && gv == vt, "delete HTTP %d %q; GET %d ETag %q", st, vt, gs, gv)
	st, _ = tx(map[string]any{"op": "delete", "id": x, "if_match": v2})
	gs, gv, _ = get(x)
	check("a retried conditional delete changes nothing", (st == 404 || st == 412) && gs == 404 && gv == vt, "HTTP %d, then GET %d %q", st, gs, gv)
	st, _ = tx(map[string]any{"op": "put", "id": x, "if_match": v2, "document": body(3)})
	check("re-create conditioned on the pre-delete version answers 412", st == 412, "HTTP %d", st)
	st, v = tx(map[string]any{"op": "put", "id": x, "if_match": vt, "document": body(3)})
	v3 := first(v)
	gs, gv, gb := get(x)
	canon, _ := benchclient.Canonical(gb)
	check("re-create conditioned on the tombstone version answers 200 with the new body", st == 200 && gs == 200 && gv == v3 && bytes.Equal(canon, body(3)), "HTTP %d; GET %d %q", st, gs, gv)
	st, _ = tx(map[string]any{"op": "put", "id": x, "if_match": vt, "document": body(3)})
	check("a retried re-create answers 412", st == 412, "HTTP %d", st)
	st, _ = tx(map[string]any{"op": "create", "id": y, "document": body(1)}, map[string]any{"op": "create", "id": x, "document": body(1)})
	gs, gv, _ = get(y)
	check("a transaction with one failing precondition applies nothing", st == 412 && gs == 404 && gv == "", "HTTP %d; GET of the other op's document %d ETag %q", st, gs, gv)
	st, _ = tx(map[string]any{"op": "create", "id": z, "document": json.RawMessage(Body(r.Seed, 99, 1))})
	gs, _, gb = get(z)
	canon, _ = benchclient.Canonical(gb)
	check("a generated body round-trips to identical canonical JSON", st == 200 && gs == 200 && bytes.Equal(canon, Body(r.Seed, 99, 1)), "HTTP %d; GET %d", st, gs)
	var ids []string
	for k := int64(10); k < 20; k++ {
		id := DocID(r.Seed^0xfeed, k)
		ids = append(ids, id)
		tx(map[string]any{"op": "create", "id": id, "document": json.RawMessage(Body(r.Seed, k, 1))})
	}
	resp, err := c.Do(ctx, http.MethodPost, base+"/collections/"+coll+"/query", mustJSON(map[string]any{
		"filter": map[string]any{"and": []any{map[string]any{"gte": []any{"$id", ids[2]}}, map[string]any{"lt": []any{"$id", ids[7]}}}},
		"limit":  3,
	}))
	var got []string
	for err == nil && resp.Status == 200 {
		var page queryPage
		if json.Unmarshal(resp.Body, &page) != nil {
			break
		}
		for _, item := range page.Items {
			got = append(got, item.ID)
		}
		if page.NextCursor == nil || *page.NextCursor == "" {
			break
		}
		resp, err = c.Do(ctx, http.MethodPost, base+"/collections/"+coll+"/query", mustJSON(map[string]any{
			"filter": map[string]any{"and": []any{map[string]any{"gte": []any{"$id", ids[2]}}, map[string]any{"lt": []any{"$id", ids[7]}}}},
			"limit":  3, "cursor": *page.NextCursor,
		}))
	}
	check("$id range queries page exactly the range in id order", strings.Join(got, ",") == strings.Join(ids[2:7], ","), "got %v", got)
	steps = append(steps, fenceSteps(ctx, c, base, coll, r)...)
	steps = append(steps, backupFields(ctx, c))
	return steps, r.Evidence("probe.json", steps)
}

func first(v []string) string {
	if len(v) == 0 {
		return ""
	}
	return v[0]
}

func isVersion(v string) bool { return len(v) == 37 && strings.HasPrefix(v, "tbv1-") }

func backupFields(ctx context.Context, c *Client) ProbeStep {
	step := ProbeStep{Name: "GET /v1/backup answers the members client.go decodes"}
	resp, err := c.Do(ctx, http.MethodGet, "/v1/backup", nil)
	if err != nil || resp.Status != http.StatusOK {
		step.Detail = fmt.Sprintf("HTTP %d %v", resp.Status, err)
		return step
	}
	var status struct {
		Shipping *bool                        `json:"shipping"`
		Files    []map[string]json.RawMessage `json:"files"`
	}
	if err := json.Unmarshal(resp.Body, &status); err != nil || status.Shipping == nil || status.Files == nil {
		step.Detail = fmt.Sprintf("no shipping/files members: %s", truncate(resp.Body, 200))
		return step
	}
	if len(status.Files) == 0 {
		step.Skipped, step.Detail = true, fmt.Sprintf("shipping=%v and no files listed: per-file members cannot be checked on this node", *status.Shipping)
		return step
	}
	members := []string{"file", "generation", "logBytes", "lastShippedAt", "lagSeconds", "pendingBytes", "lastError", "shipping", "sizeBytes"}
	for _, f := range status.Files {
		for _, m := range members {
			if _, ok := f[m]; !ok {
				step.Detail = fmt.Sprintf("a file lacks %q: %s", m, truncate(resp.Body, 300))
				return step
			}
		}
	}
	step.Passed, step.Detail = true, fmt.Sprintf("%d files, each with %s", len(status.Files), strings.Join(members, ", "))
	return step
}

func fenceSteps(ctx context.Context, c *Client, base, coll string, r *Run) []ProbeStep {
	var steps []ProbeStep
	check := func(name string, ok bool, format string, args ...any) {
		steps = append(steps, ProbeStep{Name: name, Passed: ok, Detail: fmt.Sprintf(format, args...)})
	}
	if err := created(c.JSON(ctx, http.MethodPost, base+"/collections", map[string]string{"name": "fences"}, nil)); err != nil {
		check("fences collection", false, "%v", err)
		return steps
	}
	type result struct {
		ID       string `json:"id"`
		Version  string `json:"version"`
		Asserted bool   `json:"asserted"`
	}
	tx := func(ops ...map[string]any) (int, []result) {
		resp, err := c.Do(ctx, http.MethodPost, base+"/transactions", mustJSON(map[string]any{"ops": ops}))
		if err != nil {
			return 0, nil
		}
		var out struct {
			Results []result `json:"results"`
		}
		_ = json.Unmarshal(resp.Body, &out)
		return resp.Status, out.Results
	}
	get := func(id string) (int, string) {
		resp, err := c.Do(ctx, http.MethodGet, base+"/collections/"+coll+"/documents/by-key?id="+url.QueryEscape(id), nil)
		if err != nil {
			return 0, ""
		}
		return resp.Status, resp.ETag
	}
	const fid = "closed-probe"
	assert := map[string]any{"op": "assert", "collection": "fences", "id": fid, "absent": true}
	p, q := DocID(r.Seed^0xfe7ce, 1), DocID(r.Seed^0xfe7ce, 2)
	st, res := tx(map[string]any{"op": "create", "collection": coll, "id": p, "document": json.RawMessage(Body(r.Seed, 1, 1))}, assert)
	ok := st == 200 && len(res) == 2 && isVersion(res[0].Version) && res[1].Asserted && res[1].ID == "" && res[1].Version == ""
	check("create + assert fence absent applies without the fence; the assertion answers {asserted:true}", ok, "HTTP %d results %+v", st, res)
	vp := ""
	if len(res) > 0 {
		vp = res[0].Version
	}
	fence := map[string]any{"op": "create", "collection": "fences", "id": fid, "document": map[string]any{"phase": "probe"}}
	st, _ = tx(fence)
	check("the fence is a conditional create", st == 200, "HTTP %d", st)
	st, _ = tx(fence)
	check("a retried fence create answers 412", st == 412, "HTTP %d", st)
	st, _ = tx(map[string]any{"op": "create", "collection": coll, "id": q, "document": json.RawMessage(Body(r.Seed, 2, 1))}, assert)
	gs, etag := get(q)
	check("after the fence, create + assert answers 412 and applies nothing (GET 404 without ETag)", st == 412 && gs == 404 && etag == "", "HTTP %d; GET %d ETag %q", st, gs, etag)
	st, _ = tx(map[string]any{"op": "put", "collection": coll, "id": p, "if_match": vp, "document": json.RawMessage(Body(r.Seed, 1, 2))}, assert)
	gs, etag = get(p)
	check("after the fence, a put whose if_match holds + assert answers 412 and changes nothing", st == 412 && gs == 200 && etag == vp, "HTTP %d; GET %d ETag %q", st, gs, etag)
	st, _ = tx(map[string]any{"op": "delete", "collection": coll, "id": p, "if_match": vp}, assert)
	gs, etag = get(p)
	check("after the fence, a delete whose if_match holds + assert answers 412 and changes nothing", st == 412 && gs == 200 && etag == vp, "HTTP %d; GET %d ETag %q", st, gs, etag)
	return steps
}
