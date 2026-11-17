package v1

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"maps"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"slices"
	"strings"
	"sync"
	"time"

	"github.com/coder/websocket"
)

type ShapeOptions struct {
	Where   string
	Columns []string
}

type ShapeStatus string

const (
	Loading      ShapeStatus = "Loading"
	Live         ShapeStatus = "Live"
	Reconnecting ShapeStatus = "Reconnecting"
)

type ShapeView struct {
	Rows   map[string]Row
	Status ShapeStatus
	Offset string
}

type Shape struct {
	updates chan ShapeView
	mu      sync.Mutex
	err     error
}

func (s *Shape) Updates() <-chan ShapeView { return s.updates }

func (s *Shape) Err() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.err
}

var shapeName = regexp.MustCompile(`^[A-Za-z_][A-Za-z0-9_]*$`)
var shapeHandle = regexp.MustCompile(`^h1-[a-f0-9]{16}$`)
var shapeBoundary = regexp.MustCompile(`^(0|[1-9][0-9]*)$`)
var shapeSnapshot = regexp.MustCompile(`^snap\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$`)

func (d *Database) Shape(ctx context.Context, table string, options ShapeOptions) (*Shape, error) {
	if !shapeName.MatchString(table) || len(table) > 63 {
		return nil, errors.New("terrabase: invalid shape table")
	}
	query := url.Values{}
	if options.Where != "" {
		if strings.TrimSpace(options.Where) == "" {
			return nil, errors.New("terrabase: shape where must be non-empty")
		}
		query.Set("where", options.Where)
	}
	if options.Columns != nil {
		seen := map[string]bool{}
		if len(options.Columns) == 0 {
			return nil, errors.New("terrabase: shape columns must be non-empty")
		}
		for _, name := range options.Columns {
			if !shapeName.MatchString(name) || seen[name] {
				return nil, errors.New("terrabase: shape columns must be distinct identifiers")
			}
			seen[name] = true
		}
		query.Set("columns", strings.Join(options.Columns, ","))
	}
	path, err := d.path("tables", table, "shape")
	if err != nil {
		return nil, err
	}
	c, err := d.c.projectClient(d.project)
	if err != nil {
		return nil, err
	}
	s := &Shape{updates: make(chan ShapeView, 1)}
	go func() {
		err := s.run(ctx, c, path, query)
		s.mu.Lock()
		s.err = err
		s.mu.Unlock()
		close(s.updates)
	}()
	return s, nil
}

type shapeOp struct {
	Op, Key, Move string
	Row           json.RawMessage
}

type shapeCommit struct {
	ID                string
	Ops               []shapeOp
	Confirmed, Revert []string
}

type shapePage struct {
	Handle      string
	NextOffset  string `json:"next_offset"`
	UpToDate    *bool  `json:"up_to_date"`
	Changes     []shapeCommit
	Provisional []shapeCommit
	Local       string
}

func applyShape(rows map[string]Row, ops []shapeOp) error {
	for _, op := range ops {
		if op.Key == "" || op.Move != "" && op.Move != "in" && op.Move != "out" {
			return errors.New("terrabase: invalid shape operation")
		}
		switch {
		case op.Move == "out" || op.Move == "" && op.Op == "delete":
			delete(rows, op.Key)
		case op.Move == "in" || op.Op == "insert" || op.Op == "update":
			if op.Row == nil {
				return errors.New("terrabase: missing shape row")
			}
			row := Row{}
			decoder := json.NewDecoder(bytes.NewReader(op.Row))
			if token, err := decoder.Token(); err != nil || token != json.Delim('{') {
				return errors.New("terrabase: invalid shape row")
			}
			for decoder.More() {
				name, err := decoder.Token()
				if err != nil {
					return err
				}
				var raw json.RawMessage
				if err := decoder.Decode(&raw); err != nil {
					return err
				}
				value, err := sqlCell(raw)
				if err != nil {
					return err
				}
				row.columns = append(row.columns, Column{Name: name.(string)})
				row.values = append(row.values, value)
			}
			rows[op.Key] = row
		default:
			return errors.New("terrabase: invalid shape operation")
		}
	}
	return nil
}

