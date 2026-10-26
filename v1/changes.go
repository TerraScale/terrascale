package v1

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"iter"
	"mime"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"terrascale.tech/sdk/internal/entityid"
	"time"
)

// ChangesOptions selects the starting position and collections of a feed.
// A cursor is valid only with the same collection filter.
type ChangesOptions struct {
	// Cursor is an opaque server cursor, "now", or empty for retained history.
	Cursor string
	// Collections is empty for all collections, otherwise at most 64 names.
	Collections []string
}

// ChangeEvent is a change or cursor SSE event. Cursor is the position after
// the event, or empty until the final change in a commit group. Persist only
// nonempty cursors. Cursor events have no document fields. Delivery is at least once;
// deduplicate changes by Origin, Version, Collection and ID. Document is the
// current value on the serving machine, which may differ from the operation.
type ChangeEvent struct {
	Type       string          `json:"-"`
	Cursor     string          `json:"-"`
	Collection string          `json:"collection"`
	ID         string          `json:"id"`
	Op         string          `json:"op"`
	Version    string          `json:"version"`
	Origin     string          `json:"origin"`
	Document   json.RawMessage `json:"document"`
}

// Changes streams committed document changes. It reconnects after clean EOF
// using Last-Event-ID. Cancellation, malformed events and HTTP errors stop
// iteration; an expired cursor is returned without resetting the position.
// Collection and index metadata and documents from dropped collections are
// not included in the server feed.
func (d *Database) Changes(ctx context.Context, options ChangesOptions) iter.Seq2[ChangeEvent, error] {
	collections := append([]string(nil), options.Collections...)
	return func(yield func(ChangeEvent, error) bool) {
		p, err := d.path("changes")
		if err != nil {
			yield(ChangeEvent{}, err)
			return
		}
		if len(collections) > 64 {
			yield(ChangeEvent{}, errors.New("terrabase: changes accepts at most 64 collections"))
			return
		}
		for _, name := range collections {
			if err := checkName("collection", name); err != nil {
				yield(ChangeEvent{}, err)
				return
			}
		}
		query := url.Values{}
		if options.Cursor != "" {
			query.Set("cursor", options.Cursor)
		}
		if len(collections) > 0 {
			query.Set("collections", strings.Join(collections, ","))
		}
		target, err := d.c.url(ctx, p, query)
		if err != nil {
			yield(ChangeEvent{}, err)
			return
		}
		cursor := ""
		refreshed := false
		for {
			if err := ctx.Err(); err != nil {
				yield(ChangeEvent{}, err)
				return
			}
			req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
			if err != nil {
				yield(ChangeEvent{}, err)
				return
			}
			token, err := d.c.bearerToken(ctx, "")
			if err != nil {
				yield(ChangeEvent{}, err)
				return
			}
			req.Header.Set("Authorization", "Bearer "+token)
			req.Header.Set("Accept", "text/event-stream")
			if cursor != "" {
				req.Header.Set("Last-Event-ID", cursor)
			}
			res, err := d.c.hc.Do(req)
			if err != nil {
				yield(ChangeEvent{}, err)
				return
			}
			if res.StatusCode != http.StatusOK {
				data, readErr := io.ReadAll(io.LimitReader(res.Body, d.c.maxResponse+1))
				res.Body.Close()
				if res.StatusCode == http.StatusUnauthorized && readErr == nil && int64(len(data)) <= d.c.maxResponse && d.c.invalidateToken(token, parseAPIError(res.StatusCode, res.Header, data), refreshed) {
					refreshed = true
					continue
				}
				if int64(len(data)) > d.c.maxResponse {
					data = data[:d.c.maxResponse]
					readErr = errors.Join(readErr, errors.New("terrabase: changes error response exceeds limit"))
				}
				apiErr := parseAPIError(res.StatusCode, res.Header, data)
				apiErr.cause = readErr
				yield(ChangeEvent{}, apiErr)
				return
			}
			media, _, err := mime.ParseMediaType(res.Header.Get("Content-Type"))
			if err != nil || media != "text/event-stream" {
				res.Body.Close()
				yield(ChangeEvent{}, errors.New("terrabase: changes response is not text/event-stream"))
				return
			}
			stopped := false
			refreshed = false
			err = readChanges(res.Body, d.c.maxResponse, res.Header, func(event ChangeEvent) bool {
				if event.Cursor != "" {
					cursor = event.Cursor
				}
				if !yield(event, nil) {
					stopped = true
					return false
				}
				return true
			})
			res.Body.Close()
			if stopped {
				return
			}
			if err != nil {
				yield(ChangeEvent{}, err)
				return
			}
			if cursor == "" {
				yield(ChangeEvent{}, errors.New("terrabase: changes stream ended without a resume cursor"))
				return
			}
			// A finite stream closes at token expiry or the server's stream lifetime.
			if err := sleep(ctx, 100*time.Millisecond); err != nil {
				yield(ChangeEvent{}, err)
				return
			}
		}
	}
}

