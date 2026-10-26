package v1

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"iter"
	"mime"
	"net/http"
	"net/url"
	"terrascale.tech/sdk/internal/entityid"
)

// ExportOptions resumes after a complete exported document. Both fields must
// be set together. A resumed request reads a new snapshot.
type ExportOptions struct {
	AfterCollection string
	AfterID         string
}

// ExportDocument is one live document, with its collection and stored document or SQL row key.
type ExportDocument struct {
	Collection string          `json:"collection"`
	ID         string          `json:"id"`
	Document   json.RawMessage `json:"document"`
}

// Export streams all live documents from one server read snapshot, ordered by
// collection then ID. It requires documents:read. Relational tables, empty
// collections, schemas, indexes, TTL rules and other metadata are not included;
// this is a document export, not a database backup.
//
// Each record is bounded by DefaultMaxResponseBytes; the total stream is not.
// Success requires complete newline-terminated records and clean HTTP EOF.
// Errors stop iteration without retrying or resuming. Consumers must exhaust
// the iterator to establish completion; breaking early closes the response.
func (d *Database) Export(ctx context.Context, options ExportOptions) iter.Seq2[ExportDocument, error] {
	return func(yield func(ExportDocument, error) bool) {
		p, err := d.path("export")
		q := url.Values{}
		if err == nil && (options.AfterCollection != "" || options.AfterID != "") {
			err = checkName("collection", options.AfterCollection)
			if err == nil {
				err = entityid.StoredKey(options.AfterID)
			}
			q.Set("after_collection", options.AfterCollection)
			q.Set("after_id", options.AfterID)
		}
		if err != nil {
			yield(ExportDocument{}, err)
			return
		}
		target, err := d.c.url(ctx, p, q)
		if err != nil {
			yield(ExportDocument{}, err)
			return
		}
		for refreshed := false; ; refreshed = true {
			token, err := d.c.bearerToken(ctx, "")
			if err != nil {
				yield(ExportDocument{}, err)
				return
			}
			req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
			if err != nil {
				yield(ExportDocument{}, err)
				return
			}
			req.Header.Set("Authorization", "Bearer "+token)
			req.Header.Set("Accept", "application/x-ndjson")
			res, err := d.c.hc.Do(req)
			if err != nil {
				yield(ExportDocument{}, err)
				return
			}
			if res.StatusCode != http.StatusOK {
				data, readErr := io.ReadAll(io.LimitReader(res.Body, d.c.maxResponse+1))
				res.Body.Close()
				if int64(len(data)) > d.c.maxResponse {
					data = data[:d.c.maxResponse]
					readErr = errors.Join(readErr, errors.New("terrabase: export error response exceeds limit"))
				}
				if res.StatusCode == http.StatusUnauthorized && readErr == nil && d.c.invalidateToken(token, parseAPIError(res.StatusCode, res.Header, data), refreshed) {
					continue
				}
				apiErr := parseAPIError(res.StatusCode, res.Header, data)
				apiErr.cause = readErr
				yield(ExportDocument{}, apiErr)
				return
			}
			defer res.Body.Close()
			media, _, err := mime.ParseMediaType(res.Header.Get("Content-Type"))
			if err != nil || media != "application/x-ndjson" {
				yield(ExportDocument{}, errors.New("terrabase: export response is not application/x-ndjson"))
				return
			}
			scanner := bufio.NewScanner(res.Body)
			limit := int(min(d.c.maxResponse, int64(^uint(0)>>1)-1)) + 1
			scanner.Buffer(make([]byte, min(limit, 4096)), limit)
			scanner.Split(exportLine)
			for ctx.Err() == nil && scanner.Scan() {
				var document ExportDocument
				err := decodeExact(scanner.Bytes(), &document)
				if err == nil {
					err = checkName("collection", document.Collection)
				}
				if err == nil {
					err = entityid.StoredKey(document.ID)
				}
				if err == nil && len(document.Document) == 0 {
					err = errors.New("missing document")
				}
				if err != nil {
					yield(ExportDocument{}, fmt.Errorf("terrabase: invalid export record: %w", err))
					return
				}
				if err := ctx.Err(); err != nil {
					yield(ExportDocument{}, err)
					return
				}
				if !yield(document, nil) {
					return
				}
			}
			if err := errors.Join(ctx.Err(), scanner.Err()); err != nil {
				yield(ExportDocument{}, fmt.Errorf("terrabase: read export: %w", err))
			}
			return
		}
	}
}

func exportLine(data []byte, atEOF bool) (int, []byte, error) {
	if i := bytes.IndexByte(data, '\n'); i >= 0 {
		return i + 1, data[:i], nil
	}
	if atEOF && len(data) != 0 {
		return 0, nil, io.ErrUnexpectedEOF
	}
	return 0, nil, nil
}
