// Package sdk is the TerraScale Go SDK (terrascale.tech/sdk).
//
// It holds the hosted ts-auth and status clients (Client.TSAuth, Client.Status)
// and the schema and SQL builders; the TerraBase client is package v1.
package sdk

import (
	"bytes"
	"cmp"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"math"
	"net"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode"
)

// Public protocol limits (GOAL D-32). These limits are applied before a
// request is sent and while a response is read, so a peer cannot make the
// SDK allocate an unbounded buffer.
const (
	MaxRequestBodyBytes  int64 = 16 << 20
	MaxResponseBodyBytes int64 = 16 << 20
	MaxRequestTimeout          = 30 * time.Second
	MaxRetryAfter              = 30 * time.Second
)

// RegionCode is a TerraScale region identifier.
type RegionCode string

// Launch regions.
const (
	RegionGRU RegionCode = "GRU"
	RegionSJC RegionCode = "SJC"
)

// IsRegionCode reports whether s is a known region code (case-insensitive).
func IsRegionCode(s string) bool {
	switch RegionCode(strings.ToUpper(strings.TrimSpace(s))) {
	case RegionGRU, RegionSJC:
		return true
	default:
		return false
	}
}

// ParseRegionCode normalizes s to a canonical RegionCode or returns an error.
func ParseRegionCode(s string) (RegionCode, error) {
	code := RegionCode(strings.ToUpper(strings.TrimSpace(s)))
	if !IsRegionCode(string(code)) {
		return "", fmt.Errorf("sdk: region must be exactly one of GRU, SJC, got %q", s)
	}
	return code, nil
}

// DefaultAPIBaseURL is the fixed global public API origin.
const DefaultAPIBaseURL = "https://" + publicAPIHostname

// Config configures a Client. Hosts are fixed: every client uses the global
// API and auth origins unless a base URL is overridden for a dev or test
// deployment.
type Config struct {
	// APIBaseURL is the public API origin. Empty means DefaultAPIBaseURL.
	APIBaseURL string
	// TSAuthBaseURL is the ts-auth origin used by the TSAuth family. Empty
	// means the global auth origin, publicAuthBaseURL.
	TSAuthBaseURL string
	// StatusBaseURL overrides the public ts-status origin (default
	// https://status.terrascale.tech).
	StatusBaseURL string
	// HTTPClient defaults to one with a 30s timeout. The SDK copies it and
	// disables redirects; explicit 421 placement repair remains supported.
	HTTPClient *http.Client
}

// Client is the TerraScale SDK entry point.
type Client struct {
	cfg  Config
	http *http.Client

	TSAuth *TSAuthClient
	Status *StatusClient
}

// NewClient validates local configuration and constructs a Client;
// successful construction does not establish service readiness.
func NewClient(cfg Config) (*Client, error) {
	base := strings.TrimSpace(cfg.APIBaseURL)
	if base == "" {
		base = DefaultAPIBaseURL
	}
	base = strings.TrimSuffix(base, "/")
	u, err := url.Parse(base + "/")
	if err != nil {
		return nil, fmt.Errorf("sdk: invalid API base URL %q: %w", cfg.APIBaseURL, err)
	}
	if base != "" && ((u.Scheme != "http" && u.Scheme != "https") || u.Host == "") {
		return nil, fmt.Errorf("sdk: API base URL must be an absolute http or https URL")
	}
	if u.RawQuery != "" || u.Fragment != "" {
		return nil, fmt.Errorf("sdk: API base URL must not contain a query or fragment")
	}
	if u.User != nil {
		return nil, fmt.Errorf("sdk: API base URL must not contain userinfo")
	}
	if u.Scheme == "http" {
		host := strings.ToLower(u.Hostname())
		loopback := host == "localhost"
		if ip := net.ParseIP(host); ip != nil {
			loopback = ip.IsLoopback()
		}
		if !loopback {
			return nil, fmt.Errorf("sdk: API endpoints must use HTTPS, except loopback test transports")
		}
	}
	lowerHost := strings.ToLower(u.Hostname())
	if strings.Contains(lowerHost, "tbep") || strings.Contains(lowerHost, "terrabase") {
		return nil, fmt.Errorf("sdk: clients may target only the public API, not TBEP or TerraBase")
	}
	lowerPath := strings.ToLower(u.EscapedPath())
	if strings.Contains(lowerPath, "tbep") || strings.Contains(lowerPath, "terrabase") {
		return nil, fmt.Errorf("sdk: clients may target only the public API, not TBEP or TerraBase")
	}

	statusBase, err := statusResolveBase(cfg.StatusBaseURL)
	if err != nil {
		return nil, err
	}

	httpClient := cfg.HTTPClient
	if httpClient == nil {
		httpClient = &http.Client{Timeout: 30 * time.Second}
	}
	// Redirects must never forward credentials or replay writes.
	ownedHTTPClient := *httpClient
	ownedHTTPClient.CheckRedirect = func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }
	httpClient = &ownedHTTPClient

	c := &Client{cfg: Config{
		APIBaseURL:    base,
		TSAuthBaseURL: strings.TrimSpace(cfg.TSAuthBaseURL),
		StatusBaseURL: statusBase,
		HTTPClient:    httpClient,
	}, http: httpClient}
	c.TSAuth = &TSAuthClient{c: c}
	c.Status = &StatusClient{c: c}
	return c, nil
}

