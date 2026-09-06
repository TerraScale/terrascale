package sdk

import (
	"bytes"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"reflect"
	"strings"
	"testing"
)

type producerHTTPResponse struct {
	Status    int             `json:"status"`
	MediaType string          `json:"mediaType"`
	Body      json.RawMessage `json:"body"`
}

// These cases originate in TerraBase's versioned bundle, independently of the
// Go request builders. They qualify the shared REST pipeline; the per-operation
// tests separately exercise the public typed methods.
func TestProducerHTTPTransportCorpus(t *testing.T) {
	var corpus struct {
		OpenAPISHA256 string `json:"openapiSha256"`
		Operations    []struct {
			ID      string `json:"operationId"`
			Request struct {
				Method  string         `json:"method"`
				Path    string         `json:"path"`
				Headers map[string]any `json:"headers"`
				Query   map[string]any `json:"query"`
				Body    *struct {
					Value json.RawMessage `json:"value"`
				} `json:"body"`
			} `json:"request"`
			Responses []producerHTTPResponse `json:"responses"`
			Errors    []producerHTTPResponse `json:"errors"`
		} `json:"operations"`
	}
	readPinnedJSON(t, "conformance/http.json", &corpus)
	openapi := readPinnedJSON(t, "openapi.json", &struct{}{})
	if sum := sha256.Sum256(openapi); corpus.OpenAPISHA256 != hex.EncodeToString(sum[:]) {
		t.Fatal("HTTP corpus does not identify the pinned producer OpenAPI")
	}
	if len(corpus.Operations) != len(ContractOperations) {
		t.Fatalf("HTTP corpus covers %d operations, pinned API has %d", len(corpus.Operations), len(ContractOperations))
	}
	for _, operation := range corpus.Operations {
		for _, response := range append(operation.Responses, operation.Errors...) {
			t.Run(fmt.Sprintf("%s/%d", operation.ID, response.Status), func(t *testing.T) {
				query := url.Values{}
				for name, value := range operation.Request.Query {
					query.Set(name, fmt.Sprint(value))
				}
				var requestBody any
				if operation.Request.Body != nil {
					requestBody = operation.Request.Body.Value
				}
				wireBody := []byte(response.Body)
				if response.MediaType == "application/octet-stream" {
					var raw string
					if err := json.Unmarshal(response.Body, &raw); err != nil {
						t.Fatal(err)
					}
					wireBody = []byte(raw)
				}
				calls := 0
				transport := pinningRoundTripperFunc(func(request *http.Request) (*http.Response, error) {
					calls++
					if request.Method != operation.Request.Method || request.URL.EscapedPath() != operation.Request.Path || request.URL.RawQuery != query.Encode() {
						t.Fatalf("request diverges from producer: %s %s", request.Method, request.URL)
					}
					for name, expected := range operation.Request.Headers {
						if !strings.EqualFold(name, "authorization") {
							continue
						}
						if request.Header.Get(name) != fmt.Sprint(expected) {
							t.Fatalf("%s = %q, want %q", name, request.Header.Get(name), expected)
						}
					}
					if operation.Request.Body != nil {
						actual, err := io.ReadAll(request.Body)
						if err != nil {
							t.Fatal(err)
						}
						assertProducerJSON(t, actual, operation.Request.Body.Value)
					}
					return &http.Response{StatusCode: response.Status, Header: http.Header{"Content-Type": {response.MediaType}, "X-Request-Id": {"producer-conformance"}}, Body: io.NopCloser(bytes.NewReader(wireBody))}, nil
				})
				client, err := NewClient(Config{HTTPClient: &http.Client{Transport: transport}, AccessToken: "conformance-key"})
				if err != nil {
					t.Fatal(err)
				}
				var output json.RawMessage
				var raw binaryResponse
				var target any = &output
				if response.MediaType == "application/octet-stream" {
					target = &raw
				}
				err = client.do(t.Context(), operation.Request.Method, operation.Request.Path, query, requestBody, target, operation.ID)
				if response.Status >= 200 && response.Status < 300 {
					if err != nil {
						t.Fatal(err)
					}
					if response.MediaType == "application/octet-stream" {
						if !bytes.Equal(raw, wireBody) {
							t.Fatal("binary response changed")
						}
					} else if len(response.Body) != 0 && string(response.Body) != "null" {
						assertProducerJSON(t, output, response.Body)
					}
				} else {
					var apiErr *APIError
					if !errors.As(err, &apiErr) || apiErr.Status != response.Status {
						t.Fatalf("expected status %d, got %v", response.Status, err)
					}
					// A 101 is the WebSocket upgrade contract, not a REST success.
					if response.Status != http.StatusSwitchingProtocols {
						var envelope struct {
							Error struct{ Code, Message, Fix string }
						}
						if err := json.Unmarshal(response.Body, &envelope); err != nil {
							t.Fatal(err)
						}
						if apiErr.Code != envelope.Error.Code || apiErr.Message != envelope.Error.Message || apiErr.Fix != envelope.Error.Fix || apiErr.RequestID != "producer-conformance" {
							t.Fatalf("producer error lost detail: %+v", apiErr)
						}
					}
				}
				if calls != 1 {
					t.Fatalf("unkeyed request was repeated %d times", calls)
				}
			})
		}
	}
}

