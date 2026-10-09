package tsauth

import (
	"io"
	"net/http"
	"net/http/httptest"
	"testing"
)

type changingManagementBody struct{ calls int }

func (b *changingManagementBody) MarshalJSON() ([]byte, error) {
	b.calls++
	if b.calls > 1 {
		return []byte(`{"project_id":"legacy"}`), nil
	}
	return []byte(`{"project_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}`), nil
}

func TestManagementSendsTheValidatedEncoding(t *testing.T) {
	peer := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, err := io.ReadAll(r.Body)
		if err != nil || string(body) != `{"project_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV"}` {
			t.Error("request bytes differ from the validated encoding")
		}
		w.WriteHeader(http.StatusNoContent)
	}))
	t.Cleanup(peer.Close)
	m, err := New(Config{Issuer: peer.URL + "/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	body := &changingManagementBody{}
	if err := m.WithToken("test-access-token").do(t.Context(), http.MethodPost, []string{"organizations"}, nil, body, nil); err != nil {
		t.Fatal(err)
	}
	if body.calls != 1 {
		t.Fatalf("encoded the request %d times", body.calls)
	}
}
