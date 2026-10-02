package v1test

// Contract fixture: control-plane-v1 (/v1/dashboard). See fixture_test.go.

import (
	"encoding/base64"
	"encoding/json"
	"net/http"
	"slices"
	"strconv"
	"strings"
	"time"
)

func (f *fixture) dashboardRoutes(rt *routes) {
	d := "/v1/dashboard"
	t := d + "/tenants/{tenant}"
	db := t + "/databases/{db}"
	coll := db + "/collections/{coll}"
	ix := coll + "/indexes/{index}"
	routes := map[string]fxHandler{
		"POST " + d + "/tenants":           f.createTenant,
		"GET " + d + "/tenants":            f.listTenants,
		"GET " + t:                         f.getTenant,
		"PATCH " + t:                       f.updateTenant,
		"DELETE " + t:                      f.deleteTenant,
		"GET " + t + "/usage":              f.usage,
		"POST " + t + "/databases":         f.createDatabase,
		"GET " + t + "/databases":          f.listDatabases,
		"GET " + db:                        f.getDatabase,
		"PATCH " + db:                      f.updateDatabase,
		"DELETE " + db:                     f.deleteDatabase,
		"GET " + d + "/operations/{op}":    f.getOperation,
		"POST " + db + "/collections":      f.createCollection,
		"GET " + db + "/collections":       f.listCollections,
		"GET " + coll:                      f.getCollection,
		"PATCH " + coll:                    f.updateCollection,
		"DELETE " + coll:                   f.deleteCollection,
		"GET " + coll + "/stats":           f.collectionStats,
		"GET " + coll + "/indexes":         f.listIndexes,
		"POST " + coll + "/indexes":        f.createIndex,
		"GET " + ix:                        f.getIndex,
		"PATCH " + ix:                      f.updateIndex,
		"DELETE " + ix:                     f.deleteIndex,
		"GET " + coll + "/index-decisions": f.indexDecisions,
		"POST " + t + "/keys":              f.createKey,
		"GET " + t + "/keys":               f.listKeys,
		"GET " + t + "/keys/{key}":         f.getKey,
		"DELETE " + t + "/keys/{key}":      f.revokeKey,
		"POST " + t + "/keys/{key}/rotate": f.rotateKey,
		"POST " + d + "/edge-keys":         f.createEdgeKey,
		"GET " + d + "/edge-keys":          f.listEdgeKeys,
		"DELETE " + d + "/edge-keys/{key}": f.deleteEdgeKey,
		"GET " + d + "/advisors":           f.advisors,
		"GET " + d + "/regions":            f.regions,
		"GET " + d + "/audit":              f.listAudit,
	}
	for pattern, h := range routes {
		rt.handle(pattern, f.wrap(controlRoute, h))
	}
}

// page applies ?limit and ?cursor; extra lists other accepted parameters.
func page(r *http.Request, items []any, extra ...string) fxReply {
	q := r.URL.Query()
	for name, values := range q {
		if len(values) != 1 || values[0] == "" || !slices.Contains(append([]string{"limit", "cursor"}, extra...), name) {
			return fxErr(400, "invalid_request")
		}
	}
	limit, start := 50, 0
	if v := q.Get("limit"); v != "" {
		n, err := strconv.Atoi(v)
		if err != nil || n < 1 || n > 200 {
			return fxErr(400, "invalid_request")
		}
		limit = n
	}
	if v := q.Get("cursor"); v != "" {
		raw, err := base64.RawURLEncoding.DecodeString(v)
		n, err2 := strconv.Atoi(string(raw))
		if err != nil || err2 != nil || n < 0 || n > len(items) {
			return fxErr(400, "invalid_cursor")
		}
		start = n
	}
	end := min(start+limit, len(items))
	var next any
	if end < len(items) {
		next = base64.RawURLEncoding.EncodeToString([]byte(strconv.Itoa(end)))
	}
	return fxOK(200, map[string]any{"items": append([]any{}, items[start:end]...), "next_cursor": next})
}

