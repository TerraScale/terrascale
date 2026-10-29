package tsauth

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

func TestAuthRequiredEnvelopesAndBoundedReads(t *testing.T) {
	for _, body := range []string{`null`, `{}`, `{"items":null}`, `{"items":[null]}`, `{"items":[{}]}`, strings.Repeat(" ", 8<<20) + `{}`} {
		calls := 0
		hc := &http.Client{Transport: authResponseTransport(func(*http.Request) (*http.Response, error) {
			calls++
			return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader(body)), Header: make(http.Header)}, nil
		})}
		var out Page[Organization]
		err := sendJSON(t.Context(), hc, http.MethodGet, "https://auth.example/organizations", "Bearer fixture", nil, &out)
		var protocol *ResponseContractError
		if !errors.As(err, &protocol) || calls != 1 {
			t.Fatalf("error=%v calls=%d", err, calls)
		}
	}
	hc := &http.Client{Transport: authResponseTransport(func(*http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader(`{"items":[],"next":null,"future":true}`)), Header: make(http.Header)}, nil
	})}
	var out Page[Organization]
	if err := sendJSON(t.Context(), hc, http.MethodGet, "https://auth.example/organizations", "Bearer fixture", nil, &out); err != nil {
		t.Fatal(err)
	}
}

func TestAuthMissingRequiredMembersAcrossFamilies(t *testing.T) {
	for _, model := range []any{&Organization{}, &Member{}, &Project{}, &ProjectMember{}, &Invitation{}, &CreatedInvitation{}, &InvitationLookup{}, &AcceptedInvitation{}, &OwnershipTransfer{}, &APIKey{}, &CreatedAPIKey{}, &AuditEvent{}} {
		hc := &http.Client{Transport: authResponseTransport(func(*http.Request) (*http.Response, error) {
			return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader(`{}`)), Header: make(http.Header)}, nil
		})}
		var protocol *ResponseContractError
		if err := sendJSON(t.Context(), hc, http.MethodGet, "https://auth.example/resource", "Bearer fixture", nil, model); !errors.As(err, &protocol) {
			t.Fatalf("%T error=%v", model, err)
		}
	}
}

func TestOAuthOversizeResponseCannotHideBehindValidPrefix(t *testing.T) {
	hc := &http.Client{Transport: authResponseTransport(func(*http.Request) (*http.Response, error) {
		return &http.Response{StatusCode: 200, Body: io.NopCloser(strings.NewReader(strings.Repeat(" ", 64<<10) + `{}`)), Header: make(http.Header)}, nil
	})}
	_, err := oauthPost(context.Background(), hc, "https://auth.example/oauth/token", nil, "", "")
	var protocol *ResponseContractError
	if !errors.As(err, &protocol) {
		t.Fatal(err)
	}
}

type authResponseTransport func(*http.Request) (*http.Response, error)

func (f authResponseTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
