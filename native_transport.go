package sdk

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"mime"
	"net"
	"net/http"
	"net/url"
	"strings"
)

// CommitError retains conservative uncertainty after a mutation is dispatched.
// It never authorizes choosing a fresh identity or treating unknown as abort.
type CommitError struct {
	Uncertain bool
	Err       error
	Operation *PreparedTransaction
}

func (e *CommitError) Error() string {
	return fmt.Sprintf("sdk: native commit (uncertain=%t): %v", e.Uncertain, e.Err)
}
func (e *CommitError) Unwrap() error { return e.Err }

func validateNativeOrigin(raw string) (string, error) {
	if raw == "" {
		return "", nil
	}
	u, err := url.Parse(raw)
	if err != nil || u.Host == "" || u.User != nil || u.RawQuery != "" || u.ForceQuery || u.Fragment != "" || u.Opaque != "" || (u.Path != "" && u.Path != "/") || u.RawPath != "" {
		return "", fmt.Errorf("sdk: native data origin must be an absolute origin without credentials, path, query or fragment")
	}
	host := strings.TrimSuffix(strings.ToLower(u.Hostname()), ".")
	if host == "" {
		return "", fmt.Errorf("sdk: native data origin requires a hostname")
	}
	// net/http applies IDNA normalization to Unicode hostnames before dialing.
	// Require the ASCII form so the validated host is exactly the dial target.
	for _, char := range host {
		if char > 0x7f {
			return "", fmt.Errorf("sdk: native data origin requires an ASCII hostname")
		}
	}
	if u.Hostname() != host {
		return "", fmt.Errorf("sdk: native data origin requires a canonical hostname")
	}
	if host != "api.terrascale.tech" && strings.HasSuffix(host, ".terrascale.tech") {
		region, ok := strings.CutSuffix(host, ".api.terrascale.tech")
		if !ok || !validNativeDiscoveryRegion(strings.ToUpper(region)) {
			return "", fmt.Errorf("sdk: native regional origin requires <region>.api.terrascale.tech")
		}
	}
	if u.Scheme != "https" {
		ip := net.ParseIP(host)
		if u.Scheme != "http" || !(host == "localhost" || ip != nil && ip.IsLoopback()) {
			return "", fmt.Errorf("sdk: native data origin requires HTTPS except explicit loopback development")
		}
	}
	return strings.TrimSuffix(u.String(), "/"), nil
}

func freezeNative[T any](value T) (T, error) {
	var frozen T
	data, err := json.Marshal(value)
	if err != nil {
		return frozen, err
	}
	if err := decodeNativeJSON(data, &frozen); err != nil {
		return frozen, err
	}
	return frozen, nil
}