func (f *fixture) tenantJSON(t *fxTenant) map[string]any {
	return map[string]any{"id": t.id, "name": t.name, "external_ref": t.externalRef, "status": t.status, "limits": t.limits,
		"advisor_external": t.advisorExternal, "created_at": t.createdAt}
}

func (f *fixture) tenant(r *http.Request) (*fxTenant, *fxReply) {
	t := f.tenants[r.PathValue("tenant")]
	if t == nil {
		rep := fxErr(404, "not_found")
		return nil, &rep
	}
	return t, nil
}

func (f *fixture) createTenant(r *http.Request, _ *fxKey, body []byte) fxReply {
	var req struct {
		Name            string         `json:"name"`
		ExternalRef     string         `json:"external_ref"`
		Limits          map[string]any `json:"limits"`
		AdvisorExternal bool           `json:"advisor_external"`
	}
	if strictDecode(body, &req) != nil || req.Name == "" || req.ExternalRef == "" {
		return fxErr(400, "invalid_request")
	}
	for _, id := range f.tenantList {
		if f.tenants[id].externalRef == req.ExternalRef {
			return fxErr(409, "name_taken")
		}
	}
	t := f.newTenant(req.Name, req.ExternalRef, req.Limits)
	t.advisorExternal = req.AdvisorExternal
	return fxOK(201, f.tenantJSON(t))
}

func (f *fixture) listTenants(r *http.Request, _ *fxKey, _ []byte) fxReply {
	ref := r.URL.Query().Get("external_ref")
	var items []any
	for _, id := range f.tenantList {
		if t := f.tenants[id]; ref == "" || t.externalRef == ref {
			items = append(items, f.tenantJSON(t))
		}
	}
	return page(r, items, "external_ref")
}

func (f *fixture) getTenant(r *http.Request, _ *fxKey, _ []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	return fxOK(200, f.tenantJSON(t))
}

func (f *fixture) updateTenant(r *http.Request, _ *fxKey, body []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	var req struct {
		Name            *string        `json:"name"`
		Status          *string        `json:"status"`
		Limits          map[string]any `json:"limits"`
		AdvisorExternal *bool          `json:"advisor_external"`
	}
	if strictDecode(body, &req) != nil || (req.Status != nil && *req.Status != "active" && *req.Status != "suspended") {
		return fxErr(400, "invalid_request")
	}
	if req.Name != nil {
		t.name = *req.Name
	}
	if req.Status != nil {
		t.status = *req.Status
	}
	if req.Limits != nil {
		t.limits = req.Limits
	}
	if req.AdvisorExternal != nil {
		t.advisorExternal = *req.AdvisorExternal
	}
	return fxOK(200, f.tenantJSON(t))
}

func (f *fixture) startOperation(kind, resource string, effect func()) fxReply {
	op := &fxOperation{id: fxUUID(), kind: kind, resource: resource, createdAt: fxNow(), started: f.now(), status: "running", effect: effect}
	f.ops[op.id] = op
	return fxOK(202, f.operationJSON(op))
}

// finishIfDue completes a running operation on its second poll or once
// opDelay has passed. Server.FailOperations forces a kind to fail.
func (f *fixture) finishIfDue(op *fxOperation) {
	if op.status != "running" || (op.polls < 2 && f.now().Sub(op.started) < f.opDelay) {
		return
	}
	op.finishedAt = fxNow()
	if fail, ok := f.opFailures[op.kind]; ok {
		op.status, op.errCode, op.errMessage = "failed", fail.code, fail.message
		return
	}
	op.status = "succeeded"
	if op.effect != nil {
		op.effect()
	}
}

func (f *fixture) settleOperations() {
	for _, op := range f.ops {
		f.finishIfDue(op)
	}
}

