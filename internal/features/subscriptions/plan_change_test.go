package subscriptions

import (
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/payments"
)

type planChangeSessions struct{ principal core.Principal }

func (sessions planChangeSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return nil
}
func (sessions planChangeSessions) Principal(*http.Request) (core.Principal, bool) {
	return sessions.principal, true
}
func (sessions planChangeSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}
func (sessions planChangeSessions) End(http.ResponseWriter, *http.Request) error     { return nil }
func (sessions planChangeSessions) RevokeUser(context.Context, string, string) error { return nil }

func TestPersonalPlanChangeUsesExistingSubscriptionAndSchedulesFree(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "plan-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	service := NewBillingService(store)
	session, err := service.BeginDemoCheckout(t.Context(), principal, "starter", "monthly", "/usage?tab=billing")
	if err != nil {
		t.Fatal(err)
	}
	if _, err := service.CompleteDemoCheckout(t.Context(), principal, session.ID, false); err != nil {
		t.Fatal(err)
	}
	before, _ := store.SnapshotAs(t.Context(), principal)
	originalID := before.Organizations[principal.OrganizationID].Billing.SubscriptionID
	if originalID == "" {
		t.Fatal("demo checkout did not bind a subscription")
	}
	if _, err := service.BeginDemoCheckout(t.Context(), principal, "pro", "monthly", "/usage?tab=billing"); !errors.Is(err, payments.ErrSubscriptionManaged) {
		t.Fatalf("second checkout = %v, want the managed subscription conflict", err)
	}
	mux := http.NewServeMux()
	payments.Mount(mux, core.Deps{Store: store, Sessions: planChangeSessions{principal: principal}, Demo: true}, service, nil)
	post := func(path string, values url.Values) *httptest.ResponseRecorder {
		t.Helper()
		request := httptest.NewRequest(http.MethodPost, path, strings.NewReader(values.Encode()))
		request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, request)
		return response
	}
	upgrade := post("/billing/change", url.Values{"plan": {"pro"}, "interval": {"annual"}})
	if upgrade.Code != http.StatusSeeOther {
		t.Fatalf("upgrade = %d: %s", upgrade.Code, upgrade.Body.String())
	}
	current, _ := store.SnapshotAs(t.Context(), principal)
	org := current.Organizations[principal.OrganizationID]
	if org.PlanID != "plan_pro" || org.Billing.Interval != "annual" || org.Billing.SubscriptionID != originalID {
		t.Fatalf("upgrade created another subscription or failed to change plan: %#v", org.Billing)
	}
	downgrade := post("/billing/change", url.Values{"plan": {"starter"}, "interval": {"monthly"}})
	if downgrade.Code != http.StatusSeeOther {
		t.Fatalf("downgrade = %d: %s", downgrade.Code, downgrade.Body.String())
	}
	current, _ = store.SnapshotAs(t.Context(), principal)
	org = current.Organizations[principal.OrganizationID]
	if org.PlanID != "plan_pro" || org.Billing.ScheduledPlanKey != "starter" || org.Billing.ScheduledInterval != "monthly" || org.Billing.ChangeVersion != 1 {
		t.Fatalf("downgrade was applied early or not scheduled: %#v", org.Billing)
	}
	if response := post("/billing/change/undo", nil); response.Code != http.StatusSeeOther {
		t.Fatalf("undo = %d", response.Code)
	}
	if response := post("/billing/change", url.Values{"plan": {"starter"}, "interval": {"monthly"}}); response.Code != http.StatusSeeOther {
		t.Fatalf("reschedule after undo = %d", response.Code)
	}
	current, _ = store.SnapshotAs(t.Context(), principal)
	if got := current.Organizations[principal.OrganizationID].Billing.ChangeVersion; got != 2 {
		t.Fatalf("rescheduled change version = %d, want 2", got)
	}
	if response := post("/billing/change/undo", nil); response.Code != http.StatusSeeOther {
		t.Fatalf("second undo = %d", response.Code)
	}
	if response := post("/billing/change", url.Values{"plan": {"free"}}); response.Code != http.StatusSeeOther {
		t.Fatalf("Free = %d: %s", response.Code, response.Body.String())
	}
	current, _ = store.SnapshotAs(t.Context(), principal)
	org = current.Organizations[principal.OrganizationID]
	if org.PlanID != "plan_pro" || org.Billing.ScheduledPlanKey != "free" || org.Billing.SubscriptionID != originalID {
		t.Fatalf("Free canceled early: %#v", org.Billing)
	}
	if response := post("/billing/change", url.Values{"plan": {"unknown"}, "interval": {"monthly"}}); response.Code != http.StatusBadRequest {
		t.Fatalf("invalid plan = %d", response.Code)
	}
	if err := store.UpdateAs(t.Context(), principal, func(a *core.Account) error {
		org := a.Organizations[principal.OrganizationID]
		org.OwnerUserID = "another-owner"
		a.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	if response := post("/billing/change/undo", nil); response.Code != http.StatusForbidden {
		t.Fatalf("non-owner undo = %d, want 403", response.Code)
	}
}
