package v1

import (
	"encoding/json"
	"fmt"
	"math"
	"net/http"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/sdk/internal/httpx"
)

type ErrorCode string

func (c ErrorCode) Error() string { return "terrabase: " + string(c) }

const (
	ErrVersionMismatch      ErrorCode = "version_mismatch"
	ErrPreconditionRequired ErrorCode = "precondition_required"
	ErrInvalidRules         ErrorCode = "invalid_rules"
	ErrInvalidRequest       ErrorCode = "invalid_request"
	ErrInvalidCursor        ErrorCode = "invalid_cursor"
	ErrInvalidKey           ErrorCode = "invalid_key"
	ErrKeyRevoked           ErrorCode = "key_revoked"
	ErrKeyExpired           ErrorCode = "key_expired"
	ErrAuthUnavailable      ErrorCode = "auth_unavailable"
	ErrForbiddenScope       ErrorCode = "forbidden_scope"
	ErrNotFound             ErrorCode = "not_found"
	ErrUnknownDatabase      ErrorCode = "unknown_database"
	ErrUnknownRoute         ErrorCode = "unknown_route"
	ErrMethodNotAllowed     ErrorCode = "method_not_allowed"
	ErrNameTaken            ErrorCode = "name_taken"
	ErrPreconditionFailed   ErrorCode = "precondition_failed"
	ErrPayloadTooLarge      ErrorCode = "payload_too_large"
	ErrUnsupportedMediaType ErrorCode = "unsupported_media_type"
	ErrRateLimited          ErrorCode = "rate_limited"
	ErrTenantBusy           ErrorCode = "tenant_busy"
	ErrInternalError        ErrorCode = "internal_error"
	ErrCapacity             ErrorCode = "capacity"
	ErrStorageUnavailable   ErrorCode = "storage_unavailable"
	ErrStoredCorruption     ErrorCode = "stored_corruption"
	ErrCommitUncertain      ErrorCode = "commit_uncertain"
	ErrWriteOutcomeUnknown  ErrorCode = "write_outcome_unknown"
	ErrDependenciesUnavail  ErrorCode = "dependencies_unavailable"
	ErrCatalogStale         ErrorCode = "catalog_stale"
	ErrCopyFaulted          ErrorCode = "copy_faulted"
	ErrQuotaExceeded        ErrorCode = "quota_exceeded"
	ErrRequestTimeout       ErrorCode = "request_timeout"
	ErrWrongRegion          ErrorCode = "wrong_region"
	ErrReplicaNotReady      ErrorCode = "replica_not_ready"
	ErrMoveInProgress       ErrorCode = "move_in_progress"
	ErrMoveUnresolved       ErrorCode = "move_unresolved"
)

const ErrInRegionQuorumUnconfirmed ErrorCode = "in_region_quorum_unconfirmed"

const (
	ErrForbidden ErrorCode = "forbidden"
	ErrConflict  ErrorCode = "conflict"
)

type APIError struct {
	Status     int
	Header     http.Header
	Code       ErrorCode
	Message    string
	Problems   []RulesProblem
	RetryAfter time.Duration
	Body       json.RawMessage
	RequestID  string
	Regions    []string
	Region     string
	Machine    string

	retryAfterSet bool
	cause         error
}

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

func (e *APIError) Is(target error) bool {
	code, ok := target.(ErrorCode)
	return ok && (code == e.Code || code == ErrCommitUncertain && e.Code.commitUncertain())
}

func (c ErrorCode) commitUncertain() bool {
	switch c {
	case ErrCommitUncertain, ErrWriteOutcomeUnknown, ErrInRegionQuorumUnconfirmed:
		return true
	}
	return false
}

func parseAPIError(status int, h http.Header, body []byte) *APIError {
	e := &APIError{
		Status:    status,
		Header:    h.Clone(),
		Body:      append(json.RawMessage(nil), body...),
		RequestID: h.Get("X-Request-ID"),
		Regions:   strings.FieldsFunc(h.Get("X-TerraBase-Database-Regions"), func(r rune) bool { return r == ',' || r == ' ' }),
	}
	e.RetryAfter, e.retryAfterSet = httpx.RetryAfter(h.Get("Retry-After"), math.MaxInt64)
	var env struct {
		Error json.RawMessage `json:"error"`
	}
	if decodeExact(body, &env) == nil && len(env.Error) > 0 {
		var d struct {
			Code    string          `json:"code"`
			Message string          `json:"message"`
			Region  string          `json:"region"`
			Machine string          `json:"machine"`
			Details json.RawMessage `json:"details"`
		}
		if decodeExact(env.Error, &d) == nil {
			e.Code, e.Message = ErrorCode(d.Code), d.Message
			e.Region, e.Machine = d.Region, d.Machine
			if e.Code == ErrInvalidRules {
				var details struct {
					Problems []RulesProblem `json:"problems"`
				}
				if decodeExact(d.Details, &details) == nil {
					e.Problems = details.Problems
				}
			}
		}
	} else if decodeExact(body, &e.Message) == nil {
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