func (f *fixture) operationJSON(op *fxOperation) map[string]any {
	out := map[string]any{"id": op.id, "kind": op.kind, "status": op.status, "resource": op.resource, "error": nil,
		"created_at": op.createdAt, "finished_at": nilIfEmpty(op.finishedAt)}
	if op.status == "failed" {
		out["error"] = map[string]any{"code": op.errCode, "message": op.errMessage}
	}
	return out
}

func (f *fixture) deleteTenant(r *http.Request, _ *fxKey, _ []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	t.status = "deleting"
	return f.startOperation("delete_tenant", r.URL.Path, func() {
		for _, k := range f.keys {
			if k.tenantID == t.id && k.revokedAt == "" {
				k.revokedAt = fxNow()
			}
		}
		delete(f.tenants, t.id)
		f.tenantList = slices.DeleteFunc(f.tenantList, func(id string) bool { return id == t.id })
	})
}

func (f *fixture) getOperation(r *http.Request, _ *fxKey, _ []byte) fxReply {
	op := f.ops[r.PathValue("op")]
	if op == nil {
		return fxErr(404, "not_found")
	}
	op.polls++
	f.finishIfDue(op)
	return fxOK(200, f.operationJSON(op))
}

func (f *fixture) usage(r *http.Request, _ *fxKey, _ []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	docs := 0
	for _, db := range t.dbs {
		for _, c := range db.colls {
			for _, d := range c.docs {
				if d.live() {
					docs++
				}
			}
		}
	}
	return fxOK(200, map[string]any{"databases": len(t.dbs), "documents": strconv.Itoa(docs), "storage_bytes": "4096",
		"reads": "0", "writes": strconv.FormatUint(f.seq, 10), "period_start": "1759276800000"})
}

func dbJSON(db *fxDB) map[string]any {
	return map[string]any{"id": db.id, "tenant_id": db.tenantID, "name": db.name, "home_region": db.homeRegion, "status": db.status,
		"durability": db.durability, "retention": db.retention, "created_at": db.createdAt}
}

func (f *fixture) database(r *http.Request) (*fxTenant, *fxDB, *fxReply) {
	t, rep := f.tenant(r)
	if rep != nil {
		return nil, nil, rep
	}
	db := t.dbs[r.PathValue("db")]
	if db == nil {
		rep := fxErr(404, "not_found")
		return nil, nil, &rep
	}
	return t, db, nil
}

func (f *fixture) createDatabase(r *http.Request, _ *fxKey, body []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	var req struct {
		Name       string         `json:"name"`
		HomeRegion string         `json:"home_region"`
		Durability string         `json:"durability"`
		Retention  map[string]any `json:"retention"`
	}
	if strictDecode(body, &req) != nil || !namePattern.MatchString(req.Name) ||
		(req.Durability != "" && req.Durability != "local_durable" && req.Durability != "object_durable") {
		return fxErr(400, "invalid_request")
	}
	if t.dbs[req.Name] != nil {
		return fxErr(409, "name_taken")
	}
	if n, _ := strconv.Atoi(string(mustRaw(t.limits["max_databases"]))); len(t.dbs) >= n {
		return fxErr(409, "limit_exceeded")
	}
	db := f.newDB(t, req.Name, cmpOr(req.HomeRegion, "gru"), req.Retention)
	if req.Durability != "" {
		db.durability = req.Durability
	}
	return fxOK(201, dbJSON(db))
}

func cmpOr(a, b string) string {
	if a != "" {
		return a
	}
	return b
}

func (f *fixture) listDatabases(r *http.Request, _ *fxKey, _ []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	var items []any
	for _, name := range t.dbOrder {
		if db := t.dbs[name]; db != nil {
			items = append(items, dbJSON(db))
		}
	}
	return page(r, items)
}

func (f *fixture) getDatabase(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, db, rep := f.database(r)
	if rep != nil {
		return *rep
	}
	return fxOK(200, dbJSON(db))
}

