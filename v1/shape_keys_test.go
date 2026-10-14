package v1

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"strings"
	"sync/atomic"
	"testing"
)

func TestShapeKeyDiagnosticsRedactKey(t *testing.T) {
	material := bytes.Repeat([]byte{173}, 32)
	key := ShapeKey{Handle: shapeKeyHandle, Epoch: 100, KeyID: shapeKeyID, key: func() []byte { return material }}
	for _, value := range []any{key, &key} {
		for _, format := range []string{"%v", "%+v", "%#v", "%s", "%q", "%d", "%+d", "%#x", "%f", "%t"} {
			got := fmt.Sprintf(format, value)
			if !strings.Contains(got, "key redacted") || strings.Contains(got, "173") {
				t.Fatalf("%s diagnostic exposes key material: %s", format, got)
			}
		}
	}
	for _, value := range []any{key, &key, struct{ Key ShapeKey }{key}, struct{ key ShapeKey }{key}} {
		for _, format := range []string{"%p", "%v", "%#v", "%d", "%x", "%s", "%q", "%8.3f"} {
			got := fmt.Sprintf(format, value)
			normalized := strings.Join(strings.Fields(got), " ")
			if bytes.Contains([]byte(got), material) || strings.Contains(normalized, "173 173") || strings.Contains(got, "[ad ad") || strings.Contains(got, strings.Repeat("ad", 32)) {
				t.Fatalf("%s diagnostic exposes key material: %s", format, got)
			}
		}
	}
}

const (
	shapeKeyProject = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
	shapeKeyHandle  = "h1-f3171a2230109328"
	shapeKeyID      = "01ARZ3NDEKTSV4RRFFQ69G5FC0"
	shapeKeyValue   = "hunfuJAccVHoXLOVd_t4sE3RV9_6Y0IAlZ4G139hEAU"
	shapeKeyToken   = "tb_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
)

type shapeKeyHTTPVector struct {
	ID      string `json:"id"`
	Request struct {
		Method  string            `json:"method"`
		Path    string            `json:"path"`
		Query   map[string]string `json:"query"`
		Headers map[string]string `json:"headers"`
	} `json:"request"`
	Response struct {
		Status   int               `json:"status"`
		Headers  map[string]string `json:"headers"`
		BodyUTF8 string            `json:"body_utf8"`
	} `json:"response"`
}

func shapeKeyVector(t *testing.T, id string) shapeKeyHTTPVector {
	t.Helper()
	raw, err := os.ReadFile("testdata/shape-vectors.json")
	if err != nil {
		t.Fatal(err)
	}
	var file struct {
		HTTP []shapeKeyHTTPVector `json:"http_vectors"`
	}
	if err := json.Unmarshal(raw, &file); err != nil {
		t.Fatal(err)
	}
	for _, v := range file.HTTP {
		if v.ID == id {
			return v
		}
	}
	t.Fatalf("vector %q not found", id)
	return shapeKeyHTTPVector{}
}

// shapeKeyCollection returns the notes collection served from base with the test token.
func shapeKeyCollection(t *testing.T, base string) *Collection {
	t.Helper()
	c, err := New(Config{BaseURL: base, Token: shapeKeyToken})
	if err != nil {
		t.Fatal(err)
	}
	return c.Database(shapeKeyProject, "demo").Collection("notes")
}

// serveShapeKeyVector replays one http vector and checks the request it receives.
func serveShapeKeyVector(t *testing.T, v shapeKeyHTTPVector, calls *atomic.Int64) *httptest.Server {
	t.Helper()
	return httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		if r.Method != v.Request.Method || r.URL.Path != v.Request.Path {
			t.Errorf("request: %s %s", r.Method, r.URL.Path)
		}
		if len(r.URL.Query()) != len(v.Request.Query) {
			t.Errorf("query: %v", r.URL.Query())
		}
		for k, want := range v.Request.Query {
			if got := r.URL.Query().Get(k); got != want {
				t.Errorf("query %s = %q, want %q", k, got, want)
			}
		}
		if got := r.Header.Get("Authorization"); got != "Bearer "+shapeKeyToken {
			t.Errorf("Authorization = %q", got)
		}
		if got := r.Header.Get("Cache-Control"); got != "no-store" {
			t.Errorf("request Cache-Control = %q, want no-store", got)
		}
		for k, val := range v.Response.Headers {
			w.Header().Set(k, val)
		}
		w.WriteHeader(v.Response.Status)
		io.WriteString(w, v.Response.BodyUTF8)
	}))
}

