package v1

import (
	"encoding/base64"
	"encoding/json"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/sdk/internal/entityid"
)

func TestStoredSQLKeysAcrossReadStreamsAndCheckpoints(t *testing.T) {
	for _, key := range []string{"42", "customer/chosen", `[1,"a"]`, " /a%2Fb?#+ ", "e\u0301", strings.Repeat("界", 256), strings.Repeat("😀", 128)} {
		t.Run(key[:min(len(key), 20)], func(t *testing.T) {
			if err := entityid.StoredKey(key); err != nil {
				t.Fatal(err)
			}
			if entityid.DocumentKey(key) == nil {
				t.Fatal("SQL key became valid document point input")
			}
			quoted, _ := json.Marshal(key)
			body := fmt.Sprintf("event: change\nid: checkpoint\ndata: {\"collection\":\"t\",\"id\":%s,\"op\":\"upsert\",\"version\":\"1\",\"origin\":\"0123456789abcdef\",\"document\":{}}\n\n", quoted)
			events := 0
			err := readChanges(strings.NewReader(body), 4096, nil, func(e ChangeEvent) bool {
				events++
				if e.ID != key {
					t.Fatal("SSE changed key")
				}
				return true
			})
			if err != nil || events != 1 {
				t.Fatalf("SSE events=%d error=%v", events, err)
			}
			page := base64.RawURLEncoding.EncodeToString([]byte(key))
			decoded, err := decodeShapePage(page)
			if err != nil || decoded != key {
				t.Fatalf("page=%q error=%v", decoded, err)
			}
			op := "upsert"
			version := "opaque"
			document := json.RawMessage(`{}`)
			if err := checkShapeOps([]shapeOpWire{{ID: &key, Op: op, Version: &version, Document: document}}, true, "", false, ""); err != nil {
				t.Fatal(err)
			}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.URL.Query().Get("after_id") != key || r.URL.Query().Get("after_collection") != "t" {
					t.Error("export checkpoint changed key")
				}
				w.Header().Set("Content-Type", "application/x-ndjson")
				fmt.Fprintf(w, "{\"collection\":\"t\",\"id\":%s,\"document\":{}}\n", quoted)
			}))
			defer server.Close()
			client, err := New(Config{BaseURL: server.URL, Token: "fixture.jwt.sig"})
			if err != nil {
				t.Fatal(err)
			}
			count := 0
			for doc, err := range client.Database("01ARZ3NDEKTSV4RRFFQ69G5FAV", "main").Export(t.Context(), ExportOptions{AfterCollection: "t", AfterID: key}) {
				if err != nil || doc.ID != key {
					t.Fatalf("export ID=%q error=%v", doc.ID, err)
				}
				count++
			}
			if count != 1 {
				t.Fatal(count)
			}
		})
	}
}

func TestStoredKeysRejectInvalidBoundsWithoutChangingPointContract(t *testing.T) {
	for _, key := range []string{"", "a\x00b", "a\nb", "a\u0085b", strings.Repeat("x", 257), strings.Repeat("😀", 129), string([]byte{0xff})} {
		if entityid.StoredKey(key) == nil {
			t.Fatal("accepted invalid stored key")
		}
		if _, err := decodeShapePage(base64.RawURLEncoding.EncodeToString([]byte(key))); err == nil {
			t.Fatal("accepted invalid shape checkpoint")
		}
	}
	// Both public point read paths reject a user SQL key before transport I/O.
	c, err := New(Config{Token: "fixture.jwt.sig", HTTPClient: &http.Client{Transport: storedNoTransport{t}}})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.Database("01ARZ3NDEKTSV4RRFFQ69G5FAV", "main").Collection("t").Get(t.Context(), "42"); err == nil {
		t.Fatal("point read accepted SQL key")
	}
	if _, err := c.PlatformDocument(t.Context(), "main", "t", "42"); err == nil {
		t.Fatal("platform point read accepted SQL key")
	}
}

type storedNoTransport struct{ t *testing.T }

func (s storedNoTransport) RoundTrip(*http.Request) (*http.Response, error) {
	s.t.Error("invalid point key reached transport")
	return nil, io.EOF
}

func TestPlatformPointResponseStillRequiresULID(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"id":"42","version":"opaque","state":"value","document":{}}`)
	}))
	defer server.Close()
	c, err := New(Config{BaseURL: server.URL, Token: "fixture.jwt.sig"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := c.PlatformDocument(t.Context(), "main", "t", "01ARZ3NDEKTSV4RRFFQ69G5FAV"); err == nil {
		t.Fatal("point response accepted SQL key")
	}
}