func (f *fixture) updateDatabase(r *http.Request, _ *fxKey, body []byte) fxReply {
	_, db, rep := f.database(r)
	if rep != nil {
		return *rep
	}
	var req struct {
		Retention map[string]any `json:"retention"`
	}
	if strictDecode(body, &req) != nil {
		return fxErr(400, "invalid_request")
	}
	if req.Retention != nil {
		db.retention = req.Retention
	}
	return fxOK(200, dbJSON(db))
}

func (f *fixture) deleteDatabase(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, db, rep := f.database(r)
	if rep != nil {
		return *rep
	}
	db.status = "deleting"
	t := f.tenants[db.tenantID]
	return f.startOperation("delete_database", r.URL.Path, func() {
		if t.dbs[db.name] == db {
			delete(t.dbs, db.name)
			t.dbOrder = slices.DeleteFunc(t.dbOrder, func(n string) bool { return n == db.name })
		}
	})
}

func collJSON(c *fxColl) map[string]any {
	unique := []any{}
	for _, u := range c.unique {
		unique = append(unique, u)
	}
	var schema any
	if c.schema != nil {
		schema = c.schema
	}
	var ttl any
	if c.ttlPath != nil {
		ttl = *c.ttlPath
	}
	return map[string]any{"id": c.id, "name": c.name, "generation": strconv.FormatUint(c.generation, 10), "schema": schema,
		"ttl_path": ttl, "unique": unique, "advisor": c.advisor, "created_at": c.createdAt}
}

// validUnique checks unique constraint definitions: a name, 1-4 pointer
// paths and an optional lower transform.
func validUnique(list []fxUnique) bool {
	names := map[string]bool{}
	for _, u := range list {
		if !namePattern.MatchString(u.Name) || names[u.Name] || len(u.Paths) < 1 || len(u.Paths) > 4 || (u.Transform != "" && u.Transform != "lower") {
			return false
		}
		names[u.Name] = true
		for _, p := range u.Paths {
			if len(splitPointer(p)) == 0 {
				return false
			}
		}
	}
	return true
}

func (f *fixture) collection(r *http.Request) (*fxDB, *fxColl, *fxReply) {
	_, db, rep := f.database(r)
	if rep != nil {
		return nil, nil, rep
	}
	c := db.colls[r.PathValue("coll")]
	if c == nil {
		rep := fxErr(404, "not_found")
		return nil, nil, &rep
	}
	return db, c, nil
}

type fxIndexKey struct {
	Path      string `json:"path"`
	Order     string `json:"order"`
	Transform string `json:"transform,omitempty"`
}

func newIndex(name string, keys []fxIndexKey, source string, pinned bool, state string) map[string]any {
	return map[string]any{"id": fxUUID(), "name": name, "keys": keys, "unique": source == "constraint", "source": source, "pinned": pinned,
		"state": state, "multikey": false, "progress": nil, "bytes": "0", "created_at": fxNow(), "last_used_at": nil}
}

func (f *fixture) createCollection(r *http.Request, _ *fxKey, body []byte) fxReply {
	_, db, rep := f.database(r)
	if rep != nil {
		return *rep
	}
	var req struct {
		Name    string          `json:"name"`
		Schema  json.RawMessage `json:"schema"`
		TTLPath *string         `json:"ttl_path"`
		Unique  []fxUnique      `json:"unique"`
		Indexes []struct {
			Name string       `json:"name"`
			Keys []fxIndexKey `json:"keys"`
		} `json:"indexes"`
		Advisor string `json:"advisor"`
	}
	if strictDecode(body, &req) != nil || !namePattern.MatchString(req.Name) || !validUnique(req.Unique) ||
		(req.TTLPath != nil && len(splitPointer(*req.TTLPath)) == 0) || (req.Advisor != "" && req.Advisor != "auto" && req.Advisor != "manual") {
		return fxErr(400, "invalid_request")
	}
	if req.Schema != nil && string(req.Schema) != "null" {
		var s struct {
			Fields []json.RawMessage `json:"fields"`
		}
		if json.Unmarshal(req.Schema, &s) != nil || len(s.Fields) == 0 {
			return fxErr(400, "invalid_schema")
		}
	} else {
		req.Schema = nil
	}
	if db.colls[req.Name] != nil {
		return fxErr(409, "name_taken")
	}
	c := f.newColl(db, req.Name, req.Schema, req.TTLPath, req.Unique)
	if req.Advisor != "" {
		c.advisor = req.Advisor
	}
	for _, u := range req.Unique {
		keys := make([]fxIndexKey, 0, len(u.Paths))
		for _, p := range u.Paths {
			keys = append(keys, fxIndexKey{Path: p, Order: "asc", Transform: u.Transform})
		}
		c.indexes = append(c.indexes, newIndex("unique_"+u.Name, keys, "constraint", true, "active"))
	}
	for _, ix := range req.Indexes {
		c.indexes = append(c.indexes, newIndex(ix.Name, ix.Keys, "manual", true, "backfilling"))
	}
	return fxOK(201, collJSON(c))
}

