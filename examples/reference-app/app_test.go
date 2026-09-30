package main

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"

	sdk "terrascale.tech/sdk"
)

const recordID sdk.UUID = "33333333-3333-4333-8333-333333333333"

type fakeIdentity struct {
	exchanges, revocations int
	active                 bool
	revokeError            error
}

func (f *fakeIdentity) BeginAuthorization(context.Context, []string) (*sdk.TSAuthAuthorizationAttempt, error) {
	return &sdk.TSAuthAuthorizationAttempt{AuthorizationURL: "https://auth.example/notes/oauth/authorize", State: "state", ExpiresAt: time.Now().Add(time.Minute)}, nil
}
func (f *fakeIdentity) ExchangeCode(_ context.Context, callback sdk.TSAuthOIDCCallback, attempt sdk.TSAuthAuthorizationAttempt) (*sdk.TSAuthOIDCLogin, error) {
	f.exchanges++
	if callback.State != attempt.State {
		return nil, errors.New("invalid state")
	}
	refresh := "refresh-private"
	sid := "provider-session"
	return &sdk.TSAuthOIDCLogin{Identity: sdk.TSAuthVerifiedIDToken{Issuer: "https://auth.example/notes", Subject: "alice", ExpiresAt: time.Now().Add(time.Hour), SessionID: &sid}, Tokens: sdk.TSAuthTokenResponse{AccessToken: "access-private", RefreshToken: &refresh, ExpiresIn: 3600}}, nil
}
func (f *fakeIdentity) Introspect(context.Context, string) (*sdk.TSAuthIntrospection, error) {
	subject := "alice"
	return &sdk.TSAuthIntrospection{Active: f.active, Sub: &subject}, nil
}
func (f *fakeIdentity) Revoke(context.Context, string) error { f.revocations++; return f.revokeError }
func (f *fakeIdentity) EndSessionURL(context.Context, string, string) (string, error) {
	return "https://auth.example/notes/oauth/logout", nil
}

type fakeDocuments struct {
	point       *sdk.DocumentPoint
	commits     int
	last        sdk.CommitRequest
	scope       sdk.DocumentScope
	commitError error
	status      sdk.TransactionState
}

func (f *fakeDocuments) Get(_ context.Context, scope sdk.DocumentScope, _, _ sdk.UUID) (*sdk.DocumentPoint, error) {
	f.scope = scope
	return f.point, nil
}
func (f *fakeDocuments) Commit(_ context.Context, scope sdk.DocumentScope, request sdk.CommitRequest) (*sdk.CommitResponse, error) {
	f.commits++
	f.scope = scope
	f.last = request
	return &sdk.CommitResponse{}, f.commitError
}
func (f *fakeDocuments) TransactionStatus(_ context.Context, _ sdk.DocumentScope, _ sdk.TransactionStatusRequest) (*sdk.TransactionStatusResponse, error) {
	return &sdk.TransactionStatusResponse{State: f.status}, nil
}

