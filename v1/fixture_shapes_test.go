package v1_test

// Contract fixture: shapes-v1 gatekeeper, shape tokens (EdDSA JWS), log
// reads with 307/snapshot/chunks/live, must_refetch, SSE and an edge proxy
// that verifies tokens and forwards with an edge key. See fixture_test.go.

import (
	"bytes"
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"time"
)

type fxShape struct {
	id, tenantID string
	db           *fxDB
	coll         string
	filter       *fxFilter
	project      []string
	handle       string
	floor        uint64
}

func (f *fixture) gatekeeper(r *http.Request, k *fxKey, body []byte) fxReply {
	var req struct {
		Collection string          `json:"collection"`
		Filter     json.RawMessage `json:"filter"`
		Project    []string        `json:"project"`
	}
	if err := strictDecode(body, &req); err != nil {
		return fxErr(400, "invalid_query", "message", err.Error())
	}
	db, coll, rep := f.scope(k, r.PathValue("db"), req.Collection, "shape")
	if rep != nil {
		return *rep
	}
	filter, err := parseFilter(req.Filter)
	if err != nil {
		return fxErr(400, "invalid_query", "message", err.Error())
	}
	h := sha256.New()
	fmt.Fprintf(h, "TB-SHAPE-V1\x00%s\x00%s\x00%s\x00%s\x00%s\x00%d", k.tenantID, db.id, coll.id, req.Filter, strings.Join(req.Project, ","), coll.generation)
	id := hex.EncodeToString(h.Sum(nil)[:16])
	shape := f.shapes[id]
	if shape == nil {
		shape = &fxShape{id: id, tenantID: k.tenantID, db: db, coll: coll.name, filter: filter, project: req.Project, handle: "h1-" + randB64(6)}
		f.shapes[id] = shape
	}
	now := time.Now()
	expires := now.Add(f.tokenTTL)
	token := f.signToken(map[string]any{"iss": "terrabase", "aud": "terrabase-edge", "tid": k.tenantID, "db": db.id, "shp": id,
		"iat": now.Unix(), "exp": (expires.UnixMilli() + 999) / 1000})
	return fxOK(201, map[string]any{"shape_id": id, "handle": shape.handle, "offset": "-1",
		"url": f.edge.URL + "/v1/shapes/" + id + "?offset=-1", "token": token, "expires_at": strconv.FormatInt(expires.UnixMilli(), 10)})
}

func (f *fixture) signToken(claims map[string]any) string {
	enc := base64.RawURLEncoding
	signing := enc.EncodeToString([]byte(`{"alg":"EdDSA","typ":"JWT","kid":"k1"}`)) + "." + enc.EncodeToString(mustRaw(claims))
	return signing + "." + enc.EncodeToString(ed25519.Sign(f.signer, []byte(signing)))
}

// verifyToken checks a shape token's signature, audience, expiry and shape.
func (f *fixture) verifyToken(token, shapeID string) bool {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return false
	}
	enc := base64.RawURLEncoding
	sig, err1 := enc.DecodeString(parts[2])
	payload, err2 := enc.DecodeString(parts[1])
	if err1 != nil || err2 != nil || !ed25519.Verify(f.signer.Public().(ed25519.PublicKey), []byte(parts[0]+"."+parts[1]), sig) {
		return false
	}
	var c struct {
		Aud string `json:"aud"`
		Exp int64  `json:"exp"`
		Shp string `json:"shp"`
	}
	return json.Unmarshal(payload, &c) == nil && c.Aud == "terrabase-edge" && c.Shp == shapeID && time.Now().UnixMilli() <= c.Exp*1000
}

// rotateHandle simulates a new lineage (generation change / retention).
func (f *fixture) rotateHandle(shapeID string) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.shapes[shapeID].handle = "h1-" + randB64(6)
	close(f.commitCh)
	f.commitCh = make(chan struct{})
}

// shapeQuery parses the shape read parameters strictly.
func shapeQuery(raw string, allowed ...string) (map[string]string, bool) {
	out := map[string]string{}
	if raw == "" {
		return out, true
	}
	for _, part := range strings.Split(raw, "&") {
		name, value, _ := strings.Cut(part, "=")
		v, err := url.QueryUnescape(value)
		if err != nil || v == "" || !slices.Contains(allowed, name) {
			return nil, false
		}
		if _, dup := out[name]; dup {
			return nil, false
		}
		out[name] = v
	}
	return out, true
}