func (f *fixture) listCollections(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, db, rep := f.database(r)
	if rep != nil {
		return *rep
	}
	var items []any
	for _, name := range db.collOrder {
		if c := db.colls[name]; c != nil {
			items = append(items, collJSON(c))
		}
	}
	return page(r, items)
}

func (f *fixture) getCollection(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, c, rep := f.collection(r)
	if rep != nil {
		return *rep
	}
	return fxOK(200, collJSON(c))
}

func (f *fixture) updateCollection(r *http.Request, _ *fxKey, body []byte) fxReply {
	_, c, rep := f.collection(r)
	if rep != nil {
		return *rep
	}
	// Absent members are unchanged; null clears nullable members.
	var members map[string]json.RawMessage
	var req struct {
		Schema  json.RawMessage `json:"schema"`
		TTLPath *string         `json:"ttl_path"`
		Unique  *[]fxUnique     `json:"unique"`
		Advisor *string         `json:"advisor"`
	}
	if strictDecode(body, &req) != nil || json.Unmarshal(body, &members) != nil ||
		(req.Unique != nil && !validUnique(*req.Unique)) || (req.TTLPath != nil && len(splitPointer(*req.TTLPath)) == 0) ||
		(req.Advisor != nil && *req.Advisor != "auto" && *req.Advisor != "manual") {
		return fxErr(400, "invalid_request")
	}
	if req.Unique != nil {
		// New constraints are built before they are enforced; existing
		// duplicates fail the change.
		seen := map[string]bool{}
		for _, u := range *req.Unique {
			for _, d := range c.docs {
				if !f.visible(c, d) || d.raw == nil {
					continue
				}
				key, ok := uniqueKey(u, mustObject(d.raw))
				if ok && seen[u.Name+"\x00"+key] {
					return fxErr(409, "unique_violation", "constraint", u.Name)
				}
				seen[u.Name+"\x00"+key] = true
			}
		}
	}
	if raw, ok := members["schema"]; ok {
		if (string(raw) == "null") != (c.schema == nil) {
			return fxErr(409, "codec_conversion_unsupported", "message", "schemaless and enforced collections cannot convert")
		}
		if c.schema != nil {
			c.schema = req.Schema
			c.generation++
		}
	}
	if _, ok := members["ttl_path"]; ok {
		c.ttlPath = req.TTLPath
	}
	if req.Unique != nil {
		c.unique = *req.Unique
	}
	if req.Advisor != nil {
		c.advisor = *req.Advisor
	}
	return fxOK(200, collJSON(c))
}

func (f *fixture) deleteCollection(r *http.Request, _ *fxKey, _ []byte) fxReply {
	db, c, rep := f.collection(r)
	if rep != nil {
		return *rep
	}
	return f.startOperation("delete_collection", r.URL.Path, func() {
		if db.colls[c.name] == c {
			delete(db.colls, c.name)
			db.collOrder = slices.DeleteFunc(db.collOrder, func(n string) bool { return n == c.name })
		}
	})
}