func readChanges(body io.Reader, limit int64, header http.Header, yield func(ChangeEvent) bool) error {
	scanner := bufio.NewScanner(body)
	scanner.Buffer(make([]byte, min(limit+1, 4096)), int(min(limit+1, int64(^uint(0)>>1))))
	var kind, id string
	var data strings.Builder
	var size int64
	dispatch := func() (bool, error) {
		if data.Len() == 0 {
			if kind == "change" || kind == "cursor" || kind == "error" {
				return false, errors.New("terrabase: changes event has no data")
			}
			return true, nil
		}
		if kind == "error" {
			var envelope struct {
				Error *struct {
					Code string `json:"code"`
				} `json:"error"`
			}
			if err := decodeExact([]byte(data.String()), &envelope); err != nil {
				return false, fmt.Errorf("terrabase: decode changes error: %w", err)
			}
			if envelope.Error == nil || envelope.Error.Code == "" {
				return false, errors.New("terrabase: changes error has no code")
			}
			return false, parseAPIError(http.StatusOK, header, []byte(data.String()))
		}
		if kind != "change" && kind != "cursor" {
			return false, fmt.Errorf("terrabase: unknown changes event %q", kind)
		}
		if kind == "cursor" && id == "" {
			return false, errors.New("terrabase: changes event has no cursor")
		}
		event := ChangeEvent{Type: kind, Cursor: id}
		if kind == "cursor" {
			var checkpoint struct {
				Cursor string `json:"cursor"`
			}
			if err := decodeExact([]byte(data.String()), &checkpoint); err != nil {
				return false, fmt.Errorf("terrabase: decode cursor event: %w", err)
			}
			if checkpoint.Cursor != id {
				return false, errors.New("terrabase: cursor event disagrees with its ID")
			}
		} else {
			if err := decodeExact([]byte(data.String()), &event); err != nil {
				return false, fmt.Errorf("terrabase: decode change event: %w", err)
			}
			if event.Collection == "" || event.ID == "" || event.Version == "" || event.Origin == "" || (event.Op != "upsert" && event.Op != "delete") {
				return false, errors.New("terrabase: incomplete change event")
			}
			if err := checkName("collection", event.Collection); err != nil {
				return false, err
			}
			if err := entityid.StoredKey(event.ID); err != nil {
				return false, err
			}
			if _, err := strconv.ParseUint(event.Version, 10, 63); err != nil {
				return false, errors.New("terrabase: invalid change version")
			}
			if len(event.Origin) != 16 {
				return false, errors.New("terrabase: invalid change origin")
			}
			if _, err := strconv.ParseUint(event.Origin, 16, 64); err != nil {
				return false, errors.New("terrabase: invalid change origin")
			}
			if len(event.Document) == 0 {
				return false, errors.New("terrabase: change event is missing document")
			}
			if string(event.Document) != "null" && event.Document[0] != '{' {
				return false, errors.New("terrabase: change document must be an object or null")
			}
		}
		return yield(event), nil
	}
	for scanner.Scan() {
		line := scanner.Text()
		size += int64(len(line)) + 1
		if size > limit {
			return errors.New("terrabase: changes event exceeds response limit")
		}
		if line == "" {
			more, err := dispatch()
			if err != nil || !more {
				return err
			}
			kind, id = "", ""
			data.Reset()
			size = 0
			continue
		}
		if strings.HasPrefix(line, ":") {
			continue
		}
		field, value, _ := strings.Cut(line, ":")
		value = strings.TrimPrefix(value, " ")
		switch field {
		case "event":
			kind = value
		case "id":
			if strings.ContainsRune(value, '\x00') {
				return errors.New("terrabase: changes cursor contains NUL")
			}
			id = value
		case "data":
			if data.Len() > 0 {
				data.WriteByte('\n')
			}
			data.WriteString(value)
		}
	}
	if err := scanner.Err(); err != nil {
		return fmt.Errorf("terrabase: read changes: %w", err)
	}
	if size != 0 {
		return errors.New("terrabase: truncated changes event")
	}
	return nil
}
