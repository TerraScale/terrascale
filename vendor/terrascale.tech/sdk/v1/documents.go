package v1

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
)

// Database is a handle to one database of a project, by name. It performs no I/O.
type Database struct {
	c       *Client
	project string
	name    string
}

// Database returns a handle to the named database of a project. project is
// the project id from the Management API; the API key's organization selects
// the tenant.
func (c *Client) Database(project, name string) *Database {
	return &Database{c: c, project: project, name: name}
}

// Project returns the project id.
func (d *Database) Project() string { return d.project }

// Name returns the database name.
func (d *Database) Name() string { return d.name }

func (d *Database) path(extra ...string) ([]string, error) {
	if err := checkID(d.project); err != nil {
		return nil, err
	}
	if err := checkName("database", d.name); err != nil {
		return nil, err
	}
	return append([]string{"v1", "projects", d.project, "databases", d.name}, extra...), nil
}

// DatabaseInfo is a database as returned by Database.Create.
type DatabaseInfo struct {
	ID        string `json:"id" ulid:"required"`
	ProjectID string `json:"projectId" ulid:"required"`
	Name      string `json:"name"`
	// Created is true when the call created the database (201).
	Created bool `json:"-"`
}

// Create creates the database in its project; an existing database is
// returned unchanged (Created false).
func (d *Database) Create(ctx context.Context) (*DatabaseInfo, error) {
	p, err := d.path()
	if err != nil {
		return nil, err
	}
	var info DatabaseInfo
	resp, err := d.c.call(ctx, request{method: http.MethodPut, path: p, replaySafe: true}, nil, &info)
	if err != nil {
		return nil, err
	}
	info.Created = resp.status == http.StatusCreated
	return &info, nil
}

// CollectionInfo is a collection.
type CollectionInfo struct {
	Name      string    `json:"name"`
	CreatedAt Timestamp `json:"created_at"`
}

// CollectionPage is one page of collections in name order.
type CollectionPage struct {
	Items []CollectionInfo `json:"items"`
	// NextCursor is empty on the last page.
	NextCursor string `json:"next_cursor"`
}

// ListOptions pages a list: Limit (zero uses the server default) and the
// previous page's NextCursor.
type ListOptions struct {
	Limit  int
	Cursor string
}

func (o ListOptions) query() url.Values {
	q := url.Values{}
	if o.Limit > 0 {
		q.Set("limit", strconv.Itoa(o.Limit))
	}
	if o.Cursor != "" {
		q.Set("cursor", o.Cursor)
	}
	return q
}

// CreateCollection creates an empty schemaless collection; an existing name
// fails with ErrNameTaken.
func (d *Database) CreateCollection(ctx context.Context, name string) (*CollectionInfo, error) {
	if err := checkName("collection", name); err != nil {
		return nil, err
	}
	p, err := d.path("collections")
	if err != nil {
		return nil, err
	}
	var info CollectionInfo
	if _, err := d.c.call(ctx, request{method: http.MethodPost, path: p}, map[string]string{"name": name}, &info); err != nil {
		return nil, err
	}
	return &info, nil
}

// Collections lists one page of collections (Limit 1-200, default 50).
func (d *Database) Collections(ctx context.Context, opts ListOptions) (*CollectionPage, error) {
	p, err := d.path("collections")
	if err != nil {
		return nil, err
	}
	var page CollectionPage
	if _, err := d.c.call(ctx, request{method: http.MethodGet, path: p, query: opts.query(), replaySafe: true}, nil, &page); err != nil {
		return nil, err
	}
	return &page, nil
}

// Collection is a handle to one collection by name. It performs no I/O.
type Collection struct {
	db   *Database
	name string
}

// Collection returns a handle to the named collection.
func (d *Database) Collection(name string) *Collection { return &Collection{db: d, name: name} }

// Name returns the collection name.
func (col *Collection) Name() string { return col.name }

func (col *Collection) path(extra ...string) ([]string, error) {
	if err := checkName("collection", col.name); err != nil {
		return nil, err
	}
	return col.db.path(append([]string{"collections", col.name}, extra...)...)
}

func (col *Collection) docPath(id string) ([]string, error) {
	if err := checkDocumentKey(id); err != nil {
		return nil, err
	}
	return col.path("documents", id)
}

// Info reads the collection; a missing one fails with ErrUnknownCollection.
func (col *Collection) Info(ctx context.Context) (*CollectionInfo, error) {
	return col.collectionCall(ctx, http.MethodGet)
}

// Drop deletes the collection and every document in it, and returns the
// dropped collection.
func (col *Collection) Drop(ctx context.Context) (*CollectionInfo, error) {
	return col.collectionCall(ctx, http.MethodDelete)
}

func (col *Collection) collectionCall(ctx context.Context, method string) (*CollectionInfo, error) {
	p, err := col.path()
	if err != nil {
		return nil, err
	}
	var info CollectionInfo
	if _, err := col.db.c.call(ctx, request{method: method, path: p, replaySafe: method == http.MethodGet}, nil, &info); err != nil {
		return nil, err
	}
	return &info, nil
}