func (f *fixture) collectionStats(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, c, rep := f.collection(r)
	if rep != nil {
		return *rep
	}
	n := 0
	for _, d := range c.docs {
		if d.live() {
			n++
		}
	}
	return fxOK(200, map[string]any{"document_count_estimate": strconv.Itoa(n), "bytes_estimate": "2048", "sampled_documents": strconv.Itoa(n),
		"sampled_at": fxNow(), "paths": []any{map[string]any{"path": "/email", "types": map[string]any{"utf8": 0.99, "null": 0.01},
			"missing_fraction": 0.0, "distinct_estimate": strconv.Itoa(n), "array_fraction": 0.0, "mean_value_bytes": 24}}})
}

func (f *fixture) index(r *http.Request) (*fxColl, map[string]any, *fxReply) {
	_, c, rep := f.collection(r)
	if rep != nil {
		return nil, nil, rep
	}
	for _, ix := range c.indexes {
		if ix["name"] == r.PathValue("index") {
			return c, ix, nil
		}
	}
	e := fxErr(404, "not_found")
	return nil, nil, &e
}

func (f *fixture) listIndexes(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, c, rep := f.collection(r)
	if rep != nil {
		return *rep
	}
	items := make([]any, 0, len(c.indexes))
	for _, ix := range c.indexes {
		items = append(items, ix)
	}
	return page(r, items)
}

func (f *fixture) createIndex(r *http.Request, _ *fxKey, body []byte) fxReply {
	_, c, rep := f.collection(r)
	if rep != nil {
		return *rep
	}
	var req struct {
		Name string       `json:"name"`
		Keys []fxIndexKey `json:"keys"`
	}
	if strictDecode(body, &req) != nil || req.Name == "" || len(req.Keys) < 1 || len(req.Keys) > 4 {
		return fxErr(400, "invalid_request")
	}
	ix := newIndex(req.Name, req.Keys, "manual", true, "backfilling")
	ix["progress"] = map[string]any{"scanned": "0", "total_estimate": "100"}
	c.indexes = append(c.indexes, ix)
	return fxOK(202, ix)
}

func (f *fixture) getIndex(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, ix, rep := f.index(r)
	if rep != nil {
		return *rep
	}
	return fxOK(200, ix)
}

func (f *fixture) updateIndex(r *http.Request, _ *fxKey, body []byte) fxReply {
	_, ix, rep := f.index(r)
	if rep != nil {
		return *rep
	}
	var req struct {
		Pinned bool `json:"pinned"`
	}
	if strictDecode(body, &req) != nil {
		return fxErr(400, "invalid_request")
	}
	ix["pinned"] = req.Pinned
	return fxOK(200, ix)
}

func (f *fixture) deleteIndex(r *http.Request, _ *fxKey, _ []byte) fxReply {
	c, ix, rep := f.index(r)
	if rep != nil {
		return *rep
	}
	if s := ix["source"]; s == "constraint" || s == "ttl" {
		return fxErr(409, "index_protected")
	}
	c.indexes = slices.DeleteFunc(c.indexes, func(m map[string]any) bool { return m["name"] == ix["name"] })
	ix["state"] = "dropped"
	return fxOK(200, ix)
}

func (f *fixture) indexDecisions(r *http.Request, _ *fxKey, _ []byte) fxReply {
	_, c, rep := f.collection(r)
	if rep != nil {
		return *rep
	}
	return page(r, []any{map[string]any{"collection": c.name, "generation": strconv.FormatUint(c.generation, 10), "status": "decided",
		"decider":         map[string]any{"name": "jev", "kind": "systemone", "model": "jev-latest", "usage": map[string]any{"input_tokens": 812, "output_tokens": 64}},
		"candidates":      []any{map[string]any{"id": "c01", "keys": []any{map[string]any{"path": "/org_id", "order": "asc"}}, "score": 0.82, "selected": true}},
		"fallback_reason": nil, "request_digest": strings.Repeat("ab", 32), "decided_at": fxNow()}})
}

