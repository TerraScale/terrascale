package v1

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"iter"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// ShapeOptions selects a collection's optional query-v1 filter.
type ShapeOptions struct {
	Where Filter
}

// ShapeState is the current collection and its checkpoint, after a whole
// response has been applied. Documents retain exact JSON bytes. Each yielded
// state is independently owned by the caller. CursorResume is usable only
// after the complete snapshot; during snapshot paging it is empty.
type ShapeState struct {
	Documents    map[string]Document
	ShapeID      string
	Handle       string
	Offset       string
	CursorResume string
	UpToDate     bool
}

// Shape error codes use the same APIError and errors.Is contract as reads.
const (
	ErrMustRefetch   ErrorCode = "must_refetch"
	ErrShapeBusy     ErrorCode = "shape_busy"
	ErrShapeTooLarge ErrorCode = "shape_too_large"
	ErrUnavailable   ErrorCode = "unavailable"
)

// Subscribe syncs a snapshot, sealed chunks, then the live tail. Each update
// contains the entire current state and checkpoint. Breaking iteration closes
// the subscription; cancellation returns the context error. A refused resume
// clears state and bootstraps again. Repeated operations are always applied:
// a repeated event can carry a newer materialized value.
func (col *Collection) Subscribe(ctx context.Context, options ShapeOptions) iter.Seq2[ShapeState, error] {
	return func(yield func(ShapeState, error) bool) {
		path, err := col.path("shape")
		if err != nil {
			yield(ShapeState{}, err)
			return
		}
		query := url.Values{"offset": {"-1"}}
		if !options.Where.IsZero() {
			where, err := json.Marshal(options.Where)
			if err != nil {
				yield(ShapeState{}, err)
				return
			}
			if len(where) > 8<<10 {
				yield(ShapeState{}, errors.New("terrabase: shape filter exceeds 8 KiB"))
				return
			}
			query.Set("where", string(where))
		}
		state := ShapeState{Documents: map[string]Document{}, Offset: "-1"}
		resumeAttempt := false
		redirected := false
		refetches := 0
		header := http.Header{}
		for {
			if err := ctx.Err(); err != nil {
				yield(cloneShapeState(state), err)
				return
			}
			resp, target, err := col.db.c.shapeGET(ctx, path, query, header)
			if err != nil {
				apiErr, _ := err.(*APIError)
				if resp != nil && resp.status == http.StatusConflict && errors.Is(err, ErrMustRefetch) && apiErr != nil && apiErr.cause == nil {
					newHandle := resp.header.Get("TB-Shape-Handle")
					if !resumeAttempt && state.CursorResume != "" && state.Handle != "" && query.Get("offset") != "-1" && newHandle != "" && newHandle != state.Handle {
						query.Del("handle")
						query.Del("live")
						query.Del("cursor")
						query.Set("offset", "-1")
						resumeAttempt, redirected = true, false
						query.Set("cursor", state.CursorResume)
						if len(shapeTargetWithQuery(target, query)) > (8<<10)-64 {
							query.Del("cursor")
							header.Set("TB-Shape-Resume", state.CursorResume)
						}
						continue
					}
					state = ShapeState{Documents: map[string]Document{}, Offset: "-1"}
					query.Del("handle")
					query.Del("live")
					query.Del("cursor")
					query.Set("offset", "-1")
					header.Del("TB-Shape-Resume")
					resumeAttempt, redirected = false, false
					if !yield(cloneShapeState(state), nil) {
						return
					}
					if refetches > 0 {
						if err := sleep(ctx, backoff(refetches-1)); err != nil {
							yield(cloneShapeState(state), err)
							return
						}
					}
					refetches = min(refetches+1, 6)
					continue
				}
				yield(cloneShapeState(state), err)
				return
			}
			if resp.status == http.StatusTemporaryRedirect {
				if query.Get("offset") != "-1" || redirected {
					yield(cloneShapeState(state), errors.New("terrabase: unexpected shape redirect"))
					return
				}
				query, err = shapeRedirect(target, resp, resumeAttempt)
				if err != nil {
					yield(cloneShapeState(state), err)
					return
				}
				redirected = true
				continue
			}
			if query.Get("offset") == "-1" {
				yield(cloneShapeState(state), errors.New("terrabase: shape bootstrap requires a 307"))
				return
			}
			wire, err := parseShapeBody(state, resp.body, query.Get("handle"), query.Get("offset"))
			if err == nil {
				if query.Get("live") != "true" && *wire.UpToDate && *wire.NextOffset != *wire.Offset {
					err = errors.New("terrabase: sealed shape chunk cannot report up to date")
				} else {
					head := ShapeState{Handle: *wire.Handle, Offset: *wire.NextOffset, UpToDate: *wire.UpToDate}
					err = shapeResponseHeaders(resp, head, query.Get("live") == "true", query.Get("cursor"))
				}
			}
			if err != nil {
				yield(cloneShapeState(state), err)
				return
			}
			state = wire.apply(state)
			refetches = 0
			header.Del("TB-Shape-Resume")
			resumeAttempt, redirected = false, false
			query.Set("handle", state.Handle)
			query.Set("offset", state.Offset)
			query.Del("cursor")
			query.Del("live")
			if state.UpToDate {
				query.Set("live", "true")
				if cursor := resp.header.Get("TB-Shape-Cursor"); cursor != "" {
					query.Set("cursor", cursor)
				}
			}
			if !yield(cloneShapeState(state), nil) {
				return
			}
		}
	}
}

