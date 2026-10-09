package subscriptions

import (
	"errors"
	"reflect"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/payments"
)

func TestCustomerCannotSelectReservedInternalPlan(t *testing.T) {
	for _, tc := range []struct{ name, id, key string }{
		{"reserved key", core.NewULID(), "internal"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			store := memstore.New()
			administrator, _, customer, _ := internalBillingFixture(t, store)
			plan := core.Plan{ID: tc.id, Key: tc.key, Name: "Internal", PriceCents: 1000, AnnualPriceCents: 10000, Visible: true}
			if err := store.UpsertPlanAs(t.Context(), administrator, plan); err != nil {
				t.Fatal(err)
			}
			principal := customer.Principal()
			service := NewBillingService(store)
			if _, err := service.RecordCustomer(t.Context(), principal, "cus_other"); err != nil {
				t.Fatal(err)
			}
			before := store.Export()
			for _, interval := range []string{intervalMonthly, intervalAnnual} {
				t.Run(interval, func(t *testing.T) {
					for _, action := range []struct {
						name string
						run  func() error
					}{
						{"demo checkout", func() error {
							_, err := service.BeginDemoCheckout(t.Context(), principal, plan.Key, interval, "/billing")
							return err
						}},
						{"external checkout", func() error {
							return service.RecordExternalCheckout(t.Context(), principal, plan.Key, interval, "/billing", payments.CheckoutSession{
								ID: "cs_other", CustomerID: "cus_other", ClientReferenceID: principal.OrganizationID, ExpiresAt: time.Now().Add(time.Hour),
							})
						}},
						{"immediate change", func() error {
							return service.ChangeDemoPlan(t.Context(), principal, plan.Key, interval)
						}},
						{"scheduled change", func() error {
							return service.ScheduleDemoChange(t.Context(), principal, plan.Key, interval, time.Now().Add(time.Hour))
						}},
						{"Stripe projection", func() error {
							return service.ProjectCustomerState(t.Context(), payments.CustomerState{
								OrganizationID: principal.OrganizationID, CustomerID: "cus_other", SubscriptionID: "sub_other",
								Status: "active", PlanKey: plan.Key, Interval: interval, SyncedAt: time.Now(), Revision: 1,
							}, nil)
						}},
					} {
						t.Run(action.name, func(t *testing.T) {
							if err := action.run(); !errors.Is(err, ErrPlanUnavailable) {
								t.Fatalf("selection = %v, want ErrPlanUnavailable", err)
							}
							if !reflect.DeepEqual(store.Export(), before) {
								t.Fatal("rejected reserved plan selection changed stored state")
							}
						})
					}
				})
			}
			view, err := service.Snapshot(t.Context(), principal)
			if err != nil {
				t.Fatal(err)
			}
			for _, offered := range view.Plans {
				if offered.ID == plan.ID {
					t.Fatal("reserved plan is offered to another customer")
				}
			}
		})
	}
}

func TestCustomerCannotCompleteReservedInternalPlanCheckout(t *testing.T) {
	store := memstore.New()
	administrator, _, customer, config := internalBillingFixture(t, store)
	if err := ConfigureInternalBilling(t.Context(), store, administrator, config); err != nil {
		t.Fatal(err)
	}
	plan, err := store.Plan(t.Context(), "internal")
	if err != nil {
		t.Fatal(err)
	}
	// A forged catalog entry and checkout must not bypass the reservation.
	plan.Visible = true
	if err := store.UpsertPlanAs(t.Context(), administrator, plan); err != nil {
		t.Fatal(err)
	}
	principal := customer.Principal()
	session := core.CheckoutSession{
		ID: core.NewULID(), OrganizationID: principal.OrganizationID, OwnerUserID: principal.UserID, PlanID: plan.ID,
		Interval: intervalMonthly, ReturnPath: "/billing", Status: checkoutOpen, Demo: true, ExpiresAt: time.Now().Add(time.Hour),
	}
	if err := store.UpdateAs(t.Context(), principal, func(account *core.Account) error {
		org := account.Organizations[principal.OrganizationID]
		org.Billing.CheckoutID = session.ID
		org.CheckoutSessions[session.ID] = session
		account.Organizations[org.ID] = org
		return nil
	}); err != nil {
		t.Fatal(err)
	}
	before := store.Export()
	service := NewBillingService(store)
	if _, err := service.CompleteDemoCheckout(t.Context(), principal, session.ID, false); !errors.Is(err, ErrPlanUnavailable) {
		t.Fatalf("complete checkout = %v, want ErrPlanUnavailable", err)
	}
	if !reflect.DeepEqual(store.Export(), before) {
		t.Fatal("rejected checkout completion changed stored state")
	}
	if _, err := service.CompleteDemoCheckout(t.Context(), principal, session.ID, true); err != nil {
		t.Fatalf("cancel reserved plan checkout = %v", err)
	}
}