func validGrants(grants []fxGrant) bool {
	for _, g := range grants {
		if (g.Database != "*" && !namePattern.MatchString(g.Database)) || len(g.Collections) == 0 || len(g.Permissions) == 0 {
			return false
		}
		for _, c := range g.Collections {
			if c != "*" && !namePattern.MatchString(c) {
				return false
			}
		}
		for _, p := range g.Permissions {
			if p != "read" && p != "write" && p != "shape" {
				return false
			}
		}
	}
	return true
}

func keyJSON(k *fxKey) map[string]any {
	out := map[string]any{"id": k.id, "name": k.name, "role": k.role, "created_at": k.createdAt, "last_four": k.token[len(k.token)-4:],
		"expires_at": nilIfEmpty(k.expiresAt), "revoked_at": nilIfEmpty(k.revokedAt)}
	if k.role == "data" {
		out["tenant_id"], out["grants"], out["row_filter"], out["rate"] = k.tenantID, k.grants, k.rowFilter, k.rate
	}
	return out
}

func nilIfEmpty(s string) any {
	if s == "" {
		return nil
	}
	return s
}

func (f *fixture) keysOf(tenantID, role string) []*fxKey {
	var out []*fxKey
	for _, k := range f.keys {
		if k.tenantID == tenantID && k.role == role {
			out = append(out, k)
		}
	}
	slices.SortFunc(out, func(a, b *fxKey) int { return compareStrings(a.createdAt+a.id, b.createdAt+b.id) })
	return out
}

func compareStrings(a, b string) int {
	switch {
	case a < b:
		return -1
	case a > b:
		return 1
	}
	return 0
}

func (f *fixture) keyByID(tenantID, role, id string) *fxKey {
	for _, k := range f.keysOf(tenantID, role) {
		if k.id == id {
			return k
		}
	}
	return nil
}

func (f *fixture) createKey(r *http.Request, _ *fxKey, body []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	var req struct {
		Name       string          `json:"name"`
		Grants     []fxGrant       `json:"grants"`
		RowFilter  json.RawMessage `json:"row_filter"`
		Rate       map[string]any  `json:"rate"`
		TTLSeconds int             `json:"ttl_seconds"`
	}
	if strictDecode(body, &req) != nil || req.Name == "" || len(req.Grants) == 0 || !validGrants(req.Grants) || req.TTLSeconds < 0 {
		return fxErr(400, "invalid_request")
	}
	if len(req.RowFilter) > 0 && string(req.RowFilter) != "null" {
		if _, err := parseFilter(req.RowFilter); err != nil {
			return fxErr(400, "invalid_query", "message", err.Error())
		}
	}
	if req.Rate == nil {
		req.Rate = map[string]any{"per_second": 100, "burst": 200, "max_in_flight": 16}
	}
	k := &fxKey{name: req.Name, role: "data", tenantID: t.id, grants: req.Grants, rowFilter: req.RowFilter, rate: req.Rate}
	token := f.addKey(k)
	if req.TTLSeconds > 0 {
		k.expiresAt = strconv.FormatInt(f.now().Add(time.Duration(req.TTLSeconds)*time.Second).UnixMilli(), 10)
	}
	return fxOK(201, map[string]any{"key": keyJSON(k), "token": token})
}

func (f *fixture) listKeys(r *http.Request, _ *fxKey, _ []byte) fxReply {
	t, rep := f.tenant(r)
	if rep != nil {
		return *rep
	}
	var items []any
	for _, k := range f.keysOf(t.id, "data") {
		items = append(items, keyJSON(k))
	}
	return page(r, items)
}