func (s *Shape) publish(ctx context.Context, rows map[string]Row, pending []shapeCommit, status ShapeStatus, offset string) error {
	view := ShapeView{Rows: maps.Clone(rows), Status: status, Offset: offset}
	for _, commit := range pending {
		if err := applyShape(view.Rows, commit.Ops); err != nil {
			return err
		}
	}
	for key, row := range view.Rows {
		row.columns, row.values = slices.Clone(row.columns), slices.Clone(row.values)
		for i, value := range row.values {
			if blob, ok := value.([]byte); ok {
				row.values[i] = slices.Clone(blob)
			}
		}
		view.Rows[key] = row
	}
	select {
	case <-s.updates:
	default:
	}
	select {
	case s.updates <- view:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (s *Shape) run(ctx context.Context, c *Client, path []string, filter url.Values) error {
	rows := map[string]Row{}
	var pending []shapeCommit
	handle, offset, cursor, local := "", "-1", "", "0"
	live, hintProtocol := false, false
	var hints *websocket.Conn
	var hintRetry time.Time
	defer func() {
		if hints != nil {
			_ = hints.CloseNow()
		}
	}()
	status := Loading
	for attempt := 0; ; {
		if err := s.publish(ctx, rows, pending, status, offset); err != nil {
			return err
		}
		query := maps.Clone(filter)
		query.Set("offset", offset)
		if handle != "" {
			query.Set("handle", handle)
		}
		if live {
			query.Set("live", "true")
			query.Set("local", local)
			if cursor != "" {
				query.Set("cursor", cursor)
			}
		}
		var err error
		if live && hintProtocol && !time.Now().Before(hintRetry) {
			err = c.waitShapeHint(ctx, path, filter, handle, offset, &hints)
			if err != nil {
				if hints != nil {
					_ = hints.CloseNow()
					hints = nil
				}
				var api *APIError
				if !errors.As(err, &api) || api.Code != "must_refetch" && shapeRetryable(err) {
					pause := backoff(attempt)
					if api != nil && api.retryAfterSet {
						pause = api.RetryAfter
					}
					hintRetry = time.Now().Add(pause)
					if err := s.publish(ctx, rows, pending, Reconnecting, offset); err != nil {
						return err
					}
					err = nil
				}
			}
		}
		var resp *response
		if err == nil {
			resp, err = c.do(ctx, request{method: http.MethodGet, path: path, query: query})
		}
		if resp != nil && resp.status == http.StatusTemporaryRedirect && handle == "" {
			base, resolveErr := c.url(ctx, path, filter)
			target, parseErr := url.Parse(resp.header.Get("Location"))
			if resolveErr != nil {
				return resolveErr
			}
			original, _ := url.Parse(base)
			if parseErr != nil {
				return parseErr
			}
			target = original.ResolveReference(target)
			if target.Scheme != original.Scheme || target.Host != original.Host || target.Path != original.Path || target.User != nil || !shapeHandle.MatchString(target.Query().Get("handle")) || !shapeSnapshot.MatchString(target.Query().Get("offset")) {
				return errors.New("terrabase: invalid shape bootstrap redirect")
			}
			handle, offset = target.Query().Get("handle"), target.Query().Get("offset")
			continue
		}
		if err != nil {
			if ctx.Err() != nil {
				return ctx.Err()
			}
			var api *APIError
			if errors.As(err, &api) && api.Code == "must_refetch" {
				rows, pending = map[string]Row{}, nil
				handle, offset, cursor, local, live, status = "", "-1", "", "0", false, Loading
				if hints != nil {
					_ = hints.CloseNow()
					hints = nil
				}
				continue
			}
			if !shapeRetryable(err) {
				return err
			}
			if err := s.publish(ctx, rows, pending, Reconnecting, offset); err != nil {
				return err
			}
			pause := backoff(attempt)
			if api != nil && api.retryAfterSet {
				pause = api.RetryAfter
			}
			if err := sleep(ctx, pause); err != nil {
				return err
			}
			attempt++
			continue
		}
		var page shapePage
		if decodeExact(resp.body, &page) != nil || page.UpToDate == nil || page.Changes == nil || !shapeHandle.MatchString(page.Handle) || handle != "" && page.Handle != handle || !shapeBoundary.MatchString(page.NextOffset) && !shapeSnapshot.MatchString(page.NextOffset) {
			return errors.New("terrabase: invalid shape page")
		}
		for _, commit := range page.Changes {
			if err := applyShape(rows, commit.Ops); err != nil {
				return err
			}
			pending = slices.DeleteFunc(pending, func(p shapeCommit) bool {
				return slices.Contains(commit.Confirmed, p.ID) || slices.Contains(commit.Revert, p.ID)
			})
		}
		if page.Provisional != nil {
			pending, local = page.Provisional, page.Local
		}
		handle, offset, live = page.Handle, page.NextOffset, *page.UpToDate
		cursor = resp.header.Get("TB-Shape-Cursor")
		hintProtocol = hintProtocol || resp.header.Get("TB-Shape-Hints") == "terrascale.shape.v1"
		status, attempt = Loading, 0
		if live {
			status = Live
		}
	}
}

func shapeRetryable(err error) bool {
	var api *APIError
	if !errors.As(err, &api) {
		var transport *url.Error
		var network net.Error
		return errors.As(err, &transport) || errors.As(err, &network) || errors.Is(err, io.EOF) || errors.Is(err, io.ErrUnexpectedEOF) || errors.Is(err, context.DeadlineExceeded)
	}
	switch api.Code {
	case "invalid_request", "unsupported_table", "unknown_database", "unknown_table", "invalid_key", "key_revoked", "key_expired", "forbidden_scope", "rule_denied", "column_not_readable", "rules_invalid":
		return false
	}
	switch api.Status {
	case 408, 429, 500, 502, 503, 504:
		return true
	}
	return false
}

func (c *Client) waitShapeHint(ctx context.Context, path []string, filter url.Values, handle, offset string, conn **websocket.Conn) error {
	if *conn == nil {
		query := maps.Clone(filter)
		query.Set("handle", handle)
		query.Set("offset", offset)
		target, err := c.url(ctx, append(slices.Clone(path), "ws"), query)
		if err != nil {
			return err
		}
		token, err := c.bearerToken(ctx, "")
		if err != nil {
			return err
		}
		header := http.Header{"Authorization": {"Bearer " + token}}
		c.pin(header)
		dialCtx, cancel := context.WithTimeout(ctx, OperationTimeout)
		ws, resp, err := websocket.Dial(dialCtx, "ws"+strings.TrimPrefix(target, "http"), &websocket.DialOptions{HTTPClient: c.hc, HTTPHeader: header, Subprotocols: []string{"terrascale.shape.v1"}})
		cancel()
		if err != nil {
			if resp != nil && resp.StatusCode != http.StatusSwitchingProtocols {
				body, readErr := io.ReadAll(io.LimitReader(resp.Body, c.maxResponse))
				_ = resp.Body.Close()
				api := parseAPIError(resp.StatusCode, resp.Header, body)
				api.cause = readErr
				if resp.StatusCode != 401 && resp.StatusCode != 403 && resp.StatusCode != 409 {
					return errors.New("terrabase: shape hints unavailable")
				}
				return api
			}
			return err
		}
		*conn = ws
		ws.SetReadLimit(65536)
		if ws.Subprotocol() != "terrascale.shape.v1" {
			return errors.New("terrabase: invalid shape hint subprotocol")
		}
	}
	for {
		typ, data, err := (*conn).Read(ctx)
		if err != nil {
			return err
		}
		var frame struct {
			Type, Handle, Offset, Code string
			NextOffset                 string   `json:"next_offset"`
			RetryAfter                 *float64 `json:"retry_after"`
		}
		if typ != websocket.MessageText || decodeExact(data, &frame) != nil {
			return errors.New("terrabase: invalid shape hint")
		}
		switch frame.Type {
		case "ready":
			continue
		case "error":
			status := 503
			if frame.Code == "must_refetch" {
				status = 409
			}
			if frame.Code == "invalid_key" || frame.Code == "key_revoked" || frame.Code == "key_expired" {
				status = 401
			}
			api := &APIError{Status: status, Code: ErrorCode(frame.Code)}
			if frame.RetryAfter != nil && *frame.RetryAfter >= 0 {
				api.RetryAfter, api.retryAfterSet = time.Duration(*frame.RetryAfter*float64(time.Second)), true
			}
			return api
		case "new_data", "provisional":
			if frame.Handle != handle {
				return &APIError{Status: 409, Code: "must_refetch"}
			}
			if frame.Type == "provisional" || shapeBoundary.MatchString(frame.NextOffset) && (len(frame.NextOffset) > len(offset) || len(frame.NextOffset) == len(offset) && frame.NextOffset > offset) {
				return nil
			}
		default:
			return errors.New("terrabase: invalid shape hint type")
		}
	}
}
