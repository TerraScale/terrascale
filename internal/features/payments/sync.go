package payments

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// syncAttempts bounds how often SyncCustomer re-reads Stripe after a
// concurrent sync saved or projected first.
const syncAttempts = 3

// SyncCustomer is the single path that updates billing state. It reads the
// customer's current subscriptions, payment method, invoices, and pending
// checkout from Stripe, saves the result in the state store, and projects it
// onto the organization. Webhooks, the checkout return, and plan changes all
// call it, so event order and payload shapes never matter.
//
// The save is conditional on the version loaded before Stripe was read. When
// another sync wins, the state is re-read, so the stored state always comes
// from a Stripe read that started after the previous save.
func (billing *Stripe) SyncCustomer(ctx context.Context, flow BillingFlow, customerID string) (CustomerState, error) {
	organizationID, bound, err := billing.store.OrganizationFor(ctx, customerID)
	if err != nil {
		return CustomerState{}, err
	}
	if !bound {
		return CustomerState{}, ErrUnknownCustomer
	}
	for range syncAttempts {
		previous, version, err := billing.store.Load(ctx, customerID)
		if err != nil {
			return CustomerState{}, err
		}
		if version == "" || previous.CustomerID != customerID || previous.OrganizationID != organizationID {
			return CustomerState{}, fmt.Errorf("%w: bound Stripe customer state is missing or mismatched", core.ErrUnavailable)
		}
		if previous.Revision == math.MaxUint64 {
			return CustomerState{}, fmt.Errorf("%w: Stripe customer revision exhausted", core.ErrUnavailable)
		}
		customer, err := billing.provider.FetchCustomer(ctx, customerID)
		if err != nil {
			return CustomerState{}, fmt.Errorf("read Stripe customer: %w", err)
		}
		if customer.ID != customerID || (customer.OrganizationID != "" && customer.OrganizationID != organizationID) {
			return CustomerState{}, errors.New("Stripe customer is bound to another organization")
		}
		state := billing.customerState(organizationID, customer)
		state.Revision = previous.Revision + 1
		switch err := billing.store.Save(ctx, state, version); {
		case errors.Is(err, ErrStaleState):
			continue
		case err != nil:
			return CustomerState{}, fmt.Errorf("save Stripe state: %w", err)
		}
		checkout, err := billing.pendingCheckout(ctx, flow, organizationID, customerID)
		if err != nil {
			return CustomerState{}, err
		}
		switch err := flow.ProjectCustomerState(ctx, state, checkout); {
		case errors.Is(err, ErrStaleState):
			continue
		case err != nil:
			return CustomerState{}, fmt.Errorf("project Stripe state: %w", err)
		}
		return state, nil
	}
	return CustomerState{}, ErrStaleState
}

// customerState reduces what Stripe reports to the stored state. The current
// subscription is the newest one that is not terminal, falling back to the
// newest overall.
func (billing *Stripe) customerState(organizationID string, customer StripeCustomer) CustomerState {
	state := CustomerState{
		CustomerID: customer.ID, OrganizationID: organizationID, Status: StatusNone,
		Invoices: customer.Invoices, SyncedAt: time.Now().UTC(),
	}
	var current *SubscriptionSnapshot
	live := 0
	for index := range customer.Subscriptions {
		subscription := &customer.Subscriptions[index]
		state.TrialUsed = state.TrialUsed || subscription.TrialStarted
		terminal := isTerminal(subscription.Status)
		if !terminal {
			live++
		}
		if current == nil || (!terminal && isTerminal(current.Status)) ||
			(terminal == isTerminal(current.Status) && subscription.CreatedAt.After(current.CreatedAt)) {
			current = subscription
		}
	}
	if live > 1 {
		slog.Error("Stripe customer has more than one current subscription; enable \"Limit customers to one subscription\" in Checkout settings",
			"customer_id", customer.ID, "organization_id", organizationID, "subscriptions", live)
	}
	state.PaymentMethod = customer.DefaultPaymentMethod
	if current == nil {
		return state
	}
	state.SubscriptionID, state.Status, state.PriceID = current.ID, current.Status, current.PriceID
	state.PlanKey, state.Interval, _ = billing.config.planFromPriceID(current.PriceID)
	state.CurrentPeriodStart, state.CurrentPeriodEnd = current.CurrentPeriodStart, current.CurrentPeriodEnd
	state.CancelAtPeriodEnd = current.CancelAtPeriodEnd
	state.PendingPlanKey, state.PendingInterval, _ = billing.config.planFromPriceID(current.PendingPriceID)
	state.ScheduleID = current.ScheduleID
	state.ScheduledPlanKey, state.ScheduledInterval, _ = billing.config.planFromPriceID(current.ScheduledPriceID)
	if current.PaymentMethod != nil {
		state.PaymentMethod = current.PaymentMethod
	}
	return state
}

// pendingCheckout reads the organization's open Checkout Session so the
// projection can complete or release it. Sessions not bound to this customer
// and tenant are ignored.
func (billing *Stripe) pendingCheckout(ctx context.Context, flow BillingFlow, organizationID, customerID string) (*CheckoutSession, error) {
	checkoutID, err := flow.PendingCheckoutID(ctx, organizationID)
	if err != nil || checkoutID == "" {
		return nil, err
	}
	session, err := billing.provider.RetrieveCheckoutSession(ctx, checkoutID)
	if err != nil {
		return nil, fmt.Errorf("read pending Stripe checkout: %w", err)
	}
	if session.ID != checkoutID || session.CustomerID != customerID || session.ClientReferenceID != organizationID {
		return nil, nil
	}
	return &session, nil
}

func isTerminal(status string) bool {
	return status == "canceled" || status == "incomplete_expired"
}