func TestShapeKeyVectorResponses(t *testing.T) {
	for _, id := range []string{"epoch-key", "future-key-refused", "retired-epoch-key-pair"} {
		t.Run(id, func(t *testing.T) {
			v := shapeKeyVector(t, id)
			var calls atomic.Int64
			server := serveShapeKeyVector(t, v, &calls)
			defer server.Close()

			q := v.Request.Query
			epoch := mustEpoch(t, q["epoch"])
			col := shapeKeyCollection(t, server.URL)
			shapeKeyClock(col, int64(epoch)*600+1)
			key, err := col.ShapeKey(t.Context(), q["handle"], epoch, q["key_id"])
			if calls.Load() != 1 {
				t.Fatalf("requests = %d, want 1 (no retry, no prefetch)", calls.Load())
			}
			switch id {
			case "epoch-key":
				if err != nil {
					t.Fatal(err)
				}
				if _, err := col.ShapeKey(t.Context(), q["handle"], epoch, q["key_id"]); err != nil {
					t.Fatal(err)
				}
				if calls.Load() != 1 {
					t.Fatal("epoch-key vector requires reuse from process memory")
				}
				var body struct {
					Key string `json:"key"`
				}
				if err := json.Unmarshal([]byte(v.Response.BodyUTF8), &body); err != nil {
					t.Fatal(err)
				}
				if key.Handle != q["handle"] || key.Epoch != epoch || key.KeyID != q["key_id"] {
					t.Fatalf("metadata = %s %d %s", key.Handle, key.Epoch, key.KeyID)
				}
				if got := base64.RawURLEncoding.EncodeToString(key.key()); got != body.Key {
					t.Fatalf("key = %s, want %s", got, body.Key)
				}
			case "future-key-refused":
				if !errors.Is(err, ErrInvalidRequest) || key != nil {
					t.Fatalf("err = %v, key = %v; want invalid_request and no key", err, key)
				}
			case "retired-epoch-key-pair":
				if !errors.Is(err, ErrMustRefetch) || key != nil {
					t.Fatalf("err = %v, key = %v; want must_refetch and no key", err, key)
				}
			}
		})
	}
}

// The client refuses a missing key_id before sending, so the server's
// "key_id is required" vector is never reached.
func TestShapeKeyRequiresKeyIDLocally(t *testing.T) {
	var calls atomic.Int64
	server := serveShapeKeyVector(t, shapeKeyVector(t, "key-id-required"), &calls)
	defer server.Close()

	key, err := shapeKeyCollection(t, server.URL).ShapeKey(t.Context(), shapeKeyHandle, 100, "")
	if err == nil || key != nil || calls.Load() != 0 {
		t.Fatalf("err = %v, key = %v, requests = %d; want refusal with no request", err, key, calls.Load())
	}
}

func TestShapeKeyRefusesBadInputsBeforeRequest(t *testing.T) {
	for _, tc := range []struct {
		name, handle, keyID string
	}{
		{"uppercase handle", "H1-F3171A2230109328", shapeKeyID},
		{"short handle", "h1-f31712", shapeKeyID},
		{"handle without prefix", "f3171a2230109328", shapeKeyID},
		{"empty key ID", shapeKeyHandle, ""},
		{"non-ULID key ID", shapeKeyHandle, "not-a-ulid"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var calls atomic.Int64
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { calls.Add(1) }))
			defer server.Close()

			key, err := shapeKeyCollection(t, server.URL).ShapeKey(t.Context(), tc.handle, 100, tc.keyID)
			if err == nil || key != nil || calls.Load() != 0 {
				t.Fatalf("err = %v, key = %v, requests = %d; want refusal with no request", err, key, calls.Load())
			}
		})
	}
}

// shapeKeyBody encodes a key response; a string map cannot fail to marshal.
func shapeKeyBody(handle, epoch, keyID, key string) string {
	raw, _ := json.Marshal(map[string]string{"handle": handle, "epoch": epoch, "key_id": keyID, "key": key})
	return string(raw)
}