func assertProducerJSON(t *testing.T, actual, expected []byte) {
	t.Helper()
	var a, b any
	decode := func(raw []byte, value *any) {
		decoder := json.NewDecoder(bytes.NewReader(raw))
		decoder.UseNumber()
		if err := decoder.Decode(value); err != nil {
			t.Fatal(err)
		}
	}
	decode(actual, &a)
	decode(expected, &b)
	if !reflect.DeepEqual(a, b) {
		t.Fatalf("JSON differs from producer: got %s, want %s", actual, expected)
	}
}

func TestProducerReactiveSemanticCorpus(t *testing.T) {
	var corpus struct {
		Attach struct {
			Response struct {
				Body struct {
					Checkpoint string `json:"checkpoint"`
				} `json:"body"`
			} `json:"response"`
		} `json:"attach"`
		Snapshot struct {
			Hex string `json:"hex"`
		} `json:"snapshot"`
		Log struct {
			Hex string `json:"hex"`
		} `json:"log"`
		Reset struct {
			Hex string `json:"hex"`
		} `json:"reset"`
	}
	readPinnedJSON(t, "conformance/reactive.json", &corpus)
	decode := func(encoded string) reactiveMPMessage {
		wire, err := hex.DecodeString(encoded)
		if err != nil {
			t.Fatal(err)
		}
		if err := scanReactiveMessagePack(wire); err != nil {
			t.Fatal(err)
		}
		reader := reactiveMPReader{b: wire}
		message, err := decodeMPEnvelope(&reader, "v-conformance-view", 1)
		if err != nil {
			t.Fatal(err)
		}
		if reader.i != len(wire) {
			t.Fatal("trailing producer bytes")
		}
		return message
	}
	// TerraBase's native view digest has four FNV lanes: empty views use zero,
	// and this literal identifies the producer's order-1/status="paid" view.
	const emptyDigest = "0000000000000000000000000000000000000000000000000000000000000000"
	const paidDigest = "d12faa372354de79608c8212f5fd2f3207bdf89a37b0242b1ec38d57848e19bc"
	snapshot := decode(corpus.Snapshot.Hex)
	checkpoint, err := base64.StdEncoding.DecodeString(corpus.Attach.Response.Body.Checkpoint)
	if err != nil {
		t.Fatal(err)
	}
	if snapshot.viewGeneration != 1 || snapshot.authorizationGeneration != 1 || snapshot.revision != 0 || snapshot.digest != emptyDigest || len(snapshot.snapshot) != 0 || !bytes.Equal(snapshot.checkpointBytes, checkpoint) {
		t.Fatalf("snapshot metadata diverged: %+v", snapshot)
	}
	log := decode(corpus.Log.Hex)
	if len(log.updates) != 1 {
		t.Fatalf("log has %d operations", len(log.updates))
	}
	update := log.updates[0]
	if update.Kind != UpdateInsert || update.ID != "order-1" || update.Position != 0 || update.FromRevision != 0 || update.ToRevision != 1 || update.ViewGeneration != 1 || update.Digest != paidDigest || string(update.Fields["status"]) != `"paid"` {
		t.Fatalf("producer delta lost semantics: %+v", update)
	}
	reset := decode(corpus.Reset.Hex)
	if reset.reset == nil || reset.reset.ViewGeneration != 1 || reset.reset.AuthorizationGeneration != 1 || reset.reset.Revision != 1 || reset.reset.Digest != emptyDigest || len(reset.reset.Snapshot) != 0 {
		t.Fatalf("producer reset lost replacement metadata: %+v", reset.reset)
	}
}
