package sdk_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"

	sdk "terrascale.tech/sdk"
)

const discoveryErrorContractLimit = 64 << 10

type discoveryErrorContractBody struct {
	reader    io.Reader
	readError error
	bytesRead int
	closed    bool
}

func (body *discoveryErrorContractBody) Read(value []byte) (int, error) {
	n, err := body.reader.Read(value)
	body.bytesRead += n
	if errors.Is(err, io.EOF) && body.readError != nil {
		return n, body.readError
	}
	return n, err
}

func (body *discoveryErrorContractBody) Close() error {
	body.closed = true
	return nil
}

func TestNativePublicClientDiscoveryHTTPErrorPreservesStatus(t *testing.T) {
	for _, status := range []struct {
		name, code, fallback string
		value                int
	}{
		{"forbidden", "discovery_forbidden", "forbidden", http.StatusForbidden},
		{"not_found", "discovery_unavailable", "not_found", http.StatusNotFound},
		{"unavailable", "discovery_unavailable", "server_error", http.StatusServiceUnavailable},
	} {
		for _, variant := range []string{"complete_json", "json_at_limit", "oversized_declared", "oversized_streamed", "read_failure", "malformed"} {
			for _, operation := range routingContractOperations(t) {
				t.Run(status.name+"/"+variant+"/"+operation.name, func(t *testing.T) {
					credential := routingContractKey()
					value := fmt.Sprintf(`{"error":{"code":%q},"unknown":%q}`, status.code, credential)
					contentLength := int64(-1)
					wantCode := status.fallback
					var readError error
					switch variant {
					case "complete_json":
						wantCode = status.code
					case "json_at_limit":
						value += strings.Repeat(" ", discoveryErrorContractLimit-len(value))
						contentLength = int64(len(value))
						wantCode = status.code
					case "oversized_declared", "oversized_streamed":
						value += strings.Repeat(" ", discoveryErrorContractLimit)
						if variant == "oversized_declared" {
							contentLength = int64(len(value))
						}
					case "read_failure":
						// Even a complete-looking prefix is untrusted if reading fails.
						readError = errors.New("read failed with " + credential)
					case "malformed":
						value = "gateway response with " + credential
					}
					body := &discoveryErrorContractBody{reader: strings.NewReader(value), readError: readError}
					atlasCalls, dataCalls := 0, 0
					client, err := sdk.NewClient(sdk.Config{
						DataAccessToken: credential,
						HTTPClient: &http.Client{Transport: routingContractTransport(func(request *http.Request) (*http.Response, error) {
							if request.URL.Host != "atlas.terrascale.tech" {
								dataCalls++
								return nil, errors.New("unexpected native dispatch")
							}
							atlasCalls++
							if request.Header.Get("Authorization") != "Bearer "+credential {
								t.Fatal("discovery changed credential")
							}
							return &http.Response{
								StatusCode: status.value, ContentLength: contentLength, Body: body,
								Header: http.Header{"Content-Type": {"text/html"}, "X-Request-Id": {"atlas-denial"}, "Retry-After": {"2"}},
							}, nil
						})},
					})
					if err != nil {
						t.Fatal(err)
					}
					err = operation.call(t.Context(), client)
					var routingError *sdk.RoutingError
					var apiError *sdk.APIError
					if !errors.As(err, &routingError) || !errors.As(err, &apiError) || atlasCalls != 1 || dataCalls != 0 {
						t.Fatalf("denial classification or dispatch changed: Atlas=%d native=%d", atlasCalls, dataCalls)
					}
					var localError *sdk.NativeRoutingError
					var commitError *sdk.CommitError
					if errors.As(err, &localError) || errors.As(err, &commitError) {
						t.Fatal("pre-dispatch Atlas denial became a local error or uncertain commit")
					}
					if apiError.Status != status.value || apiError.Code != wantCode || apiError.Route != "native.discovery" || apiError.Method != http.MethodGet || apiError.Path != "/api/v1/discovery" {
						t.Fatal("actual Atlas status or bounded diagnostic classification lost")
					}
					if apiError.RequestID != "atlas-denial" || apiError.RetryAfter == nil || *apiError.RetryAfter != 2*time.Second {
						t.Fatal("Atlas diagnostic headers lost")
					}
					if !body.closed || body.bytesRead > discoveryErrorContractLimit+1 || variant == "oversized_declared" && body.bytesRead != 0 {
						t.Fatalf("body disposal or bound changed: closed=%t read=%d", body.closed, body.bytesRead)
					}
					if wantCode == status.fallback && apiError.Body != "" {
						t.Fatal("unsafe error body retained")
					}
					diagnostics := fmt.Sprint(err) + apiError.Code + apiError.Message + apiError.Fix + apiError.RequestID + apiError.Placement + apiError.Body
					if strings.Contains(diagnostics, credential) {
						t.Fatal("body or reader credential survived diagnostics")
					}
				})
			}
		}
	}
}