// APIError is a typed API failure.
type APIError struct {
	Route     string
	Method    string
	Path      string
	Status    int
	Code      string
	Message   string
	Fix       string
	RequestID string
	// Body contains decoded diagnostic fields only. Malformed responses and
	// unknown fields are discarded so credentials cannot survive as raw bodies.
	Body string
	// RetryAfter exposes the server-sent Retry-After header (delta-seconds
	// or HTTP-date) when present, uncapped; nil when absent or unparseable.
	RetryAfter *time.Duration
}

func (e *APIError) Error() string {
	return fmt.Sprintf("sdk: %s %s (%s): status %d code %s: %s",
		e.Method, e.Path, e.Route, e.Status, e.Code, e.Message)
}

// Retries are fixed and apply only to safe reads. Deterministic failures and
// ambiguous server failures are not transient retry candidates.
const maxRetryAttempts = 3
const retryBackoff = 250 * time.Millisecond

func retryableStatus(status int) bool {
	switch status {
	case http.StatusRequestTimeout, 429, http.StatusBadGateway, http.StatusServiceUnavailable, http.StatusGatewayTimeout, 520, 521, 522, 523, 524:
		return true
	}
	return false
}

func bearerAuthorization(value string) (string, error) {
	// Validate before trimming so a control character cannot be hidden by
	// normalization (for example, "\n" must not become an empty token).
	if strings.ContainsFunc(value, unicode.IsControl) {
		return "", fmt.Errorf("sdk: access token contains invalid control characters")
	}
	value = strings.TrimSpace(value)
	if value == "" {
		return "", nil
	}
	if strings.ContainsAny(value, "\r\n") {
		return "", fmt.Errorf("sdk: access token contains invalid control characters")
	}
	if strings.EqualFold(value, "Bearer") {
		return "", fmt.Errorf("sdk: bearer token cannot be empty")
	}
	if len(value) >= len("Bearer ") && strings.EqualFold(value[:len("Bearer ")], "Bearer ") {
		credential := strings.TrimSpace(value[len("Bearer "):])
		if credential == "" {
			return "", fmt.Errorf("sdk: bearer token cannot be empty")
		}
		if strings.ContainsFunc(credential, unicode.IsSpace) {
			return "", fmt.Errorf("sdk: bearer token contains whitespace")
		}
		return "Bearer " + credential, nil
	}
	if strings.ContainsFunc(value, unicode.IsSpace) {
		return "", fmt.Errorf("sdk: bearer token contains whitespace")
	}
	return "Bearer " + value, nil
}

func setBearerAuthorization(header http.Header, token string) error {
	authorization, err := bearerAuthorization(token)
	if err != nil {
		return err
	}
	if authorization != "" {
		header.Set("Authorization", authorization)
	}
	return nil
}

type httpResponseDecoder interface {
	decodeHTTPResponse(int, []byte) error
}

