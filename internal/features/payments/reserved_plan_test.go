package payments_test

import (
	"errors"
	"reflect"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
)

func TestBillingServiceRejectsReservedInternalPlanSelection(t *testing.T) {
	for _, demo := range []bool{false, true} {
		mode := "Stripe"
		if demo {
			mode = "demo"
		}
		t.Run(mode, func(t *testing.T) {
			for _, tc := range []struct{ name, id, key string }{
				{"reserved key", core.NewULID(), "internal"},
			} {
				t.Run(tc.name, func(t *testing.T) {
					store := memstore.New()
					admin, err := store.CreateAccount(t.Context(), core.User{Name: "Admin", Email: "reserved-admin@example.test", PasswordHash: "unused", PlatformAdmin: true})
					if err != nil {
						t.Fatal(err)
					}
					customer, err := store.CreateAccount(t.Context(), core.User{Name: "Customer", Email: "reserved-customer@example.test", PasswordHash: "unused"})
					if err != nil {
						t.Fatal(err)
					}

					plan := core.Plan{ID: tc.id, Key: tc.key, Name: "Internal", PriceCents: 1000, AnnualPriceCents: 10000, Visible: true}
					if err := store.UpsertPlanAs(t.Context(), admin.Principal(), plan); err != nil {
						t.Fatal(err)
					}
					provider := &checkoutProvider{}
					service := payments.NewService(core.Deps{Store: store, Demo: demo}, subscriptions.NewBillingService(store),
						payments.NewStripeForTest(testConfig, provider, payments.NewMemoryStateStore()))
					before := store.Export()
					principal := customer.Principal()
					for _, action := range []struct {
						name string
						run  func() error
					}{
						{"checkout", func() error {
							_, err := service.StartCheckout(t.Context(), principal, plan.Key, payments.IntervalMonthly, "/billing")
							return err
						}},
						{"plan change", func() error {
							_, err := service.ChangePlan(t.Context(), principal, plan.Key, payments.IntervalMonthly)
							return err
						}},
						{"plan change preview", func() error {
							_, err := service.PreviewPlanChange(t.Context(), principal, plan.Key, payments.IntervalMonthly)
							return err
						}},
					} {
						t.Run(action.name, func(t *testing.T) {
							if err := action.run(); !errors.Is(err, core.ErrInvalid) {
								t.Fatalf("selection = %v, want ErrInvalid", err)
							}
							if provider.sessions != 0 || !reflect.DeepEqual(store.Export(), before) {
								t.Fatal("rejected selection created a Stripe checkout or changed stored state")
							}
						})
					}
				})
			}
		})
	}
}