// shapeAuth authorizes a shape read: a data key with shape on the
// collection, or (edge) an edge key forwarding a valid TB-Shape-Token.
func (f *fixture) shapeAuth(r *http.Request, shapeID string, allowEdge bool) (*fxShape, *fxReply) {
	k := f.keys[strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")]
	shape := f.shapes[shapeID]
	unauthorized := fxErr(401, "invalid_key")
	forbidden := fxErr(403, "forbidden_scope")
	switch {
	case k == nil || k.revokedAt != "":
		return nil, &unauthorized
	case shape == nil:
		rep := fxErr(404, "not_found")
		return nil, &rep
	case k.role == "edge" && allowEdge:
		if !f.verifyToken(r.Header.Get("TB-Shape-Token"), shapeID) {
			rep := fxErr(401, "invalid_token")
			return nil, &rep
		}
		return shape, nil
	case k.role == "data" && k.tenantID == shape.tenantID:
		if _, _, rep := f.scope(k, shape.db.name, shape.coll, "shape"); rep != nil {
			return nil, rep
		}
		return shape, nil
	}
	return nil, &forbidden
}

func (f *fixture) matches(shape *fxShape, id string, d *fxDoc) bool {
	if !d.live() {
		return false
	}
	if d.siblings == nil {
		return shape.filter.match(id, mustObject(d.raw))
	}
	return slices.ContainsFunc(d.siblings, func(s fxSibling) bool { return !s.deleted && shape.filter.match(id, mustObject(s.raw)) })
}

func (f *fixture) upsert(shape *fxShape, id string, d *fxDoc) map[string]any {
	item := docJSON(id, d)
	if d.siblings == nil {
		delete(item, "state")
		if len(shape.project) > 0 {
			item["document"] = project(mustObject(d.raw), shape.project)
		}
	}
	item["op"] = "upsert"
	return item
}

// snapshotAt returns the documents of the shape's collection as of offset s.
func (f *fixture) snapshotAt(shape *fxShape, s uint64) map[string]*fxDoc {
	state := map[string]*fxDoc{}
	for _, c := range shape.db.log {
		if c.offset > s {
			break
		}
		for _, ch := range c.changes {
			if ch.coll == shape.coll {
				state[ch.id] = ch.after
			}
		}
	}
	return state
}

type fxShapeBody map[string]any

func (f *fixture) shapeBody(shape *fxShape, offset, next string, changes []any) fxShapeBody {
	return fxShapeBody{"shape_id": shape.id, "handle": shape.handle, "offset": offset, "next_offset": next,
		"up_to_date": next == strconv.FormatUint(shape.db.head, 10), "changes": changes}
}

// snapshotPage builds page p of the snapshot at s.
func (f *fixture) snapshotPage(shape *fxShape, s uint64, p int) fxShapeBody {
	state := f.snapshotAt(shape, s)
	ids := make([]string, 0, len(state))
	for id, d := range state {
		if f.matches(shape, id, d) {
			ids = append(ids, id)
		}
	}
	slices.Sort(ids)
	lo, hi := min(p*f.snapPage, len(ids)), min((p+1)*f.snapPage, len(ids))
	next := fmt.Sprintf("snap.%d.%d", s, p+1)
	if hi >= len(ids) {
		next = strconv.FormatUint(s, 10)
	}
	changes := []any{}
	if hi > lo {
		ops := make([]any, 0, hi-lo)
		for _, id := range ids[lo:hi] {
			ops = append(ops, f.upsert(shape, id, state[id]))
		}
		changes = append(changes, map[string]any{"offset": strconv.FormatUint(s, 10), "ops": ops})
	}
	return f.shapeBody(shape, fmt.Sprintf("snap.%d.%d", s, p), next, changes)
}

// logChunk builds the chunk after offset o and reports whether it is closed.
func (f *fixture) logChunk(shape *fxShape, o uint64) (fxShapeBody, bool) {
	end := min(o+f.maxChunk, shape.db.head)
	changes := []any{}
	for _, c := range shape.db.log {
		if c.offset <= o || c.offset > end {
			continue
		}
		var ops []any
		for _, ch := range c.changes {
			if ch.coll != shape.coll {
				continue
			}
			switch {
			case f.matches(shape, ch.id, ch.after):
				ops = append(ops, f.upsert(shape, ch.id, ch.after))
			case f.matches(shape, ch.id, ch.before):
				ops = append(ops, map[string]any{"op": "delete", "id": ch.id})
			}
		}
		if len(ops) > 0 {
			changes = append(changes, map[string]any{"offset": strconv.FormatUint(c.offset, 10), "ops": ops})
		}
	}
	return f.shapeBody(shape, strconv.FormatUint(o, 10), strconv.FormatUint(end, 10), changes), end == o+f.maxChunk
}

func parseSnap(offset string) (uint64, int, bool) {
	parts := strings.Split(offset, ".")
	if len(parts) != 3 || parts[0] != "snap" {
		return 0, 0, false
	}
	s, err1 := strconv.ParseUint(parts[1], 10, 64)
	p, err2 := strconv.Atoi(parts[2])
	return s, p, err1 == nil && err2 == nil && p >= 0
}

func writeShape(w http.ResponseWriter, shape *fxShape, body fxShapeBody, cache string, cursor string) {
	h := w.Header()
	h.Set("Cache-Control", cache)
	h.Set("TB-Shape-Handle", shape.handle)
	h.Set("TB-Shape-Next-Offset", body["next_offset"].(string))
	h.Set("TB-Shape-Up-To-Date", strconv.FormatBool(body["up_to_date"].(bool)))
	h.Set("ETag", fmt.Sprintf(`"%s:%s:%s:%s"`, shape.id, shape.handle, body["offset"], body["next_offset"]))
	if cursor != "" {
		h.Set("TB-Shape-Cursor", cursor)
	}
	writeReply(w, fxOK(200, body))
}

func (f *fixture) readShape(w http.ResponseWriter, r *http.Request) {
	shapeID := r.PathValue("shape")
	f.mu.Lock()
	f.requests["GET /v1/shapes"]++
	f.shapeLog = append(f.shapeLog, r.URL.RawQuery)
	if fault := f.takeFault(r, false); fault != nil {
		f.mu.Unlock()
		writeReply(w, faultReply(fault))
		return
	}
	q, ok := shapeQuery(r.URL.RawQuery, "handle", "offset", "live", "cursor")
	shape, rep := f.shapeAuth(r, shapeID, true)
	if rep == nil && !ok {
		e := fxErr(400, "invalid_request")
		rep = &e
	}
	if rep != nil {
		f.mu.Unlock()
		writeReply(w, *rep)
		return
	}
	offset, handle := q["offset"], q["handle"]
	if offset == "-1" {
		loc := fmt.Sprintf("?handle=%s&offset=snap.%d.0", url.QueryEscape(shape.handle), shape.db.head)
		f.mu.Unlock()
		w.Header().Set("Cache-Control", "public, max-age=0, s-maxage=5")
		w.Header().Set("Location", loc)
		w.WriteHeader(http.StatusTemporaryRedirect)
		return
	}
	if handle == "" || offset == "" || (q["cursor"] != "" && q["live"] != "true") {
		f.mu.Unlock()
		writeReply(w, fxErr(400, "invalid_request"))
		return
	}
	if handle != shape.handle {
		h := shape.handle
		f.mu.Unlock()
		writeReply(w, fxErr(409, "must_refetch", "handle", h))
		return
	}
	if s, p, isSnap := parseSnap(offset); isSnap {
		if q["live"] != "" || s > shape.db.head {
			f.mu.Unlock()
			writeReply(w, fxErr(400, "invalid_request"))
			return
		}
		body := f.snapshotPage(shape, s, p)
		f.mu.Unlock()
		writeShape(w, shape, body, "public, max-age=60, s-maxage=31536000, immutable", "")
		return
	}
	o, err := strconv.ParseUint(offset, 10, 64)
	if err != nil || o > shape.db.head || (q["live"] != "" && q["live"] != "true") {
		f.mu.Unlock()
		writeReply(w, fxErr(400, "invalid_request"))
		return
	}
	if o < shape.floor {
		h := shape.handle
		f.mu.Unlock()
		writeReply(w, fxErr(409, "must_refetch", "handle", h))
		return
	}
	live := q["live"] == "true"
	if live && shape.db.head <= o {
		ch := f.commitCh
		f.mu.Unlock()
		f.waitCommit(r.Context(), ch, f.liveWait)
		f.mu.Lock()
	}
	body, closed := f.logChunk(shape, o)
	f.mu.Unlock()
	switch {
	case live:
		bucket := time.Now().Unix() / 20
		left := (bucket+1)*20 - time.Now().Unix()
		writeShape(w, shape, body, fmt.Sprintf("public, max-age=0, s-maxage=%d", left), strconv.FormatInt(bucket, 10))
	case closed:
		writeShape(w, shape, body, "public, max-age=60, s-maxage=31536000, immutable", "")
	default:
		writeShape(w, shape, body, "public, max-age=0, s-maxage=1", "")
	}
}

func (f *fixture) streamShape(w http.ResponseWriter, r *http.Request) {
	shapeID := r.PathValue("shape")
	f.mu.Lock()
	f.requests["GET /v1/shapes/sse"]++
	if fault := f.takeFault(r, false); fault != nil {
		f.mu.Unlock()
		writeReply(w, faultReply(fault))
		return
	}
	q, ok := shapeQuery(r.URL.RawQuery, "handle", "offset")
	shape, rep := f.shapeAuth(r, shapeID, false)
	if rep == nil && !ok {
		e := fxErr(400, "invalid_request")
		rep = &e
	}
	if rep == nil && q["offset"] != "-1" && q["handle"] != shape.handle {
		e := fxErr(409, "must_refetch", "handle", shape.handle)
		rep = &e
	}
	if rep != nil {
		f.mu.Unlock()
		writeReply(w, *rep)
		return
	}
	handle := shape.handle
	offset := q["offset"]
	if id := r.Header.Get("Last-Event-ID"); id != "" {
		offset = id
	}
	if offset == "-1" {
		offset = fmt.Sprintf("snap.%d.0", shape.db.head)
	}
	f.mu.Unlock()

	w.Header().Set("Content-Type", "text/event-stream")
	w.Header().Set("Cache-Control", "no-store")
	w.WriteHeader(http.StatusOK)
	flusher := w.(http.Flusher)
	send := func(event, id string, data any) {
		if id != "" {
			fmt.Fprintf(w, "id: %s\n", id)
		}
		fmt.Fprintf(w, "event: %s\ndata: %s\n\n", event, mustRaw(data))
		flusher.Flush()
	}
	fmt.Fprint(w, ": connected\n\n")
	flusher.Flush()
	lastUpToDate := ""
	for r.Context().Err() == nil {
		f.mu.Lock()
		if shape.handle != handle {
			h := shape.handle
			f.mu.Unlock()
			send("must-refetch", "", map[string]any{"error": map[string]any{"code": "must_refetch", "handle": h}})
			return
		}
		var body fxShapeBody
		if s, p, isSnap := parseSnap(offset); isSnap {
			body = f.snapshotPage(shape, s, p)
		} else {
			o, err := strconv.ParseUint(offset, 10, 64)
			if err != nil || o > shape.db.head {
				f.mu.Unlock()
				return
			}
			body, _ = f.logChunk(shape, o)
		}
		next := body["next_offset"].(string)
		upToDate := body["up_to_date"].(bool)
		ch := f.commitCh
		f.mu.Unlock()
		if len(body["changes"].([]any)) > 0 || strings.HasPrefix(offset, "snap.") {
			send("changes", next, body)
		}
		offset = next
		if !upToDate {
			continue
		}
		if lastUpToDate != next {
			body["changes"] = []any{}
			body["offset"] = next
			send("up-to-date", "", body)
			lastUpToDate = next
		}
		t := time.NewTimer(f.sseBeat)
		select {
		case <-ch:
		case <-t.C:
			fmt.Fprint(w, ": keep-alive\n\n")
			flusher.Flush()
		case <-r.Context().Done():
		}
		t.Stop()
	}
}

// serveEdge is the edge Worker: it verifies the shape token, canonicalizes
// the query and forwards to the origin with an edge key.
func (f *fixture) serveEdge(w http.ResponseWriter, r *http.Request) {
	f.mu.Lock()
	f.requests["EDGE "+r.Method]++
	f.mu.Unlock()
	shapeID, ok := strings.CutPrefix(r.URL.Path, "/v1/shapes/")
	if r.Method == http.MethodOptions {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.WriteHeader(http.StatusNoContent)
		return
	}
	if !ok || r.Method != http.MethodGet || strings.Contains(shapeID, "/") {
		writeReply(w, fxErr(404, "unknown_route"))
		return
	}
	token := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
	if !f.verifyToken(token, shapeID) {
		f.mu.Lock()
		f.requests["EDGE 401"]++
		f.mu.Unlock()
		writeReply(w, fxErr(401, "invalid_token"))
		return
	}
	q, ok := shapeQuery(r.URL.RawQuery, "handle", "offset", "live", "cursor")
	if !ok {
		writeReply(w, fxErr(400, "invalid_request"))
		return
	}
	var canon []string
	for _, name := range []string{"handle", "offset", "live", "cursor"} {
		if v, ok := q[name]; ok {
			canon = append(canon, name+"="+url.QueryEscape(v))
		}
	}
	req, _ := http.NewRequestWithContext(r.Context(), http.MethodGet, f.origin.URL+"/v1/shapes/"+shapeID+"?"+strings.Join(canon, "&"), nil)
	req.Header.Set("Authorization", "Bearer "+f.edgeToken)
	req.Header.Set("TB-Shape-Token", token)
	hc := f.origin.Client()
	hc.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	res, err := hc.Do(req)
	if err != nil {
		writeReply(w, fxErr(502, "upstream_unavailable"))
		return
	}
	defer res.Body.Close()
	data, _ := io.ReadAll(res.Body)
	for k, v := range res.Header {
		w.Header()[k] = v
	}
	w.WriteHeader(res.StatusCode)
	io.Copy(w, bytes.NewReader(data))
}
