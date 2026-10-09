package app

import (
	"crypto/tls"
	"errors"
	"io"
	"log"
	"net/http"
	"net/http/httptest"
	"slices"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/terrabasetest"
)

func TestTerraBaseDefaultPersistenceTLSMinimum(t *testing.T) {
	if testing.Short() {
		t.Skip("starts pinned TerraBase release")
	}
	tb := terrabasetest.Start(t)
	db, key := terrabasetest.Database(t, tb, "dashboard")
	for _, name := range []string{"STRIPE_MODE", "STRIPE_SECRET_KEY", "STRIPE_WEBHOOK_SECRET"} {
		t.Setenv(name, "")
	}
	versions := make(chan []uint16, 1)
	backend := httptest.NewUnstartedServer(http.NotFoundHandler())
	backend.Config.ErrorLog = log.New(io.Discard, "", 0)
	backend.TLS = &tls.Config{
		GetConfigForClient: func(hello *tls.ClientHelloInfo) (*tls.Config, error) {
			select {
			case versions <- slices.Clone(hello.SupportedVersions):
			default:
			}
			// Observe the real client before certificate verification, without trusting a test CA.
			return nil, errors.New("stop after observing ClientHello")
		},
	}
	backend.StartTLS()
	defer backend.Close()
	application, err := New(config.Config{
		TerraBaseURL: backend.URL, TerraBaseAPIKey: key,
		TerraBaseProjectID: db.Project(), TerraBaseDatabase: db.Name(),
		CSRFSecret: []byte(strings.Repeat("r", 32)),
	})
	if err != nil {
		t.Fatal(err)
	}
	response := httptest.NewRecorder()
	application.Handler.ServeHTTP(response, httptest.NewRequest(http.MethodGet, "/health/ready", nil))
	if response.Code != http.StatusServiceUnavailable {
		t.Fatalf("readiness = %d, want 503 after rejected handshake", response.Code)
	}
	select {
	case offered := <-versions:
		if !slices.Contains(offered, tls.VersionTLS13) {
			t.Fatalf("ClientHello versions = %x, want TLS 1.3", offered)
		}
		for _, version := range offered {
			if version < tls.VersionTLS13 {
				t.Fatalf("ClientHello offers TLS below 1.3: %x", offered)
			}
		}
	default:
		t.Fatal("readiness did not send a persistence ClientHello")
	}
}