func decodeSpecialResponse(decoder httpResponseDecoder, status int, body []byte) error {
	err := decoder.decodeHTTPResponse(status, body)
	if err == nil {
		return nil
	}
	var oidcFailure *TSAuthOIDCError
	if errors.As(err, &oidcFailure) {
		// Keep the protocol failure category without a decoder cause that
		// might contain an offending response value or private key material.
		return oidcError(oidcFailure.Code, oidcFailure.Operation, oidcFailure.Uncertain, nil)
	}
	return errors.New("sdk: decode success body: JSON response does not match the expected shape")
}

func decodeSuccess(out any, body []byte, contentType string) error {
	if out == nil {
		return nil
	}
	if len(bytes.TrimSpace(body)) == 0 {
		return nil
	}
	if sp, ok := out.(*string); ok {
		*sp = string(body)
		return nil
	}
	trimmed := bytes.TrimSpace(body)
	if !strings.Contains(strings.ToLower(contentType), "json") &&
		!bytes.HasPrefix(trimmed, []byte("{")) && !bytes.HasPrefix(trimmed, []byte("[")) {
		return fmt.Errorf("sdk: non-JSON success body (content-type %q)", contentType)
	}
	if err := json.Unmarshal(body, out); err != nil {
		// A JSON type error can include the offending response value. Keep the
		// decode failure category without exposing tokens in decoder details.
		return errors.New("sdk: decode success body: JSON response does not match the expected shape")
	}
	return nil
}

func errorFromResponse(route, method, path string, status int, headers http.Header, body []byte) *APIError {
	apiErr := &APIError{
		Route:  route,
		Method: method,
		Path:   path,
		Status: status,
		Code:   defaultErrorCode(status),
	}
	var doc errorDocument
	if err := json.Unmarshal(body, &doc); err == nil {
		sourceCode, sourceMessage, sourceFix := doc.Code, doc.Message, doc.Fix
		var nested errorDetail
		nestedOK := len(doc.Error) > 0 && json.Unmarshal(doc.Error, &nested) == nil && (nested.Code != "" || nested.Message != "" || nested.Fix != "")
		var errorText string
		if !nestedOK {
			_ = json.Unmarshal(doc.Error, &errorText)
		}
		// Never retain an unmodeled error object or unknown response fields.
		doc.Error = nil
		if nestedOK {
			sourceCode, sourceMessage, sourceFix = nested.Code, nested.Message, nested.Fix
			doc.Error, _ = json.Marshal(nested)
		} else if errorText != "" {
			doc.Error, _ = json.Marshal(errorText)
		}
		if sourceCode != "" {
			apiErr.Code = sourceCode
		} else if !nestedOK {
			if v := cmp.Or(doc.Code, doc.ErrorCode, doc.Error_Code); v != "" {
				apiErr.Code = v
			}
		}
		if sourceMessage != "" {
			apiErr.Message = sourceMessage
		} else if !nestedOK {
			apiErr.Message = cmp.Or(doc.Message, errorText, doc.Detail, doc.Title)
		}
		apiErr.Fix = sourceFix
		apiErr.RequestID = cmp.Or(doc.RequestID, doc.Request_ID, doc.TraceID)
		if sanitized, err := json.Marshal(doc); err == nil {
			apiErr.Body = string(sanitized)
		}
	} else if json.Valid(body) {
		// Unmarshal still decodes correctly typed correlation fields when a
		// different field has the wrong type. Keep those without the raw body.
		apiErr.RequestID = cmp.Or(doc.RequestID, doc.Request_ID, doc.TraceID)
	}
	if apiErr.Message == "" {
		apiErr.Message = http.StatusText(status)
		if apiErr.Message == "" {
			apiErr.Message = "the TerraScale API request failed"
		}
	}
	if v := headers.Get("X-Request-ID"); v != "" && apiErr.RequestID == "" {
		apiErr.RequestID = v
	}
	apiErr.RetryAfter = retryAfterFromHeader(headers)
	return apiErr
}

