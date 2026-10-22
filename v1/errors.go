package v1

import (
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// ErrorCode is a TerraBase error code. Each code is also an error value, so
// errors.Is(err, v1.ErrNotFound) matches any *APIError carrying that code.
type ErrorCode string

func (c ErrorCode) Error() string { return "terrabase: " + string(c) }

// Error codes TerraBase sends in {"error":{"code":…}} bodies.
const (
	ErrNotModified          ErrorCode = "not_modified" // 304; synthesized, the response has no body
	ErrInvalidRequest       ErrorCode = "invalid_request"
	ErrInvalidQuery         ErrorCode = "invalid_query"
	ErrInvalidCursor        ErrorCode = "invalid_cursor"
	ErrCursorExpired        ErrorCode = "cursor_expired" // 410: stream cursor is outside retention
	ErrChangesBusy          ErrorCode = "changes_busy"   // 429: database stream capacity is exhausted
	ErrInvalidPatch         ErrorCode = "invalid_patch"
	ErrSortRequiresIndex    ErrorCode = "sort_requires_index"
	ErrAssertRequiresIndex  ErrorCode = "assert_requires_index"
	ErrInvalidKey           ErrorCode = "invalid_key" // 401: a missing or wrong API key or service credential
	ErrForbiddenScope       ErrorCode = "forbidden_scope"
	ErrNotFound             ErrorCode = "not_found"
	ErrUnknownDatabase      ErrorCode = "unknown_database"
	ErrUnknownCollection    ErrorCode = "unknown_collection"
	ErrUnknownRoute         ErrorCode = "unknown_route" // also every Management 404: an unknown id or an organization the user is not a member of
	ErrMethodNotAllowed     ErrorCode = "method_not_allowed"
	ErrNameTaken            ErrorCode = "name_taken"
	ErrPatchConflict        ErrorCode = "patch_conflict"
	ErrPreconditionFailed   ErrorCode = "precondition_failed"
	ErrPayloadTooLarge      ErrorCode = "payload_too_large"
	ErrUnsupportedMediaType ErrorCode = "unsupported_media_type"
	ErrRateLimited          ErrorCode = "rate_limited"
	ErrInternalError        ErrorCode = "internal_error" // 500; never retried, its effect is unknown
	ErrCapacity             ErrorCode = "capacity"
	ErrStorageUnavailable   ErrorCode = "storage_unavailable"
	ErrStoredCorruption     ErrorCode = "stored_corruption"
	ErrCommitUncertain      ErrorCode = "commit_uncertain"      // also matches mutation transport failures; the write may have committed
	ErrWriteOutcomeUnknown  ErrorCode = "write_outcome_unknown" // 503: the write may have committed without remote confirmation
	ErrDependenciesUnavail  ErrorCode = "dependencies_unavailable"
	// ErrRemoteRegionUnavailable, ErrConfirmationBusy and ErrCatalogStale are
	// 503s refused before execution; replay-safe requests retry them.
	ErrRemoteRegionUnavailable ErrorCode = "remote_region_unavailable"
	ErrConfirmationBusy        ErrorCode = "confirmation_busy"
	ErrCatalogStale            ErrorCode = "catalog_stale"
	// ErrCopyFaulted is a 503: this node's database copy awaits certified
	// recovery. Retrying the same node does not help, so it is not retried.
	ErrCopyFaulted        ErrorCode = "copy_faulted"
	ErrQuotaExceeded      ErrorCode = "quota_exceeded"      // 429
	ErrRequestTimeout     ErrorCode = "request_timeout"     // 408
	ErrDatabaseReplicated ErrorCode = "database_replicated" // 409: delete of a database with replica regions
	ErrIndexExists        ErrorCode = "index_exists"        // 409: an index with the name, or on the same fields of the collection
	ErrUniqueViolation    ErrorCode = "unique_violation"    // 409: a document write or transaction duplicates a unique key; the whole transaction rolls back
	// ErrSchemaRequiresEmptyCollection is a 409: unique keys change only while
	// the collection is empty.
	ErrSchemaRequiresEmptyCollection ErrorCode = "schema_requires_empty_collection"
	// ErrWrongRegion is a 421: the database is not placed in the called
	// region. APIError.HomeRegion and Regions name where it is. The client
	// never retries it elsewhere: it sends the request to a region in Regions.
	ErrWrongRegion ErrorCode = "wrong_region"
)

// Codes synthesized for Management responses whose body is a plain JSON
// string message instead of an error object.
const (
	// ErrForbidden is a 403: the member's role does not allow the request.
	ErrForbidden ErrorCode = "forbidden"
	// ErrConflict is a 409: a taken name, a non-empty delete, the last owner,
	// an existing member, a pending or expired invitation.
	ErrConflict ErrorCode = "conflict"
)

// ValidationDetail identifies a failed schema keyword without document values.
type ValidationDetail struct {
	Path    string `json:"path"`
	Keyword string `json:"keyword"`
}

// APIError is a non-2xx TerraBase response.
type APIError struct {
	// Status is the HTTP status code.
	Status int
	// Header is an owned copy of the response headers, including routing hints.
	Header http.Header
	// Code is error.code; ErrInvalidRequest, ErrForbidden or ErrConflict for
	// a bare Management message; otherwise a synthesized "http_<status>".
	Code    ErrorCode
	Message string
	// Op is the failing transaction op index, when the error names one.
	Op *int
	// RetryAfter is the parsed Retry-After header, if any.
	RetryAfter time.Duration
	// ETag is the unquoted ETag of the response; a 404 for a deleted document
	// carries the tombstone's version so it can be recreated with IfMatch.
	ETag string
	// Body preserves the complete response body, including malformed errors.
	Body json.RawMessage
	// RequestID is the response's X-Request-ID, when supplied by the service.
	RequestID string
	// HomeRegion and Regions are the database's home and every region that
	// holds it, as region ids (X-TerraBase-Database-Home and
	// X-TerraBase-Database-Regions), on a 421 ErrWrongRegion.
	HomeRegion string
	Regions    []string
	// Region and Machine identify the accepting node on write_outcome_unknown.
	Region  string
	Machine string
	// Details contains bounded JSON Schema validation paths and keywords.
	Details []ValidationDetail

	retryAfterSet bool
	cause         error
}

// Unwrap preserves a failure encountered while reading the error response.
func (e *APIError) Unwrap() error { return e.cause }

func (e *APIError) Error() string {
	var b strings.Builder
	fmt.Fprintf(&b, "terrabase: %d %s", e.Status, string(e.Code))
	if e.Message != "" {
		b.WriteString(": " + e.Message)
	}
	if e.Op != nil {
		fmt.Fprintf(&b, " (op %d)", *e.Op)
	}
	return b.String()
}

// Is matches the response code; uncertain writes also match ErrCommitUncertain.
func (e *APIError) Is(target error) bool {
	code, ok := target.(ErrorCode)
	return ok && (code == e.Code || code == ErrCommitUncertain && e.Code == ErrWriteOutcomeUnknown)
}

// OpIndex returns the failing transaction op index, if the error names one.
func (e *APIError) OpIndex() (int, bool) {
	if e.Op == nil {
		return 0, false
	}
	return *e.Op, true
}

func parseAPIError(status int, h http.Header, body []byte) *APIError {
	e := &APIError{
		Status:     status,
		Header:     h.Clone(),
		Body:       append(json.RawMessage(nil), body...),
		RequestID:  h.Get("X-Request-ID"),
		ETag:       unquoteETag(h.Get("ETag")),
		HomeRegion: h.Get("X-TerraBase-Database-Home"),
		Regions:    strings.FieldsFunc(h.Get("X-TerraBase-Database-Regions"), func(r rune) bool { return r == ',' || r == ' ' }),
	}
	e.RetryAfter, e.retryAfterSet = parseRetryAfter(h)
	var env struct {
		Error json.RawMessage `json:"error"`
	}
	if decodeExact(body, &env) == nil && len(env.Error) > 0 {
		var d struct {
			Code    string             `json:"code"`
			Message string             `json:"message"`
			Op      *int               `json:"op"`
			Region  string             `json:"region"`
			Machine string             `json:"machine"`
			Details []ValidationDetail `json:"details"`
		}
		if decodeExact(env.Error, &d) == nil {
			e.Code, e.Message, e.Op = ErrorCode(d.Code), d.Message, d.Op
			e.Region, e.Machine = d.Region, d.Machine
			e.Details = d.Details
		}
	} else if decodeExact(body, &e.Message) == nil {
		// Management answers some errors with a bare JSON string.
		switch status {
		case http.StatusBadRequest:
			e.Code = ErrInvalidRequest
		case http.StatusForbidden:
			e.Code = ErrForbidden
		case http.StatusConflict:
			e.Code = ErrConflict
		}
	}
	if e.Code == "" {
		if status == http.StatusNotModified {
			e.Code = ErrNotModified
		} else {
			e.Code = ErrorCode("http_" + strconv.Itoa(status))
		}
	}
	return e
}

func unquoteETag(v string) string {
	v = strings.TrimPrefix(strings.TrimSpace(v), "W/")
	if len(v) >= 2 && v[0] == '"' && v[len(v)-1] == '"' {
		return v[1 : len(v)-1]
	}
	return v
}
