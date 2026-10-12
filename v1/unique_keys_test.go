package v1_test

// Unique keys against httptest servers: the requests the SDK sends, the
// inputs it rejects before sending, and the codes it maps.

import (
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"

	v1 "terrascale.tech/sdk/v1"
)

const schemaPath = "/v1/projects/" + testProject + "/databases/main/schema/accounts"

func TestDeclareUniqueKeysSendsTheSchemaRequest(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPut || r.URL.EscapedPath() != schemaPath {
			t.Errorf("request %s %s", r.Method, r.URL.EscapedPath())
		}
		if got := r.Header.Get("Authorization"); got != "Bearer "+testToken {
			t.Errorf("authorization %q", got)
		}
		body, err := io.ReadAll(r.Body)
		want := `{"indexes":[{"name":"accounts_email","fields":["email"],"unique":true,"normalization":"trim_lowercase"},{"name":"accounts_pair","fields":["team","email"],"unique":true}]}`
		if err != nil || string(body) != want {
			t.Errorf("body %s, %v", body, err)
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"constraints":[{"name":"accounts_email","fields":["email"],"normalization":"trim_lowercase"},{"name":"accounts_pair","fields":["team","email"],"normalization":"none"}]}`)
	}))
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	keys, err := c.Database(testProject, "main").DeclareUniqueKeys(t.Context(), "accounts", []v1.UniqueKey{
		{Name: "accounts_email", Paths: []string{"email"}, Normalization: v1.NormalizationTrimLowercase},
		{Name: "accounts_pair", Paths: []string{"team", "email"}},
	})
	if err != nil {
		t.Fatal(err)
	}
	if len(keys) != 2 || keys[1].Name != "accounts_pair" || !slices.Equal(keys[1].Paths, []string{"team", "email"}) || keys[0].Normalization != v1.NormalizationTrimLowercase {
		t.Fatalf("declared keys: %+v", keys)
	}
}

func TestDeclareUniqueKeysDerivesStableNames(t *testing.T) {
	names := make(chan string, 3)
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body struct {
			Indexes []struct {
				Name string `json:"name"`
			} `json:"indexes"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil || len(body.Indexes) != 1 {
			t.Errorf("body: %+v, %v", body, err)
		}
		names <- body.Indexes[0].Name
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"constraints":[]}`)
	}))
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(testProject, "main")
	for _, key := range []v1.UniqueKey{
		{Paths: []string{"email"}, Normalization: v1.NormalizationLowercase},
		{Paths: []string{"email"}, Normalization: v1.NormalizationLowercase},
		{Paths: []string{"email"}},
	} {
		if _, err := db.DeclareUniqueKeys(t.Context(), "accounts", []v1.UniqueKey{key}); err != nil {
			t.Fatal(err)
		}
	}
	first, again, none := <-names, <-names, <-names
	if first != again || first == none || !strings.HasPrefix(first, "key_") || len(first) != len("key_")+16 {
		t.Fatalf("names: %q %q %q", first, again, none)
	}
}

func TestDeclareUniqueKeysRejectsInvalidKeysBeforeSending(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		t.Error("an invalid declaration reached the server")
	}))
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(testProject, "main")
	tooMany := make([]v1.UniqueKey, 33)
	for i := range tooMany {
		tooMany[i] = v1.UniqueKey{Paths: []string{"email"}}
	}
	for _, tc := range []struct {
		name string
		keys []v1.UniqueKey
	}{
		{"no paths", []v1.UniqueKey{{Name: "k"}}},
		{"five paths", []v1.UniqueKey{{Name: "k", Paths: []string{"a", "b", "c", "d", "e"}}}},
		{"empty path", []v1.UniqueKey{{Name: "k", Paths: []string{"a", ""}}}},
		{"repeated path", []v1.UniqueKey{{Name: "k", Paths: []string{"a", "a"}}}},
		{"unknown normalization", []v1.UniqueKey{{Name: "k", Paths: []string{"a"}, Normalization: "upper"}}},
		{"too many keys", tooMany},
	} {
		t.Run(tc.name, func(t *testing.T) {
			if _, err := db.DeclareUniqueKeys(t.Context(), "accounts", tc.keys); err == nil {
				t.Fatal("accepted")
			}
		})
	}
}

func TestUniqueKeysReadsTheDeclaredKeys(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet || r.URL.EscapedPath() != schemaPath {
			t.Errorf("request %s %s", r.Method, r.URL.EscapedPath())
		}
		w.Header().Set("Content-Type", "application/json")
		io.WriteString(w, `{"collection":"accounts","documents":0,"sampledDocuments":0,"fields":[],"uniqueConstraints":[{"name":"accounts_email","fields":["email"],"normalization":"trim_lowercase"}]}`)
	}))
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	keys, err := c.Database(testProject, "main").UniqueKeys(t.Context(), "accounts")
	if err != nil {
		t.Fatal(err)
	}
	if len(keys) != 1 || keys[0].Name != "accounts_email" || !slices.Equal(keys[0].Paths, []string{"email"}) || keys[0].Normalization != v1.NormalizationTrimLowercase {
		t.Fatalf("keys: %+v", keys)
	}
}

func TestUniqueKeyErrorsMatchTheirCodes(t *testing.T) {
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		code := "unique_violation"
		if strings.Contains(r.URL.Path, "/schema/") {
			code = "schema_requires_empty_collection"
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusConflict)
		io.WriteString(w, `{"error":{"code":"`+code+`","message":"conflict","op":1}}`)
	}))
	t.Cleanup(srv.Close)
	c, err := v1.New(v1.Config{BaseURL: srv.URL, Token: testToken})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(testProject, "main")
	_, err = db.DeclareUniqueKeys(t.Context(), "accounts", nil)
	if !errors.Is(err, v1.ErrSchemaRequiresEmptyCollection) || errors.Is(err, v1.ErrUniqueViolation) {
		t.Fatalf("schema: %v", err)
	}

	_, err = db.Collection("accounts").Create(t.Context(), map[string]any{"email": "ana@example.com"})
	if !errors.Is(err, v1.ErrUniqueViolation) {
		t.Fatalf("create: %v", err)
	}

	_, err = db.Tx().Create("accounts", v1.NewID(), map[string]any{"email": "bo@example.com"}).Commit(t.Context())
	var apiErr *v1.APIError
	if !errors.Is(err, v1.ErrUniqueViolation) || !errors.As(err, &apiErr) {
		t.Fatalf("commit: %v", err)
	}
	if op, ok := apiErr.OpIndex(); !ok || op != 1 {
		t.Fatalf("op index: %d %t", op, ok)
	}
}
