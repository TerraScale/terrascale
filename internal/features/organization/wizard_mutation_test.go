package organization

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"net/url"
	"reflect"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
)

func wizardMutationRequest(step, organizationID string) *http.Request {
	form := url.Values{
		"step": {step}, "organizationID": {organizationID},
		"plan": {"starter"}, "name": {"New project"}, "region": {"GRU"},
	}
	r := httptest.NewRequest(http.MethodPost, "/api/v1/dashboard/organization/new-organization", strings.NewReader(form.Encode()))
	r.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	return r
}

func wizardMutationFixture(t *testing.T, step string) (core.Store, core.Principal, string) {
	t.Helper()
	store, owner, organizationID := checkoutRecoveryFixture(t)
	if step == "project" {
		if err := store.Update(t.Context(), owner.AccountID, func(a *core.Account) error {
			org := a.Organizations[organizationID]
			org.BillingStatus = "active"
			a.Organizations[org.ID] = org
			return nil
		}); err != nil {
			t.Fatal(err)
		}
	}
	return store, owner, organizationID
}

func TestOrganizationWizardPreservesLookupErrors(t *testing.T) {
	for _, step := range []string{"payment-method", "project"} {
		for _, tc := range []struct {
			name   string
			failAt int
			err    error
			status int
		}{
			{"target outage", 1, fmt.Errorf("snapshot: %w", core.ErrUnavailable), http.StatusServiceUnavailable},
			{"snapshot outage", 2, fmt.Errorf("snapshot: %w", core.ErrUnavailable), http.StatusServiceUnavailable},
			{"target backend", 1, errors.New("backend read failed"), http.StatusBadRequest},
			{"snapshot backend", 2, errors.New("backend read failed"), http.StatusBadRequest},
			{"target canceled", 1, context.Canceled, http.StatusBadRequest},
			{"target deadline", 1, context.DeadlineExceeded, http.StatusBadRequest},
			{"snapshot deadline", 2, context.DeadlineExceeded, http.StatusBadRequest},
			{"target missing", 1, fmt.Errorf("snapshot: %w", core.ErrNotFound), http.StatusSeeOther},
			{"target forbidden", 1, fmt.Errorf("snapshot: %w", core.ErrForbidden), http.StatusSeeOther},
			{"target unauthorized", 1, fmt.Errorf("snapshot: %w", core.ErrUnauthorized), http.StatusSeeOther},
			{"snapshot missing", 2, fmt.Errorf("snapshot: %w", core.ErrNotFound), http.StatusSeeOther},
			{"forbidden", 2, fmt.Errorf("snapshot: %w", core.ErrForbidden), http.StatusForbidden},
			{"unauthorized", 2, fmt.Errorf("snapshot: %w", core.ErrUnauthorized), http.StatusUnauthorized},
			{"canceled", 2, context.Canceled, http.StatusBadRequest},
		} {
			t.Run(step+"/"+tc.name, func(t *testing.T) {
				store, owner, organizationID := wizardMutationFixture(t, step)
				before, err := store.SnapshotAs(t.Context(), owner)
				if err != nil {
					t.Fatal(err)
				}
				lookup := &recoveryLookupStore{Store: store, failAt: tc.failAt, err: tc.err}
				page := &recoveryPage{principal: owner}
				deps := core.Deps{Store: lookup, Sessions: page, CSRF: page, Demo: true}
				response := httptest.NewRecorder()
				newOrganization(deps, NewService(lookup, page))(response, wizardMutationRequest(step, organizationID))
				wantLocation := ""
				if tc.status == http.StatusSeeOther {
					wantStep, wantError := "organization", "The organization change could not be completed."
					if errors.Is(tc.err, core.ErrNotFound) && tc.failAt == 1 {
						wantError = "The selected organization item was not found."
					} else if tc.failAt == 2 && step == "project" {
						wantStep, wantError = "payment", "Complete Business billing before creating resources."
					} else if errors.Is(tc.err, core.ErrForbidden) || tc.failAt == 2 {
						wantError = "You do not have permission to make that change."
					}
					wantLocation = "/organization?newOrganization=" + wantStep + "&error=" + url.QueryEscape(wantError)
				} else if response.Body.String() != http.StatusText(tc.status)+"\n" {
					t.Fatalf("error body = %q, want %q", response.Body.String(), http.StatusText(tc.status))
				}
				if response.Code != tc.status || response.Header().Get("Location") != wantLocation || page.started != (core.Principal{}) || lookup.calls != tc.failAt {
					t.Fatalf("wizard = %d, redirect %q, started %+v, lookups %d; want %d, redirect %q without switching after %d lookups", response.Code, response.Header().Get("Location"), page.started, lookup.calls, tc.status, wantLocation, tc.failAt)
				}
				after, err := store.SnapshotAs(t.Context(), owner)
				if err != nil || !reflect.DeepEqual(before, after) {
					t.Fatalf("failed wizard changed account state: %v", err)
				}
			})
		}
	}
}

