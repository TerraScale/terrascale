package pitrbench

import (
	"context"
	"net"
	"net/http"
	"net/http/httptest"
	"slices"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestDataRoutesCarryNoOwnerHeader(t *testing.T) {
	t.Parallel()
	var got []string
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, req *http.Request) {
		got = append(got, req.URL.Path+"="+req.Header.Get("X-TerraBase-User-Id"))
	}))
	t.Cleanup(srv.Close)
	_, port, _ := net.SplitHostPort(srv.Listener.Addr().String())
	n, _ := strconv.Atoi(port)
	var p http.Protocols
	p.SetHTTP1(true)
	c := NewClient(n, strings.Repeat("a", 64), "OWNER", time.Second)
	c.hc.Transport = &http.Transport{Protocols: &p}
	for _, path := range []string{"/v1/projects/P/databases/d/transactions", "/v1/management/organizations/O/projects/P/databases/D/indexes"} {
		if _, err := c.Do(context.Background(), http.MethodPost, path, []byte(`{}`)); err != nil {
			t.Fatal(err)
		}
	}
	// On a data route the owner header turns the operator into that user, who holds no scopes there (403).
	if want := []string{"/v1/projects/P/databases/d/transactions=", "/v1/management/organizations/O/projects/P/databases/D/indexes=OWNER"}; !slices.Equal(got, want) {
		t.Fatalf("owner headers %v, want %v", got, want)
	}
}
