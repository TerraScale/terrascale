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
	*testBillingFlow
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
	*testProvider
	releaseCalls int
}

func (*staleUndoProvider) PreviewPlanChange(context.Context, SubscriptionSnapshot, string) (PlanChangeQuote, error) {
	return PlanChangeQuote{}, nil
}
func (*staleUndoProvider) ChangePlanNow(context.Context, SubscriptionSnapshot, string, string) error {
	return nil
}
func (*staleUndoProvider) SchedulePlanChange(context.Context, SubscriptionSnapshot, string, string, string) (string, error) {
	return "", nil
}
func (*staleUndoProvider) SetCancelAtPeriodEnd(context.Context, string, bool, string) error {
	return nil
}
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
	provider := &staleUndoProvider{testProvider: &testProvider{customerID: "cus_retry", subscription: SubscriptionSnapshot{
		ID: "sub_retry", CustomerID: "cus_retry", Status: "active", PriceID: "price_pro_monthly",
	}}}
	mux := http.NewServeMux()
	mount(mux, core.Deps{Store: store, Sessions: testSessions{principal: principal}}, &staleUndoFlow{testBillingFlow: &testBillingFlow{}, store: store},
		StripeConfig{Production: true, Mode: "test", PriceIDs: map[PriceKey]string{{PlanKey: "pro", Interval: "monthly"}: "price_pro_monthly"}}, provider, nil)
	response := httptest.NewRecorder()
	mux.ServeHTTP(response, httptest.NewRequest(http.MethodPost, "/billing/change/undo", nil))
	current, _ := store.Snapshot(principal.AccountID)
	if response.Code != http.StatusSeeOther || provider.releaseCalls != 0 || current.Organizations[principal.OrganizationID].Billing.ScheduledPlanKey != "" {
		t.Fatalf("retry undo = %d, release calls = %d, billing = %#v", response.Code, provider.releaseCalls, current.Organizations[principal.OrganizationID].Billing)
	}
}