// List reads one page of documents in id order (Limit 1-1000, default 50). A
// page also stops at 8 MiB of JSON.
func (col *Collection) List(ctx context.Context, opts ListOptions) (*QueryResult, error) {
	p, err := col.path("documents")
	if err != nil {
		return nil, err
	}
	return col.db.c.page(ctx, request{method: http.MethodGet, path: p, query: opts.query(), replaySafe: true}, nil)
}

// WriteOption configures a document write: a Precondition.
type WriteOption interface{ applyWrite(*writeConfig) }

type writeConfig struct {
	ifMatch        string
	ifNoneMatchAny bool
}

// Precondition is a conditional-write precondition. It applies to document
// writes and to individual transaction ops.
type Precondition struct {
	ifMatch        string
	ifNoneMatchAny bool
}

// IfMatch requires the document's current version to equal version.
func IfMatch(version string) Precondition { return Precondition{ifMatch: version} }

// IfNoneMatchAny requires the document to be absent or deleted (If-None-Match: *).
func IfNoneMatchAny() Precondition { return Precondition{ifNoneMatchAny: true} }

func (p Precondition) applyWrite(w *writeConfig) {
	if p.ifMatch != "" {
		w.ifMatch = p.ifMatch
	}
	w.ifNoneMatchAny = w.ifNoneMatchAny || p.ifNoneMatchAny
}

func resolveWrite(opts []WriteOption) (writeConfig, error) {
	var w writeConfig
	for _, o := range opts {
		o.applyWrite(&w)
	}
	if w.ifMatch != "" && w.ifNoneMatchAny {
		return w, errors.New("terrabase: IfMatch and IfNoneMatchAny are mutually exclusive")
	}
	if w.ifMatch != "" {
		if err := checkVersion(w.ifMatch); err != nil {
			return w, err
		}
	}
	return w, nil
}

func (w writeConfig) header() http.Header {
	h := http.Header{}
	if w.ifMatch != "" {
		h.Set("If-Match", `"`+w.ifMatch+`"`)
	}
	if w.ifNoneMatchAny {
		h.Set("If-None-Match", "*")
	}
	return h
}

// ReadOption configures Get.
type ReadOption func(*readConfig)

type readConfig struct{ ifNoneMatch string }

// IfNoneMatch makes Get return ErrNotModified when the document's version is
// still version.
func IfNoneMatch(version string) ReadOption {
	return func(r *readConfig) { r.ifNoneMatch = version }
}

// Get reads a document. Absent, deleted and expired documents return
// ErrNotFound; for a deleted document the *APIError's ETag holds the
// tombstone version for IfMatch.
func (col *Collection) Get(ctx context.Context, id string, opts ...ReadOption) (*Document, error) {
	p, err := col.docPath(id)
	if err != nil {
		return nil, err
	}
	var rc readConfig
	for _, o := range opts {
		o(&rc)
	}
	req := request{method: http.MethodGet, path: p, replaySafe: true}
	if rc.ifNoneMatch != "" {
		if err := checkVersion(rc.ifNoneMatch); err != nil {
			return nil, err
		}
		req.header = http.Header{"If-None-Match": {`"` + rc.ifNoneMatch + `"`}}
	}
	var doc Document
	if _, err := col.db.c.call(ctx, req, nil, &doc); err != nil {
		return nil, err
	}
	return &doc, nil
}

// Put writes the complete document. Use IfMatch or IfNoneMatchAny for
// conditional writes; only IfMatch writes are retried.
// Created reports a 201 (the document did not exist).
func (col *Collection) Put(ctx context.Context, id string, doc any, opts ...WriteOption) (*WriteResult, error) {
	p, err := col.docPath(id)
	if err != nil {
		return nil, err
	}
	w, err := resolveWrite(opts)
	if err != nil {
		return nil, err
	}
	body, err := encodeObject(doc)
	if err != nil {
		return nil, err
	}
	return col.write(ctx, request{method: http.MethodPut, path: p, body: body, header: w.header(), replaySafe: w.ifMatch != ""})
}

// Create stores a new document under a server-generated ULID. It is never
// retried, since a replay would create a second document.
func (col *Collection) Create(ctx context.Context, doc any, opts ...WriteOption) (*WriteResult, error) {
	p, err := col.path("documents")
	if err != nil {
		return nil, err
	}
	w, err := resolveWrite(opts)
	if err != nil {
		return nil, err
	}
	if w.ifMatch != "" || w.ifNoneMatchAny {
		return nil, errors.New("terrabase: Create does not take preconditions")
	}
	body, err := encodeObject(doc)
	if err != nil {
		return nil, err
	}
	res, err := col.write(ctx, request{method: http.MethodPost, path: p, body: body})
	if err == nil {
		err = ValidateID(res.ID)
	}
	if err != nil {
		return nil, err
	}
	return res, nil
}