func TestNativePublicClientDiscoveryDenialDiscardsCredentialEcho(t *testing.T) {
	for _, status := range []struct {
		name, code, fallback string
		value                int
	}{
		{"forbidden", "discovery_forbidden", "forbidden", http.StatusForbidden},
		{"not_found", "discovery_unavailable", "not_found", http.StatusNotFound},
		{"unavailable", "discovery_unavailable", "server_error", http.StatusServiceUnavailable},
	} {
		for _, field := range []string{
			"nested_code", "nested_message", "nested_fix", "encoded_nested_message",
			"top_code", "top_errorCode", "top_error_code", "top_message", "top_fix", "top_detail", "top_title",
			"top_requestId", "top_request_id", "top_traceId", "top_error_description", "error_string",
			"header_request_id", "header_placement", "header_retry_after",
		} {
			for _, operation := range routingContractOperations(t) {
				if operation.name != "read" && operation.name != "commit" {
					continue
				}
				t.Run(status.name+"/"+field+"/"+operation.name, func(t *testing.T) {
					credential := routingContractKey()
					echo := "Bearer " + credential
					nested := map[string]string{"code": status.code, "message": "producer text", "fix": "producer guidance"}
					envelope := map[string]any{"error": nested}
					headers := http.Header{"Content-Type": {"application/json"}, "X-Request-Id": {"atlas-denial"}, "Retry-After": {"2"}}
					wantCode, wantRequestID := status.code, "atlas-denial"
					wantRetryAfter := true
					switch field {
					case "nested_code":
						nested["code"] = echo
						wantCode = status.fallback
					case "nested_message", "encoded_nested_message":
						nested["message"] = echo
					case "nested_fix":
						nested["fix"] = echo
					case "error_string":
						envelope["error"] = echo
						wantCode = status.fallback
					case "header_request_id":
						headers.Set("X-Request-ID", echo)
						wantRequestID = ""
					case "header_placement":
						headers.Set("X-TerraScale-Placement", echo)
					case "header_retry_after":
						headers.Set("Retry-After", echo)
						wantRetryAfter = false
					default:
						envelope[strings.TrimPrefix(field, "top_")] = echo
					}
					value := string(routingContractJSON(t, envelope))
					if field == "encoded_nested_message" {
						value = strings.ReplaceAll(value, "tbk1.", `t\u0062k1.`)
						var decoded struct {
							Error struct {
								Message string `json:"message"`
							} `json:"error"`
						}
						if err := json.Unmarshal([]byte(value), &decoded); err != nil || decoded.Error.Message != echo {
							t.Fatal("fixture did not encode the actual credential in complete JSON")
						}
					}
					body := &discoveryErrorContractBody{reader: strings.NewReader(value)}
					atlasCalls, dataCalls := 0, 0
					client, err := sdk.NewClient(sdk.Config{
						DataAccessToken: credential,
						HTTPClient: &http.Client{Transport: routingContractTransport(func(request *http.Request) (*http.Response, error) {
							if request.URL.Host != "atlas.terrascale.tech" {
								dataCalls++
								return nil, errors.New("unexpected native dispatch")
							}
							atlasCalls++
							if request.Header.Get("Authorization") != "Bearer "+credential {
								t.Fatal("discovery changed credential")
							}
							return &http.Response{StatusCode: status.value, ContentLength: int64(len(value)), Header: headers, Body: body}, nil
						})},
					})
					if err != nil {
						t.Fatal(err)
					}
					err = operation.call(t.Context(), client)
					var routingError *sdk.RoutingError
					var apiError *sdk.APIError
					if !errors.As(err, &routingError) || !errors.As(err, &apiError) || atlasCalls != 1 || dataCalls != 0 {
						t.Fatalf("credential-bearing Atlas denial changed dispatch: Atlas=%d native=%d", atlasCalls, dataCalls)
					}
					var localError *sdk.NativeRoutingError
					var commitError *sdk.CommitError
					if errors.As(err, &localError) || errors.As(err, &commitError) {
						t.Fatal("pre-dispatch denial became a local error or uncertain commit")
					}
					if apiError.Status != status.value || apiError.Code != wantCode || apiError.RequestID != wantRequestID {
						t.Fatal("actual status or safe recognized metadata lost")
					}
					if apiError.Message != http.StatusText(status.value) || apiError.Body != "" || apiError.Fix != "" || apiError.Placement != "" {
						t.Fatal("arbitrary producer diagnostic text retained")
					}
					if wantRetryAfter {
						if apiError.RetryAfter == nil || *apiError.RetryAfter != 2*time.Second {
							t.Fatal("safe Retry-After lost")
						}
					} else if apiError.RetryAfter != nil {
						t.Fatal("credential-bearing Retry-After accepted")
					}
					if !body.closed || body.bytesRead != len(value) || body.bytesRead > discoveryErrorContractLimit {
						t.Fatal("complete bounded denial body was not read and closed")
					}
					diagnostics := fmt.Sprint(err, apiError) + apiError.Code + apiError.Message + apiError.Fix + apiError.RequestID + apiError.Placement + apiError.Body
					if strings.Contains(diagnostics, credential) {
						t.Fatal("modeled credential echo survived Atlas denial diagnostics")
					}
				})
			}
		}
	}
}