func TestOrganizationWizardPreservesValidationAndSuccess(t *testing.T) {
	for _, step := range []string{"payment-method", "project"} {
		for _, condition := range []string{"valid", "personal", "billing", "owner"} {
			t.Run(step+"/"+condition, func(t *testing.T) {
				store, owner, organizationID := wizardMutationFixture(t, step)
				if condition == "personal" {
					organizationID = owner.OrganizationID
				} else if condition != "valid" {
					if err := store.Update(t.Context(), owner.AccountID, func(a *core.Account) error {
						org := a.Organizations[organizationID]
						if condition == "owner" {
							org.OwnerUserID = "other"
						} else {
							org.BillingStatus = "inactive"
						}
						a.Organizations[org.ID] = org
						return nil
					}); err != nil {
						t.Fatal(err)
					}
				}
				before, err := store.SnapshotAs(t.Context(), owner)
				if err != nil {
					t.Fatal(err)
				}
				page := &recoveryPage{principal: owner}
				deps := core.Deps{Store: store, Sessions: page, CSRF: page, Demo: true}
				response := httptest.NewRecorder()
				newOrganization(deps, NewService(store, page))(response, wizardMutationRequest(step, organizationID))
				location, err := url.Parse(response.Header().Get("Location"))
				if err != nil {
					t.Fatal(err)
				}
				if condition == "valid" {
					wantStatus, wantPath := http.StatusSeeOther, "/organization"
					wantReturn := ""
					if step == "payment-method" {
						wantStatus, wantPath = http.StatusTemporaryRedirect, "/billing/checkout"
						wantReturn = "/organization?newOrganization=project&organizationID=" + url.QueryEscape(organizationID)
					}
					if response.Code != wantStatus || location.Path != wantPath || location.Query().Get("return") != wantReturn || page.started.OrganizationID != organizationID {
						t.Fatalf("valid wizard = %d, %s, started %+v; want %d, %s for %s", response.Code, location, page.started, wantStatus, wantPath, organizationID)
					}
					after, err := store.SnapshotAs(t.Context(), owner)
					if err != nil || after.ActiveOrganizationID != organizationID {
						t.Fatalf("valid wizard did not select target organization: %v", err)
					}
					if step == "project" && len(after.Organizations[organizationID].Projects) != 1 {
						t.Fatal("valid project step did not create its first project")
					}
				} else {
					wantStep, wantError := "organization", "You do not have permission to make that change."
					if step == "project" {
						wantStep, wantError = "payment", "Complete Business billing before creating resources."
					}
					if response.Code != http.StatusSeeOther || location.Query().Get("newOrganization") != wantStep || location.Query().Get("error") != wantError || page.started != (core.Principal{}) {
						t.Fatalf("invalid wizard = %d, %s, started %+v; want unchanged domain redirect to %s with %q", response.Code, location, page.started, wantStep, wantError)
					}
					after, err := store.SnapshotAs(t.Context(), owner)
					if err != nil || !reflect.DeepEqual(before, after) {
						t.Fatalf("invalid wizard changed account state: %v", err)
					}
				}
			})
		}
	}
}