func TestShapeKeyResponseValidation(t *testing.T) {
	ok := shapeKeyBody(shapeKeyHandle, "100", shapeKeyID, shapeKeyValue)
	for _, tc := range []struct {
		name, cacheControl, body string
		wantErr                  bool
	}{
		{"exact no-store", "no-store", ok, false},
		{"no-store among directives, any case", "private, NO-STORE", ok, false},
		{"missing no-store", "private", ok, true},
		{"no Cache-Control", "", ok, true},
		{"handle mismatch", "no-store", shapeKeyBody("h1-0000000000000000", "100", shapeKeyID, shapeKeyValue), true},
		{"epoch mismatch", "no-store", shapeKeyBody(shapeKeyHandle, "101", shapeKeyID, shapeKeyValue), true},
		{"key ID mismatch", "no-store", shapeKeyBody(shapeKeyHandle, "100", "01ARZ3NDEKTSV4RRFFQ69G5FC1", shapeKeyValue), true},
		{"padded key", "no-store", shapeKeyBody(shapeKeyHandle, "100", shapeKeyID, shapeKeyValue+"="), true},
		{"31-byte key", "no-store", shapeKeyBody(shapeKeyHandle, "100", shapeKeyID, shapeKeyValue[:42]), true},
		{"33-byte key", "no-store", shapeKeyBody(shapeKeyHandle, "100", shapeKeyID, shapeKeyValue+"A"), true},
		{"non-zero trailing bits", "no-store", shapeKeyBody(shapeKeyHandle, "100", shapeKeyID, shapeKeyValue[:42]+"V"), true},
		{"embedded CR LF", "no-store", shapeKeyBody(shapeKeyHandle, "100", shapeKeyID, shapeKeyValue[:20]+"\r\n"+shapeKeyValue[20:]), true},
	} {
		t.Run(tc.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if tc.cacheControl != "" {
					w.Header().Set("Cache-Control", tc.cacheControl)
				}
				w.Header().Set("Content-Type", "application/json")
				io.WriteString(w, tc.body)
			}))
			defer server.Close()

			key, err := shapeKeyCollection(t, server.URL).ShapeKey(t.Context(), shapeKeyHandle, 100, shapeKeyID)
			if (err != nil) != tc.wantErr {
				t.Fatalf("err = %v, want error %v", err, tc.wantErr)
			}
			if !tc.wantErr && (key.Handle != shapeKeyHandle || key.Epoch != 100 || key.KeyID != shapeKeyID) {
				t.Fatalf("metadata = %s %d %s", key.Handle, key.Epoch, key.KeyID)
			}
		})
	}
}

// Rotation fetches each epoch with its own request and keeps each key separate.
func TestShapeKeyRotationFetchesEachEpochSeparately(t *testing.T) {
	const nextKeyID = "01ARZ3NDEKTSV4RRFFQ69G5FC1"
	keys := map[string]struct{ keyID, key string }{
		"100": {shapeKeyID, base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{1}, 32))},
		"101": {nextKeyID, base64.RawURLEncoding.EncodeToString(bytes.Repeat([]byte{2}, 32))},
	}
	var calls atomic.Int64
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls.Add(1)
		epoch := r.URL.Query().Get("epoch")
		pair, found := keys[epoch]
		if !found {
			http.Error(w, `{"error":{"code":"must_refetch"}}`, http.StatusConflict)
			return
		}
		w.Header().Set("Cache-Control", "no-store")
		io.WriteString(w, shapeKeyBody(shapeKeyHandle, epoch, pair.keyID, pair.key))
	}))
	defer server.Close()

	col := shapeKeyCollection(t, server.URL)
	current, err := col.ShapeKey(t.Context(), shapeKeyHandle, 100, shapeKeyID)
	if err != nil {
		t.Fatal(err)
	}
	next, err := col.ShapeKey(t.Context(), shapeKeyHandle, 101, nextKeyID)
	if err != nil {
		t.Fatal(err)
	}
	if calls.Load() != 2 {
		t.Fatalf("requests = %d, want 2", calls.Load())
	}
	if current.Epoch != 100 || current.KeyID != shapeKeyID || next.Epoch != 101 || next.KeyID != nextKeyID {
		t.Fatalf("epochs/IDs = %d %s, %d %s", current.Epoch, current.KeyID, next.Epoch, next.KeyID)
	}
	if bytes.Equal(current.key(), next.key()) {
		t.Fatal("epoch keys are equal")
	}
}

func TestShapeKeyDecryptVectors(t *testing.T) {
	for _, v := range loadShapeVectors(t).TBS1 {
		t.Run(v.ID, func(t *testing.T) {
			epoch := mustEpoch(t, v.Epoch)
			obj := mustHex(t, v.TBS1Hex)
			material := mustHex(t, v.EpochKeyHex)
			key := &ShapeKey{Handle: v.Handle, Epoch: epoch, KeyID: v.KeyID, key: func() []byte { return material }}

			got, err := key.Decrypt(v.RequestOffset, obj)
			if err != nil {
				t.Fatal(err)
			}
			if string(got) != v.PlaintextUTF8 {
				t.Fatalf("plaintext = %q, want %q", got, v.PlaintextUTF8)
			}
			var missing ShapeKey
			if got, err := missing.Decrypt(v.RequestOffset, obj); err == nil || got != nil {
				t.Fatal("missing epoch key must reject decryption")
			}

			wrongEpoch := &ShapeKey{Handle: v.Handle, Epoch: epoch + 1, KeyID: v.KeyID, key: key.key}
			if _, err := wrongEpoch.Decrypt(v.RequestOffset, obj); err == nil {
				t.Fatal("wrong epoch decrypted")
			}
			// "-1" is the bootstrap sentinel; the other two parse but bind to a different offset.
			for _, offset := range []string{"-1", "0" + v.RequestOffset, v.RequestOffset + "0"} {
				if _, err := key.Decrypt(offset, obj); err == nil {
					t.Fatalf("offset %q decrypted", offset)
				}
			}
		})
	}
}
