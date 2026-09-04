// Document CRUD client (GOAL D-13).
//
// Documents are served by the public TerraBase API as canonical JSON bytes.
// A point read is the JSON value itself (not an envelope), and PUT/PATCH
// requests carry the value itself (not {"body": ...}).
package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"
	"strconv"
	"strings"
)

const (
	// MaxDocumentBytes is the public maximum encoded size of one document.
	MaxDocumentBytes = 1 << 20
	// MaxBatchOperations is the maximum number of operations in one atomic
	// document batch.
	MaxBatchOperations = 1000
	// DefaultDocumentPageSize is the server's default list page size.
	DefaultDocumentPageSize = 100
	// MaxDocumentPageSize is the largest list page accepted by the API.
	MaxDocumentPageSize = 1000
)

// Document is a point-read document. Point reads return Body as the raw
// canonical JSON value; metadata is supplied by response headers and write
// responses. Collection and ID are populated for SDK-created values and
// reactive snapshots.
type Document struct {
	ID         string `json:"id,omitempty"`
	Collection string `json:"collection,omitempty"`
	// body remains part of the reactive compatibility envelope; point reads
	// decode directly into this field without an envelope.
	Body     json.RawMessage `json:"body,omitempty"`
	Revision string          `json:"revision,omitempty"`
	ETag     string          `json:"-"`
	Session  string          `json:"-"`
}

// DocumentRow is one row returned by the collection list endpoint. The wire
// shape is exactly {"id", "version", "value"}; value remains opaque JSON.
type DocumentRow struct {
	ID      string          `json:"id"`
	Version int64           `json:"version"`
	Value   json.RawMessage `json:"value"`
}

// DocumentWriteResponse is the response to PUT, PATCH, and batch writes.
// The response body contains only the new revision. ETag and session metadata
// are carried by response headers and handled by the shared client.
type DocumentWriteResponse struct {
	Revision string `json:"revision"`
	ETag     string `json:"-"`
	Session  string `json:"-"`
}

// BatchResponse reports one atomically committed document batch.
type BatchResponse struct {
	Committed uint64 `json:"committed"`
	Count     int    `json:"count"`
	Session   string `json:"-"`
}

// BatchOperation is one operation in an atomic document batch. Put and patch
// operations require Value; delete operations omit it.
type BatchOperation struct {
	Op    string `json:"op"`
	ID    string `json:"id"`
	Value any    `json:"value,omitempty"`
}

// HistoryEntry is one retained document revision. The public history
// contract exposes revision and deletion state.
type HistoryEntry struct {
	Revision string `json:"revision"`
	Deleted  bool   `json:"deleted"`
}

// ListDocumentsResult is one bounded page. The API has no opaque cursor: use
// the last returned ID as after for the next request.
type ListDocumentsResult struct {
	Documents []DocumentRow `json:"documents"`
}

// DocumentsResponse is the generated contract name for ListDocumentsResult.
type DocumentsResponse = ListDocumentsResult

// DocumentsClient performs document CRUD. Reach it via Client.Documents.
type DocumentsClient struct {
	c *Client
}

func documentBase(databaseID, collection string) (string, error) {
	if strings.TrimSpace(databaseID) == "" {
		return "", fmt.Errorf("sdk: database id cannot be empty")
	}
	if strings.TrimSpace(collection) == "" {
		return "", fmt.Errorf("sdk: collection cannot be empty")
	}
	return "/api/v1/databases/" + url.PathEscape(databaseID) +
		"/collections/" + url.PathEscape(collection) + "/documents", nil
}

func documentPath(databaseID, collection, docID string) (string, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return "", err
	}
	if strings.TrimSpace(docID) == "" {
		return "", fmt.Errorf("sdk: document id cannot be empty")
	}
	return base + "/" + url.PathEscape(docID), nil
}

// rawJSON validates and bounds a document value, then keeps its encoded bytes
// intact so Client.do sends the value itself rather than introducing a body
// wrapper. json.Marshal sorts map keys and therefore gives structured Go
// values deterministic JSON; json.RawMessage callers retain their bytes.
func rawJSON(value any, label string) (json.RawMessage, error) {
	if value == nil {
		return nil, fmt.Errorf("sdk: %s cannot be nil", label)
	}
	encoded, err := json.Marshal(value)
	if err != nil {
		return nil, fmt.Errorf("sdk: marshal %s: %w", label, err)
	}
	if len(encoded) > MaxDocumentBytes {
		return nil, fmt.Errorf("sdk: %s exceeds the 1 MiB document limit", label)
	}
	return json.RawMessage(encoded), nil
}

// Create stores a document at a caller-owned ID. TerraBase has no
// server-assigned-ID POST route, so Create and Upsert use the same PUT wire
// operation.
func (d *DocumentsClient) Create(ctx context.Context, databaseID, collection, docID string, value any, opts ...RequestOption) (*DocumentWriteResponse, error) {
	return d.Put(ctx, databaseID, collection, docID, value, opts...)
}

// Put creates or replaces a document (the public upsert operation).
func (d *DocumentsClient) Put(ctx context.Context, databaseID, collection, docID string, value any, opts ...RequestOption) (*DocumentWriteResponse, error) {
	path, err := documentPath(databaseID, collection, docID)
	if err != nil {
		return nil, err
	}
	payload, err := rawJSON(value, "document value")
	if err != nil {
		return nil, err
	}
	var out DocumentWriteResponse
	if err := d.c.do(ctx, "PUT", path, nil, payload, &out, "documents.put", opts...); err != nil {
		return nil, err
	}
	// TerraBase derives the strong ETag from the returned revision. Keep this
	// useful metadata available even on clients whose shared transport only
	// exposes the decoded response body.
	if out.ETag == "" && out.Revision != "" {
		out.ETag = strconv.Quote(out.Revision)
	}
	out.Session = d.c.SessionToken()
	return &out, nil
}

