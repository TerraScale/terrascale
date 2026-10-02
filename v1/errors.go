package v1

import (
	"encoding/json"
	"errors"
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

// Error codes from api-v1.md and control-plane-v1.md.
const (
	ErrNotModified            ErrorCode = "not_modified" // 304; synthesized, the response has no body
	ErrInvalidRequest         ErrorCode = "invalid_request"
	ErrInvalidQuery           ErrorCode = "invalid_query"
	ErrInvalidCursor          ErrorCode = "invalid_cursor"
	ErrInvalidPatch           ErrorCode = "invalid_patch"
	ErrTTLInvalid             ErrorCode = "ttl_invalid"
	ErrSortRequiresIndex      ErrorCode = "sort_requires_index"
	ErrInvalidKey             ErrorCode = "invalid_key"
	ErrInvalidToken           ErrorCode = "invalid_token" // shape token rejected by the origin or edge
	ErrForbiddenScope         ErrorCode = "forbidden_scope"
	ErrNotFound               ErrorCode = "not_found"
	ErrUnknownDatabase        ErrorCode = "unknown_database"
	ErrUnknownCollection      ErrorCode = "unknown_collection"
	ErrUnknownRoute           ErrorCode = "unknown_route"
	ErrMethodNotAllowed       ErrorCode = "method_not_allowed"
	ErrConflictUnresolved     ErrorCode = "conflict_unresolved"
	ErrUniqueViolation        ErrorCode = "unique_violation"
	ErrPatchConflict          ErrorCode = "patch_conflict"
	ErrIndexLimitExceeded     ErrorCode = "index_limit_exceeded"
	ErrIdempotencyConflict    ErrorCode = "idempotency_conflict"
	ErrIdempotencyExpired     ErrorCode = "idempotency_expired"
	ErrSchemaChanged          ErrorCode = "schema_changed"
	ErrMustRefetch            ErrorCode = "must_refetch"
	ErrPreconditionFailed     ErrorCode = "precondition_failed"
	ErrPayloadTooLarge        ErrorCode = "payload_too_large"
	ErrTransactionTooLarge    ErrorCode = "transaction_too_large"
	ErrUnsupportedMediaType   ErrorCode = "unsupported_media_type"
	ErrHomeRegionRequired     ErrorCode = "home_region_required"
	ErrRateLimited            ErrorCode = "rate_limited"
	ErrUpstreamUnavailable    ErrorCode = "upstream_unavailable" // edge only
	ErrCapacity               ErrorCode = "capacity"
	ErrStorageUnavailable     ErrorCode = "storage_unavailable"
	ErrStoredCorruption       ErrorCode = "stored_corruption"
	ErrCommitUncertain        ErrorCode = "commit_uncertain"
	ErrDependenciesUnavail    ErrorCode = "dependencies_unavailable"
	ErrNameTaken              ErrorCode = "name_taken"
	ErrIndexProtected         ErrorCode = "index_protected"
	ErrInvalidSchema          ErrorCode = "invalid_schema"
	ErrLimitExceeded          ErrorCode = "limit_exceeded"
	ErrIdempotencyKeyRequired ErrorCode = "idempotency_key_required"
)

// APIError is a non-2xx TerraBase response.
type APIError struct {
	// Status is the HTTP status code.
	Status int
	// Code is error.code, or a synthesized "http_<status>" when the body
	// carried none.
	Code    ErrorCode
	Message string
	// Op is the failing transaction op index, when the error names one.
	Op *int
	// Constraint is the unique constraint name for unique_violation.
	Constraint string
	// Handle is the new shape handle for must_refetch.
	Handle string
	// RetryAfter is the parsed Retry-After header, if any.
	RetryAfter time.Duration
	// ETag is the unquoted ETag of the response; a 404 for a deleted document
	// carries the tombstone's version so it can be recreated with IfMatch.
	ETag string
	// Body is the raw error object, for route-specific members such as
	// suggested_index.
	Body json.RawMessage

	retryAfterSet bool
}

func (e *APIError) Error() string {
	var b strings.Builder
	fmt.Fprintf(&b, "terrabase: %d %s", e.Status, e.Code)
	if e.Message != "" {
		b.WriteString(": " + e.Message)
	}
	if e.Op != nil {
		fmt.Fprintf(&b, " (op %d)", *e.Op)
	}
	if e.Constraint != "" {
		fmt.Fprintf(&b, " (constraint %s)", e.Constraint)
	}
	return b.String()
}

// Is reports whether target is the ErrorCode carried by e.
func (e *APIError) Is(target error) bool {
	code, ok := target.(ErrorCode)
	return ok && code == e.Code
}

// OpIndex returns the failing transaction op index, if the error names one.
func (e *APIError) OpIndex() (int, bool) {
	if e.Op == nil {
		return 0, false
	}
	return *e.Op, true
}

func parseAPIError(status int, h http.Header, body []byte) *APIError {
	e := &APIError{Status: status, ETag: unquoteETag(h.Get("ETag"))}
	e.RetryAfter, e.retryAfterSet = parseRetryAfter(h)
	var env struct {
		Error json.RawMessage `json:"error"`
	}
	if decodeExact(body, &env) == nil && len(env.Error) > 0 {
		var d struct {
			Code       string `json:"code"`
			Message    string `json:"message"`
			Op         *int   `json:"op"`
			Constraint string `json:"constraint"`
			Handle     string `json:"handle"`
		}
		if decodeExact(env.Error, &d) == nil {
			e.Code, e.Message, e.Op, e.Constraint, e.Handle = ErrorCode(d.Code), d.Message, d.Op, d.Constraint, d.Handle
			e.Body = env.Error
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

// ErrSubscriptionClosed is returned by Subscription methods after Close.
var ErrSubscriptionClosed = errors.New("terrabase: subscription closed")