func (c *Client) doNative(ctx context.Context, scope DocumentScope, required nativeRequirements, method, path string, body, out any, mutation bool) error {
	if ctx == nil {
		return fmt.Errorf("sdk: native request context cannot be nil")
	}
	if err := ctx.Err(); err != nil {
		return err
	}
	if !strings.HasPrefix(path, "/v1/tenants/") {
		return fmt.Errorf("sdk: native path is outside UUID scoped /v1")
	}
	var data []byte
	if body != nil {
		var err error
		data, err = json.Marshal(body)
		if err != nil {
			return err
		}
		if len(data) > MaxNativeBodyBytes {
			return fmt.Errorf("sdk: native request byte limit")
		}
	}
	requestCtx, cancel := context.WithTimeout(ctx, c.cfg.RequestTimeout)
	defer cancel()
	credential := c.nativeToken()
	var response *http.Response
	var responseBody []byte
	attempted := make(map[string]bool)
	var admission nativeDiscoveryAdmission
	for {
		base := c.cfg.DataBaseURL
		if base == "" {
			var err error
			admission, err = c.nativeRouter.resolveHosted(requestCtx, scope, credential, required)
			if err != nil {
				return &RoutingError{Route: "native", Method: method, Path: path, Err: err}
			}
			base = admission.URL
		}
		if attempted[base] {
			return &RoutingError{Route: "native", Method: method, Path: path, Err: nativeRoutingFailure("regions_unavailable", nil)}
		}
		attempted[base] = true
		req, err := http.NewRequestWithContext(requestCtx, method, base+path, bytes.NewReader(data))
		if err != nil {
			return err
		}
		req.Header.Set("Accept", "application/json")
		if body != nil {
			req.Header.Set("Content-Type", "application/json")
		}
		if credential != "" {
			if err := setBearerAuthorization(req.Header, credential); err != nil {
				return err
			}
		}
		if c.cfg.DataBaseURL == "" {
			if err := c.nativeRouter.assertCurrent(admission); err != nil {
				return &RoutingError{Route: "native", Method: method, Path: path, Err: err}
			}
		}
		response, err = c.nativeHTTP.Do(req)
		regional := c.cfg.DataBaseURL == "" && !admission.Lease.Proxy
		if err != nil {
			if regional && requestCtx.Err() == nil {
				c.nativeRouter.fail(admission)
				if !mutation {
					continue
				}
			}
			return nativeDispatchError(mutation, true, err)
		}
		if regional && requestCtx.Err() == nil && (response.StatusCode == http.StatusMisdirectedRequest || response.StatusCode >= 500) {
			c.nativeRouter.fail(admission)
			if !mutation {
				response.Body.Close()
				if delay := retryAfterFromHeader(response.Header); delay != nil {
					if err := sleepContext(requestCtx, *delay); err != nil {
						return err
					}
				}
				continue
			}
		}
		if response.ContentLength > MaxNativeBodyBytes {
			response.Body.Close()
			return nativeDispatchError(mutation, true, fmt.Errorf("sdk: native response byte limit"))
		}
		responseBody, err = io.ReadAll(io.LimitReader(response.Body, MaxNativeBodyBytes+1))
		response.Body.Close()
		if err != nil {
			if regional && requestCtx.Err() == nil {
				c.nativeRouter.fail(admission)
				if !mutation && response.StatusCode == http.StatusOK {
					continue
				}
			}
			return nativeDispatchError(mutation, true, err)
		}
		if len(responseBody) > MaxNativeBodyBytes {
			return nativeDispatchError(mutation, true, fmt.Errorf("sdk: native response byte limit"))
		}
		break
	}
	if response.StatusCode != http.StatusOK {
		apiErr := errorFromResponse("native", method, path, response.StatusCode, response.Header, responseBody)
		uncertain := true
		// Only the producer's exact status/code pairs prove a retained rejection
		// or admission failure. Proxy errors and unexpected redirects do not.
		var envelope struct {
			Error struct {
				Code string `json:"code"`
			} `json:"error"`
		}
		if err := decodeNativeJSON(responseBody, &envelope); err == nil {
			apiErr.Code = envelope.Error.Code
			switch response.StatusCode {
			case http.StatusBadRequest:
				uncertain = apiErr.Code != "invalid_request"
			case http.StatusUnauthorized:
				uncertain = apiErr.Code != "invalid_key"
			case http.StatusForbidden:
				uncertain = apiErr.Code != "forbidden_scope"
			case http.StatusConflict:
				uncertain = apiErr.Code != "idempotency_conflict" && apiErr.Code != "schema_changed" && apiErr.Code != "authority_changed"
			case http.StatusPreconditionFailed:
				uncertain = apiErr.Code != "precondition_failed"
			case http.StatusServiceUnavailable:
				uncertain = apiErr.Code != "authority_unavailable"
			}
		}
		return nativeDispatchError(mutation, uncertain, apiErr)
	}
	mediaType, _, err := mime.ParseMediaType(response.Header.Get("Content-Type"))
	if err != nil || mediaType != "application/json" {
		return nativeDispatchError(mutation, true, fmt.Errorf("sdk: native success requires application/json"))
	}
	if err := decodeNativeJSON(responseBody, out); err != nil {
		return nativeDispatchError(mutation, true, err)
	}
	return nil
}

