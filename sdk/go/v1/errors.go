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
	ErrInvalidRequest       ErrorCode = "invalid_request"
	ErrInvalidCursor        ErrorCode = "invalid_cursor"
	ErrInvalidKey           ErrorCode = "invalid_key"      // 401: a missing or wrong API key or service credential
	ErrKeyRevoked           ErrorCode = "key_revoked"      // 401: permanent; replace the key
	ErrKeyExpired           ErrorCode = "key_expired"      // 401: permanent; replace the key
	ErrAuthUnavailable      ErrorCode = "auth_unavailable" // 503: retry with backoff
	ErrForbiddenScope       ErrorCode = "forbidden_scope"
	ErrNotFound             ErrorCode = "not_found"
	ErrUnknownDatabase      ErrorCode = "unknown_database"
	ErrUnknownRoute         ErrorCode = "unknown_route" // also every Management 404: an unknown id or an organization the user is not a member of
	ErrMethodNotAllowed     ErrorCode = "method_not_allowed"
	ErrNameTaken            ErrorCode = "name_taken"
	ErrPreconditionFailed   ErrorCode = "precondition_failed"
	ErrPayloadTooLarge      ErrorCode = "payload_too_large"
	ErrUnsupportedMediaType ErrorCode = "unsupported_media_type"
	ErrRateLimited          ErrorCode = "rate_limited"
	ErrTenantBusy           ErrorCode = "tenant_busy"    // 429: refused at admission, before execution; retry after Retry-After
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
	ErrCopyFaulted    ErrorCode = "copy_faulted"
	ErrQuotaExceeded  ErrorCode = "quota_exceeded"  // 429
	ErrRequestTimeout ErrorCode = "request_timeout" // 408
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
	// RetryAfter is the parsed Retry-After header, if any.
	RetryAfter time.Duration
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
	if e.RequestID != "" {
		fmt.Fprintf(&b, " [request %s]", e.RequestID)
	}
	return b.String()
}

// Is matches the response code; uncertain writes also match ErrCommitUncertain.
func (e *APIError) Is(target error) bool {
	code, ok := target.(ErrorCode)
	return ok && (code == e.Code || code == ErrCommitUncertain && e.Code == ErrWriteOutcomeUnknown)
}

func parseAPIError(status int, h http.Header, body []byte) *APIError {
	e := &APIError{
		Status:     status,
		Header:     h.Clone(),
		Body:       append(json.RawMessage(nil), body...),
		RequestID:  h.Get("X-Request-ID"),
		HomeRegion: h.Get("X-TerraBase-Database-Home"),
		Regions:    strings.FieldsFunc(h.Get("X-TerraBase-Database-Regions"), func(r rune) bool { return r == ',' || r == ' ' }),
	}
	e.RetryAfter, e.retryAfterSet = parseRetryAfter(h)
	var env struct {
		Error json.RawMessage `json:"error"`
	}
	if decodeExact(body, &env) == nil && len(env.Error) > 0 {
		var d struct {
			Code    string `json:"code"`
			Message string `json:"message"`
			Region  string `json:"region"`
			Machine string `json:"machine"`
		}
		if decodeExact(env.Error, &d) == nil {
			e.Code, e.Message = ErrorCode(d.Code), d.Message
			e.Region, e.Machine = d.Region, d.Machine
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
		e.Code = ErrorCode("http_" + strconv.Itoa(status))
	}
	return e
}
