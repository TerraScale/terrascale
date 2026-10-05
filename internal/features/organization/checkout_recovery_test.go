package organization

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
)

// Only the selected recovery lookup fails; subsequent page reads would succeed.
type recoveryLookupStore struct {
	core.Store
	failAt int
	calls  int
	err    error
}

func (s *recoveryLookupStore) SnapshotAs(ctx context.Context, p core.Principal) (core.Account, error) {
	s.calls++
	if s.calls == s.failAt {
		return core.Account{}, s.err
	}
	return s.Store.SnapshotAs(ctx, p)
}

type recoveryPage struct {
	core.Sessions
	core.CSRF
	principal core.Principal
	started   core.Principal
	rendered  bool
}

func (p *recoveryPage) Principal(*http.Request) (core.Principal, bool) {
	return p.principal, true
}

func (p *recoveryPage) Start(_ http.ResponseWriter, _ *http.Request, principal core.Principal) error {
	p.started = principal
	return nil
}

func (*recoveryPage) Token(http.ResponseWriter, *http.Request) string { return "csrf" }

func (*recoveryPage) Verify(*http.Request) error { return nil }

func (p *recoveryPage) Page(w http.ResponseWriter, r *http.Request, page core.Page) error {
	p.rendered = true
	return page.View(page).Render(r.Context(), w)
}

func checkoutRecoveryFixture(t *testing.T) (core.Store, core.Principal, string) {
	t.Helper()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "recovery@example.test", PasswordHash: "hash"})
	if err != nil {
		t.Fatal(err)
	}
	owner := account.Principal()
	service := NewService(store, nil)
	org, err := service.CreateBusiness(t.Context(), owner, "Checkout organization")
	if err != nil {
		t.Fatal(err)
	}
	target, err := service.ForOrganization(t.Context(), owner, org.ID)
	if err != nil {
		t.Fatal(err)
	}
	if err := store.UpdateAs(t.Context(), target, func(a *core.Account) error {
		org := a.Organizations[target.OrganizationID]
		org.CheckoutSessions["checkout"] = core.CheckoutSession{
			OwnerUserID: owner.UserID, OrganizationID: org.ID, Status: "canceled",
			ReturnPath: "/organization?newOrganization=project&organizationID=" + url.QueryEscape(org.ID),
		}
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if err := service.SwitchOrganization(t.Context(), owner, owner.OrganizationID); err != nil {
		t.Fatal(err)
	}
	return store, owner, org.ID
}

func recoveryRequest(organizationID string) *http.Request {
	return httptest.NewRequest(http.MethodGet, "/organization?newOrganization=project&payment=canceled&organizationID="+url.QueryEscape(organizationID), nil)
}

func TestResumeCheckoutOrganizationLookupErrors(t *testing.T) {
	for _, failAt := range []int{1, 2} {
		for _, tc := range []struct {
			name     string
			err      error
			fallback bool
		}{
			{name: "unavailable", err: fmt.Errorf("snapshot: %w", core.ErrUnavailable)},
			{name: "backend", err: errors.New("backend read failed")},
			{name: "canceled", err: context.Canceled},
			{name: "deadline", err: context.DeadlineExceeded},
			{name: "missing", err: fmt.Errorf("snapshot: %w", core.ErrNotFound), fallback: true},
			{name: "forbidden", err: fmt.Errorf("snapshot: %w", core.ErrForbidden), fallback: true},
			{name: "unauthorized", err: fmt.Errorf("snapshot: %w", core.ErrUnauthorized), fallback: true},
		} {
			t.Run(fmt.Sprintf("lookup%d/%s", failAt, tc.name), func(t *testing.T) {
				store, owner, organizationID := checkoutRecoveryFixture(t)
				lookup := &recoveryLookupStore{Store: store, failAt: failAt, err: tc.err}
				got, verified, err := resumeCheckoutOrganization(core.Deps{}, NewService(lookup, nil), httptest.NewRecorder(), recoveryRequest(organizationID), owner)
				wantErr := tc.err
				if tc.fallback {
					wantErr = nil
				}
				if err != wantErr || got != owner || verified || lookup.calls != failAt {
					t.Fatalf("recovery = %+v, %t, %v after %d lookups; want original principal, false, %v after %d", got, verified, err, lookup.calls, wantErr, failAt)
				}
			})
		}
	}
}

func TestOrganizationPageReportsCheckoutRecoveryOutage(t *testing.T) {
	for _, failAt := range []int{1, 2} {
		t.Run(fmt.Sprintf("lookup%d", failAt), func(t *testing.T) {
			store, owner, organizationID := checkoutRecoveryFixture(t)
			lookup := &recoveryLookupStore{Store: store, failAt: failAt, err: fmt.Errorf("snapshot: %w", core.ErrUnavailable)}
			page := &recoveryPage{principal: owner}
			deps := core.Deps{Store: lookup, Sessions: page, CSRF: page, Render: page, Demo: true}
			response := httptest.NewRecorder()
			organizationPage(deps, NewService(lookup, page))(response, recoveryRequest(organizationID))
			if response.Code != http.StatusServiceUnavailable || page.rendered || page.started != (core.Principal{}) || lookup.calls != failAt {
				t.Fatalf("page = %d, rendered %t, started %+v, lookups %d; want 503 without rendering or switching", response.Code, page.rendered, page.started, lookup.calls)
			}
		})
	}
}

func TestResumeCheckoutOrganizationFallbackAndVerifiedReturn(t *testing.T) {
	for _, tc := range []struct {
		name     string
		mutate   func(*core.Organization)
		missing  bool
		verified bool
	}{
		{name: "verified cancellation", verified: true},
		{name: "missing organization", missing: true},
		{name: "inactive membership", mutate: func(org *core.Organization) {
			member := org.Members[org.OwnerUserID]
			member.Status = "removed"
			org.Members[org.OwnerUserID] = member
		}},
		{name: "personal organization", mutate: func(org *core.Organization) { org.Kind = kindPersonal }},
		{name: "not owner", mutate: func(org *core.Organization) { org.OwnerUserID = "other" }},
		{name: "unverified checkout", mutate: func(org *core.Organization) { clear(org.CheckoutSessions) }},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store, owner, organizationID := checkoutRecoveryFixture(t)
			if tc.mutate != nil {
				if err := store.Update(t.Context(), owner.AccountID, func(a *core.Account) error {
					org := a.Organizations[organizationID]
					tc.mutate(&org)
					a.Organizations[org.ID] = org
					return nil
				}); err != nil {
					t.Fatal(err)
				}
			}
			if tc.missing {
				organizationID = "missing"
			}
			page := &recoveryPage{principal: owner}
			got, verified, err := resumeCheckoutOrganization(core.Deps{Store: store, Sessions: page}, NewService(store, page), httptest.NewRecorder(), recoveryRequest(organizationID), owner)
			if err != nil || verified != tc.verified {
				t.Fatalf("recovery = %+v, %t, %v; want verified %t without error", got, verified, err, tc.verified)
			}
			if tc.verified {
				if got.OrganizationID != organizationID || page.started != got {
					t.Fatalf("verified return = %+v, started %+v; want checkout organization %s", got, page.started, organizationID)
				}
			} else if got != owner || page.started != (core.Principal{}) {
				t.Fatalf("fallback = %+v, started %+v; want original principal without switching", got, page.started)
			}
		})
	}
}