func TestNativePublicClientDiscoverySharesOriginalDeadline(t *testing.T) {
	for _, name := range []string{"configured_budget", "shorter_caller_deadline", "discovery_budget_above_three_seconds"} {
		t.Run(name, func(t *testing.T) {
			ctx := t.Context()
			requestTimeout := 300 * time.Millisecond
			var callerDeadline time.Time
			if name == "shorter_caller_deadline" {
				requestTimeout = sdk.MaxRequestTimeout
				var cancel context.CancelFunc
				ctx, cancel = context.WithTimeout(ctx, 300*time.Millisecond)
				defer cancel()
				callerDeadline, _ = ctx.Deadline()
			} else if name == "discovery_budget_above_three_seconds" {
				requestTimeout = 5 * time.Second
			}
			document := routingContractJSON(t, routingContractDiscovery(t))
			var discoveryDeadline time.Time
			var remainingAfterDiscovery time.Duration
			atlasCalls, dataCalls := 0, 0
			client, err := sdk.NewClient(sdk.Config{
				DataAccessToken: routingContractKey(), RequestTimeout: requestTimeout,
				HTTPClient: &http.Client{Transport: routingContractTransport(func(request *http.Request) (*http.Response, error) {
					deadline, ok := request.Context().Deadline()
					if !ok {
						t.Fatal("native operation has no deadline")
					}
					if request.URL.Host == "atlas.terrascale.tech" {
						atlasCalls++
						initialRemaining := time.Until(deadline)
						discoveryDeadline = deadline
						if !callerDeadline.IsZero() && !deadline.Equal(callerDeadline) {
							t.Fatal("shorter caller deadline changed during discovery")
						}
						if name == "discovery_budget_above_three_seconds" {
							if initialRemaining < 4*time.Second {
								t.Fatal("discovery introduced a separate three-second cap")
							}
						} else {
							timer := time.NewTimer(initialRemaining / 2)
							defer timer.Stop()
							select {
							case <-timer.C:
							case <-request.Context().Done():
								t.Fatal("discovery exhausted the request before the fixture returned")
							}
							remainingAfterDiscovery = time.Until(deadline)
							if remainingAfterDiscovery >= initialRemaining || remainingAfterDiscovery <= 0 {
								t.Fatal("fixture did not consume part of the discovery budget")
							}
						}
						return routingContractResponse(http.StatusOK, document), nil
					}
					dataCalls++
					if request.URL.Host != "gru.api.terrascale.tech" || !deadline.Equal(discoveryDeadline) {
						t.Fatal("native dispatch changed the admitted origin or renewed the deadline")
					}
					if name == "discovery_budget_above_three_seconds" {
						return routingContractResponse(http.StatusOK, []byte(`{"version":1,"state":"absent","state_version":null,"context":[],"siblings":[]}`)), nil
					}
					if time.Until(deadline) > remainingAfterDiscovery {
						t.Fatal("native dispatch regained the time already spent on discovery")
					}
					<-request.Context().Done()
					return nil, request.Context().Err()
				})},
			})
			if err != nil {
				t.Fatal(err)
			}
			_, err = client.Documents.Get(ctx, routingContractScope, routingContractCollection, routingContractDocument)
			if name == "discovery_budget_above_three_seconds" {
				if err != nil {
					t.Fatal(err)
				}
			} else if !errors.Is(err, context.DeadlineExceeded) {
				t.Fatal("original native deadline cause lost")
			}
			if atlasCalls != 1 || dataCalls != 1 {
				t.Fatalf("native operation replayed: Atlas=%d native=%d", atlasCalls, dataCalls)
			}
		})
	}
}