func (f *fixture) getKey(r *http.Request, _ *fxKey, _ []byte) fxReply {
	k := f.keyByID(r.PathValue("tenant"), "data", r.PathValue("key"))
	if k == nil {
		return fxErr(404, "not_found")
	}
	return fxOK(200, keyJSON(k))
}

func (f *fixture) revokeKey(r *http.Request, _ *fxKey, _ []byte) fxReply {
	k := f.keyByID(r.PathValue("tenant"), "data", r.PathValue("key"))
	if k == nil {
		return fxErr(404, "not_found")
	}
	k.revokedAt = fxNow()
	return fxOK(200, keyJSON(k))
}

func (f *fixture) rotateKey(r *http.Request, _ *fxKey, body []byte) fxReply {
	old := f.keyByID(r.PathValue("tenant"), "data", r.PathValue("key"))
	if old == nil {
		return fxErr(404, "not_found")
	}
	var req struct {
		GraceSeconds *int `json:"grace_seconds"`
	}
	if strictDecode(body, &req) != nil || (req.GraceSeconds != nil && (*req.GraceSeconds < 0 || *req.GraceSeconds > 86400)) {
		return fxErr(400, "invalid_request")
	}
	grace := 0
	if req.GraceSeconds != nil {
		grace = *req.GraceSeconds
	}
	old.expiresAt = strconv.FormatInt(f.now().Add(time.Duration(grace)*time.Second).UnixMilli(), 10)
	k := &fxKey{name: old.name, role: "data", tenantID: old.tenantID, grants: old.grants, rowFilter: old.rowFilter, rate: old.rate}
	token := f.addKey(k)
	return fxOK(201, map[string]any{"key": keyJSON(k), "token": token})
}

func (f *fixture) createEdgeKey(r *http.Request, _ *fxKey, body []byte) fxReply {
	var req struct {
		Name string `json:"name"`
	}
	if strictDecode(body, &req) != nil || req.Name == "" {
		return fxErr(400, "invalid_request")
	}
	k := &fxKey{name: req.Name, role: "edge"}
	token := f.addKey(k)
	return fxOK(201, map[string]any{"key": keyJSON(k), "token": token})
}

func (f *fixture) listEdgeKeys(r *http.Request, _ *fxKey, _ []byte) fxReply {
	var items []any
	for _, k := range f.keysOf("", "edge") {
		items = append(items, keyJSON(k))
	}
	return page(r, items)
}

func (f *fixture) deleteEdgeKey(r *http.Request, _ *fxKey, _ []byte) fxReply {
	k := f.keyByID("", "edge", r.PathValue("key"))
	if k == nil {
		return fxErr(404, "not_found")
	}
	k.revokedAt = fxNow()
	return fxOK(200, keyJSON(k))
}

func (f *fixture) advisors(r *http.Request, _ *fxKey, _ []byte) fxReply {
	return page(r, []any{
		map[string]any{"name": "jev", "kind": "systemone", "model": "jev-latest", "state": "healthy", "consecutive_failures": 0, "rest_until": nil, "last_latency_ms": 420},
		map[string]any{"name": "deterministic", "kind": "deterministic", "model": "", "state": "resting", "consecutive_failures": 3, "rest_until": "1759364100000", "last_latency_ms": 1},
	})
}

func (f *fixture) regions(r *http.Request, _ *fxKey, _ []byte) fxReply {
	return page(r, []any{map[string]any{"code": "gru", "name": "São Paulo", "status": "available"}})
}

func (f *fixture) listAudit(r *http.Request, _ *fxKey, _ []byte) fxReply {
	tenant := r.URL.Query().Get("tenant")
	var items []any
	for _, e := range slices.Backward(f.audit) {
		if tenant == "" || e["tenant"] == tenant {
			items = append(items, map[string]any{"id": e["id"], "at": e["at"], "actor_key_id": e["actor_key_id"], "action": e["action"],
				"resource": e["resource"], "idempotency_key": e["idempotency_key"], "outcome": e["outcome"]})
		}
	}
	return page(r, items, "tenant")
}
