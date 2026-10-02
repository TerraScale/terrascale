package v1_test

// The v1 client tests run against the in-memory contract fake in
// terrascale.tech/sdk/v1/v1test over real HTTPS. This file adapts it to the
// names the tests use.

import (
	"net/http"
	"testing"
	"time"

	"terrascale.tech/sdk/v1/v1test"
)

type fixture struct {
	*v1test.Server
	origin                  struct{ URL string }
	dataToken, controlToken string
	tenantID                string
}

type fxFault struct {
	method, pathPrefix string
	status             int
	code               string
	retryAfter         string
	afterCommit        bool
	remaining          int
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	srv, err := v1test.Start(v1test.Options{Seed: true, LiveWait: 300 * time.Millisecond, OperationDelay: time.Hour})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(srv.Close)
	// The dashboard tests exercise the failed-operation shape.
	srv.FailOperations("delete_collection", "storage_unavailable", "object store unreachable")
	f := &fixture{Server: srv, dataToken: srv.Seed.DataKey, controlToken: srv.ControlKey, tenantID: srv.Seed.TenantID}
	f.origin.URL = srv.URL
	return f
}

func (f *fixture) httpClient() *http.Client { return f.Client() }

func (f *fixture) inject(fault fxFault) {
	f.InjectFault(v1test.Fault{Method: fault.method, PathPrefix: fault.pathPrefix, Status: fault.status, Code: fault.code,
		RetryAfter: fault.retryAfter, AfterCommit: fault.afterCommit, Remaining: fault.remaining})
}

func (f *fixture) faultsLeft() int { return f.FaultsLeft() }

func (f *fixture) count(method, path string) int { return f.RequestCount(method, path) }

func (f *fixture) seedConflict(db, coll, id, a, b string) string {
	v, err := f.SeedConflict(f.tenantID, db, coll, id, a, b)
	if err != nil {
		panic(err)
	}
	return v
}

func (f *fixture) rotateHandle(shapeID string) { f.RotateShapeHandle(shapeID) }
