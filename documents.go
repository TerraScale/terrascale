// Document CRUD client (GOAL D-13).
//
// Paths are a hand-written stub against /api/v1 pending the published
// terrabase openapi.json: documents live under
// /api/v1/databases/{databaseId}/collections/{collection}/documents.
package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"net/url"
	"strconv"
	"strings"
)

// Document is a single stored document with its causal metadata.
type Document struct {
	ID         string          `json:"id"`
	Collection string          `json:"collection,omitempty"`
	Body       json.RawMessage `json:"body"`
	Revision   string          `json:"revision,omitempty"`
}

// HistoryEntry is one retained document version (GOAL D-25, D-48 conflict viewer).
type HistoryEntry struct {
	Revision string          `json:"revision"`
	Body     json.RawMessage `json:"body,omitempty"`
	Dot      string          `json:"dot,omitempty"`
	HLC      string          `json:"hlc,omitempty"`
	Reason   string          `json:"reason,omitempty"`
}

// ListDocumentsResult is one page of documents.
type ListDocumentsResult struct {
	Documents  []Document `json:"documents"`
	NextCursor string     `json:"nextCursor,omitempty"`
}

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

// Create stores a new document. Body is marshaled as canonical JSON.
func (d *DocumentsClient) Create(ctx context.Context, databaseID, collection string, body any, opts ...RequestOption) (*Document, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if body == nil {
		return nil, fmt.Errorf("sdk: document body cannot be nil")
	}
	var out Document
	if err := d.c.do(ctx, "POST", base, nil, map[string]any{"body": body}, &out, "documents.create", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Get fetches one document by id.
func (d *DocumentsClient) Get(ctx context.Context, databaseID, collection, docID string, opts ...RequestOption) (*Document, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(docID) == "" {
		return nil, fmt.Errorf("sdk: document id cannot be empty")
	}
	var out Document
	if err := d.c.do(ctx, "GET", base+"/"+url.PathEscape(docID), nil, nil, &out, "documents.get", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// List pages documents. limit <= 0 omits the limit; cursor resumes a page.
func (d *DocumentsClient) List(ctx context.Context, databaseID, collection string, limit int, cursor string, opts ...RequestOption) (*ListDocumentsResult, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if limit < 0 {
		return nil, fmt.Errorf("sdk: limit cannot be negative")
	}
	query := url.Values{}
	if limit > 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	if cursor != "" {
		query.Set("cursor", cursor)
	}
	var out ListDocumentsResult
	if err := d.c.do(ctx, "GET", base, query, nil, &out, "documents.list", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Update replaces a document body (full replacement).
func (d *DocumentsClient) Update(ctx context.Context, databaseID, collection, docID string, body any, opts ...RequestOption) (*Document, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(docID) == "" {
		return nil, fmt.Errorf("sdk: document id cannot be empty")
	}
	if body == nil {
		return nil, fmt.Errorf("sdk: document body cannot be nil")
	}
	var out Document
	if err := d.c.do(ctx, "PUT", base+"/"+url.PathEscape(docID), nil, map[string]any{"body": body}, &out, "documents.update", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Patch merges a partial body into a document.
func (d *DocumentsClient) Patch(ctx context.Context, databaseID, collection, docID string, patch any, opts ...RequestOption) (*Document, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(docID) == "" {
		return nil, fmt.Errorf("sdk: document id cannot be empty")
	}
	if patch == nil {
		return nil, fmt.Errorf("sdk: patch cannot be nil")
	}
	var out Document
	if err := d.c.do(ctx, "PATCH", base+"/"+url.PathEscape(docID), nil, map[string]any{"body": patch}, &out, "documents.patch", opts...); err != nil {
		return nil, err
	}
	return &out, nil
}

// Delete removes a document.
func (d *DocumentsClient) Delete(ctx context.Context, databaseID, collection, docID string, opts ...RequestOption) error {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return err
	}
	if strings.TrimSpace(docID) == "" {
		return fmt.Errorf("sdk: document id cannot be empty")
	}
	return d.c.do(ctx, "DELETE", base+"/"+url.PathEscape(docID), nil, nil, nil, "documents.delete", opts...)
}

// History returns retained versions of a document ("why this version won").
func (d *DocumentsClient) History(ctx context.Context, databaseID, collection, docID string, opts ...RequestOption) ([]HistoryEntry, error) {
	base, err := documentBase(databaseID, collection)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(docID) == "" {
		return nil, fmt.Errorf("sdk: document id cannot be empty")
	}
	var out struct {
		History []HistoryEntry `json:"history"`
	}
	if err := d.c.do(ctx, "GET", base+"/"+url.PathEscape(docID)+"/history", nil, nil, &out, "documents.history", opts...); err != nil {
		return nil, err
	}
	return out.History, nil
}
