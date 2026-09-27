package databases

import (
	"net/http"
	"net/http/httptest"
	"net/url"
	"regexp"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func TestFormErrorsShowOnlyKnownCodes(t *testing.T) {
	if formError(ErrNameTaken) == "" {
		t.Fatal("a known code has a message")
	}
	if got := formError("Your account is suspended. Call 555-0100"); got != "" {
		t.Fatalf("an unknown code must show nothing, got %q", got)
	}
}

func TestFormFailedRedirectsBrowsersAndAnswersAPIClients(t *testing.T) {
	browser := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/databases", nil)
	browser.Header.Set("Accept", "text/html")
	recorder := httptest.NewRecorder()
	FormFailed(recorder, browser, http.StatusConflict, "/databases", ErrNameTaken, url.Values{"create": {"1"}, "name": {"core"}})
	location, _ := url.Parse(recorder.Header().Get("Location"))
	if recorder.Code != http.StatusSeeOther || location.Path != "/databases" || location.Query().Get("error") != ErrNameTaken || location.Query().Get("name") != "core" {
		t.Fatalf("browser redirect = %d %s", recorder.Code, recorder.Header().Get("Location"))
	}

	api := httptest.NewRecorder()
	FormFailed(api, httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/databases", nil), http.StatusConflict, "/databases", ErrNameTaken, nil)
	if api.Code != http.StatusConflict || api.Body.String() != formErrors[ErrNameTaken]+"\n" {
		t.Fatalf("API answer = %d %q", api.Code, api.Body.String())
	}
}

func TestNameInputPatternMatchesTheServerRule(t *testing.T) {
	input := regexp.MustCompile(`^(?:` + namePatternHTML + `)$`)
	for _, name := range []string{"a", "core-primary", "db1", "1db", "a-b-c", "trailing-", "-leading", "Upper", "under_score", "a b", "", "x123456789x123456789x123456789x123456789x123456789x123456789xyz", "x123456789x123456789x123456789x123456789x123456789x123456789xyzw"} {
		if input.MatchString(name) != core.ValidDatabaseName(name) {
			t.Errorf("%q: the input pattern says %v, the server says %v", name, input.MatchString(name), core.ValidDatabaseName(name))
		}
	}
}

func TestByteSize(t *testing.T) {
	for n, want := range map[int64]string{0: "0 B", 727: "727 B", 1024: "1.0 KiB", 1536: "1.5 KiB", 5 << 20: "5.0 MiB", 3 << 30: "3.0 GiB"} {
		if got := byteSize(n); got != want {
			t.Errorf("byteSize(%d) = %q, want %q", n, got, want)
		}
	}
}
