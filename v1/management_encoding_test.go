package v1

import (
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

type changingManagementBody struct{ calls int }

func (b *changingManagementBody) MarshalJSON() ([]byte, error) {
	b.calls++
	if b.calls > 1 {
		return []byte(`{"organizationId":"legacy"}`), nil
	}
	return []byte(`{"organizationId":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}`), nil
}

func TestManagementSendsTheValidatedEncoding(t *testing.T) {
	peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(r.Body)
		if err != nil || string(body) != `{"organizationId":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}` {
			t.Error("request bytes differ from the validated encoding")
		}
		w.WriteHeader(http.StatusNoContent)
	}))
	t.Cleanup(peer.Close)
	m, err := NewManagement(Config{BaseURL: peer.URL})
	if err != nil {
		t.Fatal(err)
	}
	body := &changingManagementBody{}
	if err := m.WithToken("test-access-token").call(t.Context(), http.MethodPost, []string{"commits"}, nil, body, nil); err != nil {
		t.Fatal(err)
	}
	if body.calls != 1 {
		t.Fatalf("encoded the request %d times", body.calls)
	}
}

func TestManagementAsReportsInvalidUserAndMissingCredential(t *testing.T) {
	m, err := NewManagement(Config{BaseURL: "https://tb.example"})
	if err != nil {
		t.Fatal(err)
	}
	err = m.As("legacy").DeleteOrganization(t.Context(), "01ARZ3NDEKTSV4RRFFQ69G5FAV")
	if err == nil || !strings.Contains(err.Error(), "user ID") || !strings.Contains(err.Error(), "Config.Token") {
		t.Fatal("As must report both invalid user ID and missing credential")
	}
}