// shapeGET shares the SDK's credential refresh, response limits and error
// types. Shape-capacity retries keep the checkpoint within a fixed exchange budget.
func (c *Client) shapeGET(ctx context.Context, path []string, query url.Values, header http.Header) (*response, string, error) {
	ctx, cancel := context.WithTimeout(ctx, 45*time.Second)
	defer cancel()
	target, err := c.url(ctx, path, query)
	if err != nil {
		return nil, "", err
	}
	if len(target) > (8<<10)-64 {
		return nil, target, errors.New("terrabase: shape request target exceeds 8 KiB")
	}
	refreshed := false
	for attempt := 0; ; attempt++ {
		token, err := c.bearerToken(ctx, "")
		if err != nil {
			return nil, target, err
		}
		resp, err := c.send(ctx, request{method: http.MethodGet, header: header, token: token}, target)
		if resp != nil && resp.status == http.StatusConflict {
			c.shapeKeys.evict(strings.Join(path, "/"))
		}
		if err != nil {
			if resp != nil && (resp.status < 200 || resp.status >= 300) {
				apiErr := parseAPIError(resp.status, resp.header, resp.body)
				apiErr.cause = err
				return resp, target, apiErr
			}
			if ctx.Err() != nil || attempt >= c.maxRetries {
				return resp, target, err
			}
			if err := sleep(ctx, backoff(attempt)); err != nil {
				return nil, target, err
			}
			continue
		}
		if resp.status == http.StatusOK || resp.status == http.StatusTemporaryRedirect {
			return resp, target, nil
		}
		if resp.status == http.StatusUnauthorized && c.invalidateToken(token, parseAPIError(resp.status, resp.header, resp.body), refreshed) {
			refreshed = true
			continue
		}
		apiErr := parseAPIError(resp.status, resp.header, resp.body)
		shapeRetry := (resp.status == http.StatusTooManyRequests && apiErr.Code == ErrShapeBusy) || (resp.status == http.StatusServiceUnavailable && (apiErr.Code == ErrShapeTooLarge || apiErr.Code == ErrUnavailable))
		delay, retry := retryDelay(apiErr, attempt, true, true)
		if shapeRetry {
			delay, retry = backoff(attempt), true
			if apiErr.retryAfterSet {
				delay = apiErr.RetryAfter
			}
		}
		if retry && attempt < c.maxRetries {
			if err := waitRetry(ctx, delay, apiErr); err != nil {
				return nil, target, err
			}
			continue
		}
		return resp, target, apiErr
	}
}

func shapeTargetWithQuery(target string, query url.Values) string {
	u, _ := url.Parse(target)
	u.RawQuery = query.Encode()
	return u.String()
}

// Only a bootstrap redirect within the authorized collection may carry auth.
func shapeRedirect(target string, resp *response, resume bool) (url.Values, error) {
	base, err := url.Parse(target)
	if err != nil {
		return nil, err
	}
	u, err := base.Parse(resp.header.Get("Location"))
	if err != nil || resp.header.Get("Location") == "" {
		return nil, errors.New("terrabase: invalid shape redirect")
	}
	if u.Scheme != base.Scheme || u.Host != base.Host || u.EscapedPath() != base.EscapedPath() || u.User != nil || u.Fragment != "" {
		return nil, errors.New("terrabase: shape redirect leaves the authorized collection")
	}
	query, err := url.ParseQuery(u.RawQuery)
	if err != nil {
		return nil, fmt.Errorf("terrabase: shape redirect query: %w", err)
	}
	for key, values := range query {
		if (key != "where" && key != "handle" && key != "offset") || len(values) != 1 || values[0] == "" {
			return nil, errors.New("terrabase: invalid shape redirect query")
		}
	}
	if !isShapeHandle(query.Get("handle")) || query.Get("handle") != resp.header.Get("TB-Shape-Handle") {
		return nil, errors.New("terrabase: shape redirect handle mismatch")
	}
	snapshot, boundary, page, err := parseShapeOffset(query.Get("offset"))
	if err != nil || boundary < 0 {
		return nil, errors.New("terrabase: invalid shape redirect offset")
	}
	if !resume && (!snapshot || page != "") {
		return nil, errors.New("terrabase: bootstrap redirect must name the first snapshot page")
	}
	if resume && snapshot {
		return nil, errors.New("terrabase: resume redirect must name a commit boundary")
	}
	if (base.Query().Get("where") != "") != (query.Get("where") != "") {
		return nil, errors.New("terrabase: shape redirect dropped or added a filter")
	}
	if where := query.Get("where"); len(where) > 8<<10 || (where != "" && !json.Valid([]byte(where))) {
		return nil, errors.New("terrabase: invalid shape redirect filter")
	}
	return query, nil
}

func shapeResponseHeaders(resp *response, state ShapeState, live bool, previousCursor string) error {
	if resp.header.Get("TB-Shape-Handle") != state.Handle || resp.header.Get("TB-Shape-Next-Offset") != state.Offset || resp.header.Get("TB-Shape-Up-To-Date") != strconv.FormatBool(state.UpToDate) {
		return errors.New("terrabase: shape headers disagree with body")
	}
	if live {
		cursor := resp.header.Get("TB-Shape-Cursor")
		n, err := strconv.ParseInt(cursor, 10, 64)
		if err != nil || n < 0 || strconv.FormatInt(n, 10) != cursor {
			return errors.New("terrabase: invalid live shape cursor")
		}
		if previousCursor != "" {
			prev, _ := strconv.ParseInt(previousCursor, 10, 64)
			if n <= prev {
				return errors.New("terrabase: live shape cursor did not advance")
			}
		}
	}
	return nil
}