func responseTooLargeError(route, method, path string, status int, headers http.Header) *APIError {
	apiErr := errorFromResponse(route, method, path, status, headers, nil)
	apiErr.Code = "response_too_large"
	apiErr.Message = fmt.Sprintf("response body exceeds %d bytes", MaxResponseBodyBytes)
	return apiErr
}

func responseReadError(route, method, path string, status int, headers http.Header) *APIError {
	apiErr := errorFromResponse(route, method, path, status, headers, nil)
	if status >= 200 && status < 300 {
		apiErr.Code = "response_read_failed"
	}
	// Reader errors and partial bodies can contain credentials. Expose the
	// observed response classification without either raw value.
	apiErr.Message = "could not read response body"
	return apiErr
}

// errorDocument deliberately models only bounded diagnostic fields. The
// response itself has already been capped by roundTrip before this decoder is
// called, and unknown fields are ignored for forward compatibility.
type errorDocument struct {
	Code             string          `json:"code,omitempty"`
	ErrorCode        string          `json:"errorCode,omitempty"`
	Error_Code       string          `json:"error_code,omitempty"`
	Message          string          `json:"message,omitempty"`
	Detail           string          `json:"detail,omitempty"`
	Title            string          `json:"title,omitempty"`
	RequestID        string          `json:"requestId,omitempty"`
	Request_ID       string          `json:"request_id,omitempty"`
	TraceID          string          `json:"traceId,omitempty"`
	Fix              string          `json:"fix,omitempty"`
	Error            json.RawMessage `json:"error,omitempty"`
	ErrorDescription string          `json:"error_description,omitempty"`
}

type errorDetail struct {
	Code    string `json:"code,omitempty"`
	Message string `json:"message,omitempty"`
	Fix     string `json:"fix,omitempty"`
}

func defaultErrorCode(status int) string {
	switch {
	case status == http.StatusUnauthorized:
		return "unauthenticated"
	case status == http.StatusForbidden:
		return "forbidden"
	case status == http.StatusNotFound:
		return "not_found"
	case status == http.StatusConflict:
		return "conflict"
	case status == http.StatusMisdirectedRequest:
		return "misdirected_region"
	case status == http.StatusTooManyRequests:
		return "rate_limited"
	case status >= 500:
		return "server_error"
	default:
		return "request_failed"
	}
}

func (c *Client) retryDelayFor(headers http.Header, attempt int) time.Duration {
	if headers != nil {
		if d, ok := parseRetryAfter(headers.Get("Retry-After")); ok {
			return d
		}
	}
	d := retryBackoff << attempt
	if d <= 0 {
		return retryBackoff
	}
	if d > MaxRetryAfter {
		return MaxRetryAfter
	}
	return d
}

// retryAfterFromHeader exposes the Retry-After the server sent (delta-seconds
// or HTTP-date) on APIError and retry scheduling. Nil when absent or unparseable.
func retryAfterFromHeader(headers http.Header) *time.Duration {
	value := strings.TrimSpace(headers.Get("Retry-After"))
	if value == "" {
		return nil
	}
	if secs, err := strconv.ParseUint(value, 10, 64); err == nil || isDecimal(value) {
		// An out-of-range delta is still a delta: clamp to the largest
		// representable duration instead of wrapping.
		if err != nil || secs > uint64(math.MaxInt64)/uint64(time.Second) {
			secs = uint64(math.MaxInt64 / int64(time.Second))
		}
		d := time.Duration(secs) * time.Second
		return &d
	}
	if t, err := http.ParseTime(value); err == nil {
		d := time.Until(t)
		if d < 0 {
			d = 0
		}
		return &d
	}
	return nil
}

func parseRetryAfter(value string) (time.Duration, bool) {
	duration := retryAfterFromHeader(http.Header{"Retry-After": {value}})
	if duration == nil {
		return 0, false
	}
	return *duration, true
}

func isDecimal(value string) bool {
	if value == "" {
		return false
	}
	for _, r := range value {
		if r < '0' || r > '9' {
			return false
		}
	}
	return true
}

func sleepContext(ctx context.Context, d time.Duration) error {
	if d <= 0 {
		return ctx.Err()
	}
	timer := time.NewTimer(d)
	defer timer.Stop()
	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}