// Upsert is an explicit alias for Put, matching the operation's create-or-
// replace semantics without inventing a second wire route.
func (d *DocumentsClient) Upsert(ctx context.Context, databaseID, collection, docID string, value any, opts ...RequestOption) (*DocumentWriteResponse, error) {
	return d.Put(ctx, databaseID, collection, docID, value, opts...)
}

// Get fetches one document. The successful response body is raw canonical
// JSON, so it is decoded into Body directly rather than into a wrapper.
func (d *DocumentsClient) Get(ctx context.Context, databaseID, collection, docID string, opts ...RequestOption) (*Document, error) {
	path, err := documentPath(databaseID, collection, docID)
	if err != nil {
		return nil, err
	}
	var body json.RawMessage
	if err := d.c.do(ctx, "GET", path, nil, nil, &body, "documents.get", opts...); err != nil {
		return nil, err
	}
	return &Document{ID: docID, Collection: collection, Body: body, Session: d.c.SessionToken()}, nil
}

// List pages documents in canonical key order. after is the last ID from the
// preceding page; limit is bounded to the public 1..=1000 range. A zero limit
// omits the query and lets the server use its default.
func (d *DocumentsClient) List(ctx context.Context, databaseID, collection string, limit int, after string, opts ...RequestOption) (*ListDocumentsResult, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if limit < 0 || limit > MaxDocumentPageSize {
		return nil, fmt.Errorf("sdk: document list limit must be between 0 and %d", MaxDocumentPageSize)
	}
	query := url.Values{}
	if limit > 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	if strings.TrimSpace(after) != "" {
		query.Set("after", after)
	}
	var out ListDocumentsResult
	if err := d.c.do(ctx, "GET", base, query, nil, &out, "documents.list", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Update is retained as a source-compatible name for the PUT upsert route.
// Its response is the server's revision receipt, not a synthetic document.
func (d *DocumentsClient) Update(ctx context.Context, databaseID, collection, docID string, value any, opts ...RequestOption) (*DocumentWriteResponse, error) {
	return d.Put(ctx, databaseID, collection, docID, value, opts...)
}

// Patch applies an RFC 7386 JSON merge patch to a document.
func (d *DocumentsClient) Patch(ctx context.Context, databaseID, collection, docID string, patch any, opts ...RequestOption) (*DocumentWriteResponse, error) {
	path, err := documentPath(databaseID, collection, docID)
	if err != nil {
		return nil, err
	}
	payload, err := rawJSON(patch, "patch")
	if err != nil {
		return nil, err
	}
	var out DocumentWriteResponse
	if err := d.c.do(ctx, "PATCH", path, nil, payload, &out, "documents.patch", opts...); err != nil {
		return nil, err
	}
	if out.ETag == "" && out.Revision != "" {
		out.ETag = strconv.Quote(out.Revision)
	}
	out.Session = d.c.SessionToken()
	return &out, nil
}

// Batch sends one atomic batch of put, patch, and delete operations.
func (d *DocumentsClient) Batch(ctx context.Context, databaseID, collection string, operations []BatchOperation, opts ...RequestOption) (*BatchResponse, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if len(operations) == 0 || len(operations) > MaxBatchOperations {
		return nil, fmt.Errorf("sdk: document batch must contain between 1 and %d operations", MaxBatchOperations)
	}
	for index, operation := range operations {
		if strings.TrimSpace(operation.ID) == "" {
			return nil, fmt.Errorf("sdk: document batch operation %d id cannot be empty", index)
		}
		switch operation.Op {
		case "put", "patch":
			if operation.Value == nil {
				return nil, fmt.Errorf("sdk: document batch operation %d %s value cannot be nil", index, operation.Op)
			}
			if _, err := rawJSON(operation.Value, fmt.Sprintf("document batch operation %d value", index)); err != nil {
				return nil, err
			}
		case "delete":
		default:
			return nil, fmt.Errorf("sdk: document batch operation %d op must be put, patch, or delete", index)
		}
	}
	request := struct {
		Operations []BatchOperation `json:"operations"`
	}{Operations: operations}
	var out BatchResponse
	if err := d.c.do(ctx, "POST", base+"/batch", nil, request, &out, "documents.batch", opts...); err != nil {
		return nil, err
	}
	out.Session = d.c.SessionToken()
	return &out, nil
}

// Delete removes one document (the server writes a tombstone).
func (d *DocumentsClient) Delete(ctx context.Context, databaseID, collection, docID string, opts ...RequestOption) error {
	path, err := documentPath(databaseID, collection, docID)
	if err != nil {
		return err
	}
	return d.c.do(ctx, "DELETE", path, nil, nil, nil, "documents.delete", opts...)
}

// History returns retained versions of a document.
func (d *DocumentsClient) History(ctx context.Context, databaseID, collection, docID string, opts ...RequestOption) ([]HistoryEntry, error) {
	path, err := documentPath(databaseID, collection, docID)
	if err != nil {
		return nil, err
	}
	var out struct {
		History []HistoryEntry `json:"history"`
	}
	if err := d.c.do(ctx, "GET", path+"/history", nil, nil, &out, "documents.history", opts...); err != nil {
		return nil, err
	}
	return out.History, nil
}