func (c *Client) nativeToken() string {
	if c.cfg.DataTokenProvider != nil {
		return c.cfg.DataTokenProvider()
	}
	return c.cfg.DataAccessToken
}
func nativeDispatchError(mutation, uncertain bool, err error) error {
	if mutation {
		return &CommitError{Uncertain: uncertain, Err: err}
	}
	return err
}

// PreparedTransaction owns the exact dispatched operation, bound to its
// originating client. Recovery uses these immutable bytes and identity rather
// than a caller's mutable request. No method automatically retries a mutation.
type PreparedTransaction struct {
	documents *DocumentsClient
	scope     DocumentScope
	request   CommitRequest
	payload   []byte
}

func (d *DocumentsClient) PrepareTransaction(scope DocumentScope, request CommitRequest) (*PreparedTransaction, error) {
	if d == nil || d.c == nil {
		return nil, fmt.Errorf("sdk: uninitialized documents client")
	}
	if err := request.validate(scope); err != nil {
		return nil, err
	}
	frozen, err := freezeNative(request)
	if err != nil {
		return nil, err
	}
	if err := frozen.validate(scope); err != nil {
		return nil, err
	}
	payload, err := json.Marshal(frozen)
	if err != nil {
		return nil, err
	}
	if len(payload) > MaxNativeBodyBytes {
		return nil, fmt.Errorf("sdk: native transaction request byte limit")
	}
	return &PreparedTransaction{documents: d, scope: scope, request: frozen, payload: payload}, nil
}
func (p *PreparedTransaction) Scope() DocumentScope            { return p.scope }
func (p *PreparedTransaction) Request() (CommitRequest, error) { return freezeNative(p.request) }
func (p *PreparedTransaction) StatusRequest() (TransactionStatusRequest, error) {
	return freezeNative(p.request.StatusRequest())
}
func (p *PreparedTransaction) Commit(ctx context.Context) (*CommitResponse, error) {
	if p == nil || p.documents == nil || p.documents.c == nil {
		return nil, fmt.Errorf("sdk: uninitialized prepared transaction")
	}
	if p.request.Authority.Mode == AuthoritySingleWriter {
		return nil, &RoutingError{Route: "native.commit", Method: http.MethodPost, Path: p.scope.path() + "/transactions", Err: fmt.Errorf("authenticated single writer admission and fence are unavailable")}
	}
	var receipt CommitResponse
	required := nativeWrite
	if len(p.request.Writes) > 1 {
		required |= nativeAtomic
	}
	for _, write := range p.request.Writes {
		if write.Condition.Kind != ConditionNone {
			required |= nativeConditional
		}
	}
	if err := p.documents.c.doNative(ctx, p.scope, required, http.MethodPost, p.scope.path()+"/transactions", json.RawMessage(p.payload), &receipt, true); err != nil {
		var commitErr *CommitError
		if errors.As(err, &commitErr) {
			commitErr.Operation = p
		}
		return nil, err
	}
	if err := receipt.validate(p.scope, p.request.StatusRequest()); err != nil {
		return nil, &CommitError{Uncertain: true, Err: err, Operation: p}
	}
	return &receipt, nil
}

// Retry explicitly submits the same frozen identity and effects on the same
// client. Call Status first when the original outcome is uncertain.
func (p *PreparedTransaction) Retry(ctx context.Context) (*CommitResponse, error) {
	return p.Commit(ctx)
}
func (p *PreparedTransaction) Status(ctx context.Context) (*TransactionStatusResponse, error) {
	if p == nil || p.documents == nil {
		return nil, fmt.Errorf("sdk: uninitialized prepared transaction")
	}
	return p.documents.TransactionStatus(ctx, p.scope, p.request.StatusRequest())
}
