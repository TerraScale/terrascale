package payments

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

type staleUndoFlow struct {
	BillingFlow
	store *core.Store
}

func (*staleUndoFlow) RecordScheduledChange(core.Principal, string, string, string, string, time.Time) error {
	return nil
}
func (*staleUndoFlow) RecordPendingChange(core.Principal, string, string, string) error { return nil }
func (*staleUndoFlow) ChangeDemoPlan(core.Principal, string, string) error              { return nil }
func (flow *staleUndoFlow) ClearScheduledChange(principal core.Principal, _ string) error {
	return flow.store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[principal.OrganizationID]
		org.Billing.ScheduledPlanKey = ""
		org.Billing.ScheduledInterval = ""
		org.Billing.ScheduleID = ""
		account.Organizations[org.ID] = org
		return nil
	})
}

type staleUndoProvider struct {
	BillingProvider
	subscription SubscriptionSnapshot
	releaseCalls int
}

func (provider *staleUndoProvider) RetrieveSubscription(context.Context, string) (SubscriptionSnapshot, error) {
	return provider.subscription, nil
}

type staleUndoSessions struct{ principal core.Principal }

func (sessions staleUndoSessions) Start(http.ResponseWriter, *http.Request, core.Principal) error {
	return nil
}
func (sessions staleUndoSessions) Principal(*http.Request) (core.Principal, bool) {
	return sessions.principal, true
}
func (sessions staleUndoSessions) BearerPrincipal(*http.Request) (core.Principal, bool) {
	return core.Principal{}, false
}
func (sessions staleUndoSessions) End(http.ResponseWriter, *http.Request) {}
func (sessions staleUndoSessions) RevokeUser(string, string)              {}

func (provider *staleUndoProvider) ReleaseSchedule(context.Context, string, string) error {
	provider.releaseCalls++
	return nil
}

func TestUndoRecoversWhenStripeReleasedScheduleBeforeLocalWrite(t *testing.T) {
	store := core.NewStore()
	account, err := store.CreateAccount(core.User{Name: "Owner", Email: "undo-retry@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	principal := account.Principal()
	if err := store.UpdateAs(principal, func(account *core.Account) error {
		org := account.Organizations[principal.OrganizationID]
		org.PlanID = "plan_pro"
		org.Billing = core.Billing{
			CustomerID: "cus_retry", SubscriptionID: "sub_retry", Status: "active",
			PlanKey: "pro", Interval: "monthly", ScheduledPlanKey: "starter",
			ScheduledInterval: "monthly", ScheduleID: "sub_sched_released", ChangeVersion: 1,
		}
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	provider := &staleUndoProvider{subscription: SubscriptionSnapshot{
		ID: "sub_retry", CustomerID: "cus_retry", Status: "active", PriceID: "price_pro_monthly",
	}}
	mux := http.NewServeMux()
	billing := newStripe(StripeConfig{Mode: ModeTest, PriceIDs: map[PriceKey]string{{PlanKey: "pro", Interval: "monthly"}: "price_pro_monthly"}}, provider)
	Mount(mux, core.Deps{Store: store, Sessions: staleUndoSessions{principal: principal}}, &staleUndoFlow{store: store}, billing)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodPost, "/billing/change/undo", nil))
	current, _ := store.SnapshotAs(principal)
	if response.Code != http.StatusSeeOther || provider.releaseCalls != 0 || current.Organizations[principal.OrganizationID].Billing.ScheduledPlanKey != "" {
		t.Fatalf("retry undo = %d, release calls = %d, billing = %#v", response.Code, provider.releaseCalls, current.Organizations[principal.OrganizationID].Billing)
	}
}