// Patch applies a MergePatch or an Ops patch to an existing document.
func (col *Collection) Patch(ctx context.Context, id string, patch Patch, opts ...WriteOption) (*WriteResult, error) {
	p, err := col.docPath(id)
	if err != nil {
		return nil, err
	}
	w, err := resolveWrite(opts)
	if err != nil {
		return nil, err
	}
	if w.ifNoneMatchAny {
		return nil, errors.New("terrabase: Patch does not take IfNoneMatchAny")
	}
	body, contentType, err := patch.encode()
	if err != nil {
		return nil, err
	}
	return col.write(ctx, request{method: http.MethodPatch, path: p, body: body, contentType: contentType, header: w.header(), replaySafe: w.ifMatch != ""})
}

// Delete tombstones a document. The result's Version is the tombstone's.
func (col *Collection) Delete(ctx context.Context, id string, opts ...WriteOption) (*WriteResult, error) {
	p, err := col.docPath(id)
	if err != nil {
		return nil, err
	}
	w, err := resolveWrite(opts)
	if err != nil {
		return nil, err
	}
	if w.ifNoneMatchAny {
		return nil, errors.New("terrabase: Delete does not take IfNoneMatchAny")
	}
	return col.write(ctx, request{method: http.MethodDelete, path: p, header: w.header(), replaySafe: w.ifMatch != ""})
}

func (col *Collection) write(ctx context.Context, req request) (*WriteResult, error) {
	var res WriteResult
	resp, err := col.db.c.call(ctx, req, nil, &res)
	if err != nil {
		return nil, err
	}
	res.Created = resp.status == http.StatusCreated
	res.Location = resp.header.Get("Location")
	return &res, nil
}

// encodeObject marshals doc and requires a JSON object. json.RawMessage and
// json.Number values pass through unchanged.
func encodeObject(doc any) ([]byte, error) {
	body, err := json.Marshal(doc)
	if err != nil {
		return nil, fmt.Errorf("terrabase: encode document: %w", err)
	}
	if t := bytes.TrimLeft(body, " \t\r\n"); len(t) == 0 || t[0] != '{' {
		return nil, errors.New("terrabase: a document must be a JSON object")
	}
	return body, nil
}

// Patch is a document patch: MergePatch or Ops.
type Patch struct {
	merge json.RawMessage
	ops   []PatchOp
	err   error
}

// MergePatch is an RFC 7396 merge patch; nil members remove fields.
func MergePatch(v any) Patch {
	body, err := encodeObject(v)
	return Patch{merge: body, err: err}
}

// Ops is an ordered list of patch operations (at most 256).
func Ops(ops ...PatchOp) Patch {
	p := Patch{ops: ops}
	if len(ops) == 0 {
		p.err = errors.New("terrabase: Ops needs at least one operation")
	}
	for _, op := range ops {
		p.err = errors.Join(p.err, op.err)
	}
	return p
}

func (p Patch) encode() ([]byte, string, error) {
	switch {
	case p.err != nil:
		return nil, "", p.err
	case p.merge != nil:
		return p.merge, "application/merge-patch+json", nil
	case p.ops != nil:
		body, err := json.Marshal(struct {
			Ops []PatchOp `json:"ops"`
		}{p.ops})
		return body, "application/vnd.terrabase.ops+json", err
	default:
		return nil, "", errors.New("terrabase: empty Patch; use MergePatch or Ops")
	}
}

// PatchOp is one operation of an Ops patch. Paths are RFC 6901 JSON Pointers
// without array indices.
type PatchOp struct {
	op    string
	path  string
	value json.RawMessage
	by    json.RawMessage
	err   error
}

// Number is an exact integer or a json.Number decimal accepted by Inc.
type Number interface {
	~int | ~int8 | ~int16 | ~int32 | ~int64 | ~uint | ~uint8 | ~uint16 | ~uint32 | ~uint64 | json.Number
}

// Set sets the member at path, creating missing parent objects.
func Set(path string, value any) PatchOp { return valueOp("set", path, value) }

// Unset removes the member at path.
func Unset(path string) PatchOp { return PatchOp{op: "unset", path: path} }

// Append appends value to the array at path, creating it when missing.
func Append(path string, value any) PatchOp { return valueOp("append", path, value) }

// Inc adds by to the number at path (missing counts as 0); arithmetic is exact.
func Inc[N Number](path string, by N) PatchOp {
	raw, err := json.Marshal(by)
	if err != nil {
		err = fmt.Errorf("terrabase: inc %s: %w", path, err)
	}
	return PatchOp{op: "inc", path: path, by: raw, err: err}
}

func valueOp(op, path string, value any) PatchOp {
	raw, err := json.Marshal(value)
	if err != nil {
		err = fmt.Errorf("terrabase: %s %s: %w", op, path, err)
	}
	return PatchOp{op: op, path: path, value: raw, err: err}
}

// MarshalJSON encodes the op in the application/vnd.terrabase.ops+json form.
func (o PatchOp) MarshalJSON() ([]byte, error) {
	if o.err != nil {
		return nil, o.err
	}
	return json.Marshal(struct {
		Op    string          `json:"op"`
		Path  string          `json:"path"`
		Value json.RawMessage `json:"value,omitempty"`
		By    json.RawMessage `json:"by,omitempty"`
	}{o.op, o.path, o.value, o.by})
}