func testApplication(t *testing.T) (*application, *fakeIdentity, *fakeDocuments, *browserSession) {
	t.Helper()
	identity := &fakeIdentity{active: true}
	documents := &fakeDocuments{}
	config := configuration{origin: "https://app.example", issuer: "https://auth.example/notes", scope: sdk.DocumentScope{Tenant: "11111111-1111-4111-8111-111111111111", Database: "22222222-2222-4222-8222-222222222222", Shard: sdk.DecimalU64(^uint64(0))}, collection: "44444444-4444-4444-8444-444444444444", schemaGeneration: 1, ownerField: 7, bodyField: 9}
	app := newApplication(config, identity, documents)
	session := &browserSession{identity: sdk.TSAuthVerifiedIDToken{Issuer: config.issuer, Subject: "alice"}, tokens: sdk.TSAuthTokenResponse{AccessToken: "access-private"}, csrf: "csrf-private", expires: time.Now().Add(time.Hour)}
	app.sessions["session-private"] = session
	return app, identity, documents, session
}
func request(t *testing.T, app *application, method, path string, values url.Values) *httptest.ResponseRecorder {
	t.Helper()
	var body string
	if values != nil {
		body = values.Encode()
	}
	r := httptest.NewRequest(method, path, strings.NewReader(body))
	r.AddCookie(&http.Cookie{Name: "__Host-tsref-session", Value: "session-private"})
	r.Header.Set("Origin", app.config.origin)
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	w := httptest.NewRecorder()
	app.handler().ServeHTTP(w, r)
	return w
}
func livePoint(t *testing.T, app *application, owner, body string) *sdk.DocumentPoint {
	t.Helper()
	ownerValue, err := sdk.UTF8Value(owner)
	if err != nil {
		t.Fatal(err)
	}
	bodyValue, err := sdk.UTF8Value(body)
	if err != nil {
		t.Fatal(err)
	}
	version := sdk.Hex32{1}
	return &sdk.DocumentPoint{State: sdk.PointValue, StateVersion: &version, Siblings: []sdk.DocumentSibling{{Kind: "live", Fields: []sdk.DocumentField{{ID: app.config.ownerField, Value: ownerValue}, {ID: app.config.bodyField, Value: bodyValue}}}}}
}
func TestCreateBindsVerifiedOwnerAndUsesNativeScope(t *testing.T) {
	app, _, documents, session := testApplication(t)
	w := request(t, app, "POST", "/notes", url.Values{"csrf": {session.csrf}, "body": {"<script>private note</script>"}, "owner": {"mallory"}})
	if w.Code != 303 || documents.commits != 1 {
		t.Fatalf("create status=%d commits=%d", w.Code, documents.commits)
	}
	if documents.scope != app.config.scope || documents.last.Authority.Mode != sdk.AuthorityLeaderless || documents.last.Writes[0].Condition.Kind != sdk.ConditionAbsent {
		t.Fatal("native scope or absent condition changed")
	}
	write := documents.last.Writes[0]
	owner, ok := write.Value.Fields[0].Value.UTF8()
	if !ok || owner != app.owner(session) {
		t.Fatal("owner came from browser input")
	}
	documents.point = livePoint(t, app, owner, "<script>private note</script>")
	w = request(t, app, "GET", w.Header().Get("Location"), nil)
	if w.Code != 200 || strings.Contains(w.Body.String(), "<script>") || !strings.Contains(w.Body.String(), "&lt;script&gt;") {
		t.Fatalf("owned read/escaping failed: %d", w.Code)
	}
	if strings.Contains(w.Body.String(), "access-private") {
		t.Fatal("OAuth credential exposed")
	}
}
func TestOtherOwnerAbsentAndConflictReturnSameScopedNotFound(t *testing.T) {
	app, _, documents, session := testApplication(t)
	var expected string
	for _, point := range []*sdk.DocumentPoint{livePoint(t, app, "another owner", "secret"), {State: sdk.PointAbsent}, {State: sdk.PointConflict, Siblings: []sdk.DocumentSibling{{Kind: "live"}, {Kind: "live"}}}} {
		documents.point = point
		w := request(t, app, "GET", "/notes/"+string(recordID), nil)
		if w.Code != 404 || strings.Contains(w.Body.String(), "secret") {
			t.Fatalf("foreign record leak: %d", w.Code)
		}
		code := strings.Split(w.Body.String(), "support_id")[0]
		if expected != "" && code != expected {
			t.Fatal("foreign/conflict/absent distinction exposed")
		}
		expected = code
		w = request(t, app, "POST", "/notes/"+string(recordID), url.Values{"csrf": {session.csrf}, "body": {"replacement"}, "version": {"ignored"}})
		if w.Code != 404 || documents.commits != 0 {
			t.Fatal("foreign record was mutated")
		}
	}
}
func TestCsrfAndRevokedSessionPreventNativeRequests(t *testing.T) {
	app, identity, documents, session := testApplication(t)
	w := request(t, app, "POST", "/notes", url.Values{"csrf": {"wrong"}, "body": {"note"}})
	if w.Code != 403 || documents.commits != 0 {
		t.Fatal("CSRF did not fail closed")
	}
	identity.active = false
	w = request(t, app, "POST", "/notes", url.Values{"csrf": {session.csrf}, "body": {"note"}})
	if w.Code != 401 || documents.commits != 0 {
		t.Fatal("revoked session performed a write")
	}
}
func TestOwnedUpdateUsesVersionConditionAndPreservesOwner(t *testing.T) {
	app, _, documents, session := testApplication(t)
	documents.point = livePoint(t, app, app.owner(session), "original")
	w := request(t, app, "POST", "/notes/"+string(recordID), url.Values{"csrf": {session.csrf}, "body": {"new"}, "version": {"stale"}})
	if w.Code != 409 || documents.commits != 0 {
		t.Fatal("stale update committed")
	}
	w = request(t, app, "POST", "/notes/"+string(recordID), url.Values{"csrf": {session.csrf}, "body": {"new"}, "version": {documents.point.StateVersion.String()}})
	if w.Code != 303 || documents.last.Writes[0].Condition.Kind != sdk.ConditionVersion || *documents.last.Writes[0].Condition.Version != *documents.point.StateVersion {
		t.Fatal("update lost exact native CAS version")
	}
}
func TestOwnedUpdateCommitTimePreconditionRejection(t *testing.T) {
	for _, test := range []struct {
		name        string
		err         error
		status      int
		code        string
		keepPending bool
	}{
		{
			name:   "definitive conditional rejection",
			err:    &sdk.CommitError{Err: &sdk.APIError{Status: http.StatusPreconditionFailed, Code: "precondition_failed", Message: "private native details"}},
			status: http.StatusConflict,
			code:   "record_changed",
		},
		{
			name:        "uncertain conditional rejection",
			err:         &sdk.CommitError{Uncertain: true, Err: &sdk.APIError{Status: http.StatusPreconditionFailed, Code: "precondition_failed", Message: "private native details"}},
			status:      http.StatusServiceUnavailable,
			code:        "transaction_unresolved",
			keepPending: true,
		},
		{
			name:   "other precondition error",
			err:    &sdk.CommitError{Err: &sdk.APIError{Status: http.StatusPreconditionFailed, Code: "other_error", Message: "private native details"}},
			status: http.StatusServiceUnavailable,
			code:   "database_capability_unavailable",
		},
		{
			name:   "other status",
			err:    &sdk.CommitError{Err: &sdk.APIError{Status: http.StatusConflict, Code: "precondition_failed", Message: "private native details"}},
			status: http.StatusServiceUnavailable,
			code:   "database_capability_unavailable",
		},
		{
			name:   "missing commit classification",
			err:    &sdk.APIError{Status: http.StatusPreconditionFailed, Code: "precondition_failed", Message: "private native details"},
			status: http.StatusServiceUnavailable,
			code:   "database_capability_unavailable",
		},
	} {
		t.Run(test.name, func(t *testing.T) {
			app, _, documents, session := testApplication(t)
			documents.point = livePoint(t, app, app.owner(session), "original")
			documents.commitError = test.err
			w := request(t, app, http.MethodPost, "/notes/"+string(recordID), url.Values{"csrf": {session.csrf}, "body": {"new"}, "version": {documents.point.StateVersion.String()}})
			if w.Code != test.status || !strings.Contains(w.Body.String(), test.code) || strings.Contains(w.Body.String(), "private native details") {
				t.Fatalf("commit rejection status=%d body=%s", w.Code, w.Body.String())
			}
			if documents.commits != 1 || documents.last.Writes[0].Condition.Kind != sdk.ConditionVersion || *documents.last.Writes[0].Condition.Version != *documents.point.StateVersion {
				t.Fatal("commit-time rejection did not preserve the original single conditional dispatch")
			}
			if (session.pending != nil) != test.keepPending {
				t.Fatal("commit rejection lost its definitive or uncertain classification")
			}
			if test.keepPending && session.pending.request.TransactionID != documents.last.TransactionID {
				t.Fatal("uncertain rejection lost its original transaction")
			}
			if test.status == http.StatusConflict && !strings.Contains(w.Body.String(), "Reload before updating it.") {
				t.Fatal("conditional rejection did not request a reload")
			}
		})
	}
}
func TestUncertainCommitBlocksFreshWritesAndUnknownNeverMeansAbort(t *testing.T) {
	app, _, documents, session := testApplication(t)
	documents.commitError = &sdk.CommitError{Uncertain: true, Err: errors.New("raw-secret")}
	documents.status = sdk.TransactionUnknown
	values := url.Values{"csrf": {session.csrf}, "body": {"note"}}
	w := request(t, app, "POST", "/notes", values)
	if w.Code != 503 || session.pending == nil || strings.Contains(w.Body.String(), "raw-secret") {
		t.Fatal("uncertainty lost or error leaked")
	}
	original := session.pending.request.TransactionID
	w = request(t, app, "POST", "/notes", values)
	if w.Code != 409 || documents.commits != 1 {
		t.Fatal("uncertain write recreated")
	}
	w = request(t, app, "GET", "/operation", nil)
	if w.Code != 409 || session.pending == nil || session.pending.request.TransactionID != original {
		t.Fatal("unknown treated as abort")
	}
}
func TestAuthorizationAttemptConsumedBeforeExchangeAndCookieIsSecure(t *testing.T) {
	app, identity, _, _ := testApplication(t)
	r := httptest.NewRequest("GET", "/sign-in", nil)
	w := httptest.NewRecorder()
	app.handler().ServeHTTP(w, r)
	cookies := w.Result().Cookies()
	if len(cookies) != 1 || !cookies[0].Secure || !cookies[0].HttpOnly || cookies[0].Path != "/" {
		t.Fatal("authorization cookie is not server bound")
	}
	for i := range 2 {
		r = httptest.NewRequest("GET", "/callback?code=code&state=state", nil)
		r.AddCookie(cookies[0])
		w = httptest.NewRecorder()
		app.handler().ServeHTTP(w, r)
		if i == 0 && w.Code != 303 {
			t.Fatalf("first callback status=%d", w.Code)
		}
		if i == 1 && w.Code != 400 {
			t.Fatal("callback replay accepted")
		}
	}
	if identity.exchanges != 1 {
		t.Fatal("authorization code exchange repeated")
	}
}
func TestFailedRevocationClosesDataAccessAndRemainsRetryable(t *testing.T) {
	app, identity, _, session := testApplication(t)
	identity.revokeError = errors.New("private provider details")
	w := request(t, app, "POST", "/logout", url.Values{"csrf": {session.csrf}})
	if w.Code != 503 || !session.closing || strings.Contains(w.Body.String(), "private provider details") {
		t.Fatal("revocation failure was hidden or leaked")
	}
	w = request(t, app, "GET", "/notes/"+string(recordID), nil)
	if w.Code != 401 {
		t.Fatal("sign-out pending allowed data access")
	}
	w = request(t, app, "GET", "/", nil)
	if w.Code != 200 || !strings.Contains(w.Body.String(), "Retry sign-out") || strings.Contains(w.Body.String(), "Create note") {
		t.Fatal("pending revocation cannot be retried after refreshing")
	}
	identity.revokeError = nil
	w = request(t, app, "POST", "/logout", url.Values{"csrf": {session.csrf}})
	if w.Code != 200 || app.sessions["session-private"] != nil || identity.revocations != 2 {
		t.Fatal("revocation retry did not complete")
	}
	if w.Header().Get("Location") != "" || !strings.Contains(w.Body.String(), `href="https://auth.example/notes/oauth/logout"`) || !strings.Contains(w.Header().Get("Content-Security-Policy"), "form-action 'self'") {
		t.Fatal("provider sign-out did not use an ordinary validated link under the restrictive form CSP")
	}
}
func TestHostedConfigurationCannotOverrideAtlasAndCatalogFieldIdsAreRequired(t *testing.T) {
	values := map[string]string{"REFERENCE_ORIGIN": "https://app.example", "TERRASCALE_AUTH_ISSUER": "https://auth.example/notes", "TERRASCALE_CLIENT_ID": "client", "TERRASCALE_CLIENT_SECRET": "private", "TERRASCALE_DATA_KEY": "private-data", "TERRASCALE_TENANT_ID": "11111111-1111-4111-8111-111111111111", "TERRASCALE_DATABASE_ID": "22222222-2222-4222-8222-222222222222", "TERRASCALE_COLLECTION_ID": "44444444-4444-4444-8444-444444444444", "TERRASCALE_SHARD": "18446744073709551615", "TERRASCALE_SCHEMA_GENERATION": "1", "TERRASCALE_SCHEMA_DIGEST": strings.Repeat("0", 64), "TERRASCALE_OWNER_FIELD_ID": "7", "TERRASCALE_BODY_FIELD_ID": "9"}
	getenv := func(key string) string { return values[key] }
	c, err := loadConfiguration(getenv)
	if err != nil || c.dataOrigin != "" || uint64(c.scope.Shard) != ^uint64(0) {
		t.Fatalf("hosted configuration=%v", err)
	}
	values["REFERENCE_DEVELOPMENT_DATA_URL"] = "https://gru.api.terrascale.tech"
	if _, err = loadConfiguration(getenv); err == nil {
		t.Fatal("hosted Atlas selection bypassed")
	}
	values["REFERENCE_DEVELOPMENT"] = "true"
	if _, err = loadConfiguration(getenv); err != nil {
		t.Fatal("explicit development origin rejected")
	}
	delete(values, "TERRASCALE_OWNER_FIELD_ID")
	if _, err = loadConfiguration(getenv); err == nil {
		t.Fatal("unprovisioned owner field accepted")
	}
}
