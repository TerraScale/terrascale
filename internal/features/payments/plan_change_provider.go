package payments

import (
	"context"
	"fmt"
	"os"
	"time"

	stripe "github.com/stripe/stripe-go/v86"
)

// PreviewOrganizationChange reads the same Stripe subscription used by the
// mutation route. The caller must authorize the organization owner first.
func PreviewOrganizationChange(ctx context.Context, orgCustomerID, subscriptionID, planKey, interval string) (PlanChangeQuote, error) {
	config, err := LoadStripeConfig(os.Getenv)
	if err != nil || config.Demo || config.Mode != "test" {
		return PlanChangeQuote{}, ErrStripeUnavailable
	}
	provider, err := NewStripeProvider(config, false)
	if err != nil {
		return PlanChangeQuote{}, err
	}
	priceID, ok := config.PriceID(planKey, interval)
	if !ok {
		return PlanChangeQuote{}, ErrUnconfiguredPrice
	}
	current, err := provider.RetrieveSubscription(ctx, subscriptionID)
	if err != nil {
		return PlanChangeQuote{}, err
	}
	if current.ID != subscriptionID || current.CustomerID != orgCustomerID || !isEntitledStatus(current.Status) {
		return PlanChangeQuote{}, ErrInvalidBillingInput
	}
	return provider.(PlanChangeProvider).PreviewPlanChange(ctx, current, priceID)
}

type PlanChangeQuote struct {
	AmountDueCents int64
	ProrationCents int64
	Currency       string
	EffectiveAt    time.Time
}

// PlanChangeProvider is separate from checkout so existing billing adapters
// can still implement only the operations they use.
type PlanChangeProvider interface {
	PreviewPlanChange(context.Context, SubscriptionSnapshot, string) (PlanChangeQuote, error)
	ChangePlanNow(context.Context, SubscriptionSnapshot, string, string) error
	SchedulePlanChange(context.Context, SubscriptionSnapshot, string, string, string) (string, error)
	SetCancelAtPeriodEnd(context.Context, string, bool, string) error
	ReleaseSchedule(context.Context, string, string) error
}

func (provider *stripeProvider) PreviewPlanChange(ctx context.Context, current SubscriptionSnapshot, nextPriceID string) (PlanChangeQuote, error) {
	if ctx == nil || current.ID == "" || current.ItemID == "" || nextPriceID == "" {
		return PlanChangeQuote{}, ErrInvalidBillingInput
	}
	preview, err := provider.client.V1Invoices.CreatePreview(ctx, &stripe.InvoiceCreatePreviewParams{
		Subscription: new(current.ID),
		SubscriptionDetails: &stripe.InvoiceCreatePreviewSubscriptionDetailsParams{
			Items:             []*stripe.InvoiceCreatePreviewSubscriptionDetailsItemParams{{ID: new(current.ItemID), Price: new(nextPriceID), Quantity: new(int64(1))}},
			ProrationBehavior: new("always_invoice"),
		},
	})
	if err != nil {
		return PlanChangeQuote{}, err
	}
	quote := PlanChangeQuote{AmountDueCents: preview.AmountDue, Currency: string(preview.Currency), EffectiveAt: time.Now().UTC()}
	if preview.Lines != nil {
		for _, line := range preview.Lines.Data {
			if line != nil && line.Parent != nil && ((line.Parent.SubscriptionItemDetails != nil && line.Parent.SubscriptionItemDetails.Proration) || (line.Parent.InvoiceItemDetails != nil && line.Parent.InvoiceItemDetails.Proration)) {
				quote.ProrationCents += line.Amount
			}
		}
	}
	return quote, nil
}

func (provider *stripeProvider) ChangePlanNow(ctx context.Context, current SubscriptionSnapshot, nextPriceID, idempotencyKey string) error {
	if ctx == nil || current.ID == "" || current.ItemID == "" || nextPriceID == "" || idempotencyKey == "" {
		return ErrInvalidBillingInput
	}
	params := &stripe.SubscriptionUpdateParams{
		Items:             []*stripe.SubscriptionUpdateItemParams{{ID: new(current.ItemID), Price: new(nextPriceID), Quantity: new(int64(1))}},
		ProrationBehavior: new("always_invoice"), PaymentBehavior: new("pending_if_incomplete"),
	}
	params.SetIdempotencyKey(idempotencyKey)
	_, err := provider.client.V1Subscriptions.Update(ctx, current.ID, params)
	return err
}

func (provider *stripeProvider) SchedulePlanChange(ctx context.Context, current SubscriptionSnapshot, nextPriceID, planKey, idempotencyKey string) (string, error) {
	if ctx == nil || current.ID == "" || current.PriceID == "" || nextPriceID == "" || current.CurrentPeriodEnd.IsZero() || idempotencyKey == "" {
		return "", ErrInvalidBillingInput
	}
	scheduleID := current.ScheduleID
	phaseStart := time.Now().UTC().Unix()
	if scheduleID == "" {
		params := &stripe.SubscriptionScheduleCreateParams{FromSubscription: new(current.ID), Metadata: map[string]string{"dashboard_change_key": idempotencyKey}}
		params.SetIdempotencyKey(idempotencyKey + ":create")
		created, err := provider.client.V1SubscriptionSchedules.Create(ctx, params)
		if err != nil {
			return "", err
		}
		scheduleID = created.ID
		if created.CurrentPhase != nil {
			phaseStart = created.CurrentPhase.StartDate
		}
	} else {
		existing, err := provider.client.V1SubscriptionSchedules.Retrieve(ctx, scheduleID, nil)
		if err != nil {
			return "", err
		}
		if existing.Subscription == nil || existing.Subscription.ID != current.ID || existing.CurrentPhase == nil || existing.Metadata["dashboard_change_key"] != idempotencyKey {
			return "", fmt.Errorf("subscription schedule does not belong to the current subscription")
		}
		phaseStart = existing.CurrentPhase.StartDate
	}
	if phaseStart >= current.CurrentPeriodEnd.Unix() {
		return "", ErrInvalidBillingInput
	}
	interval := "month"
	if _, targetInterval, ok := provider.config.planFromPriceID(nextPriceID); ok && targetInterval == IntervalAnnual {
		interval = "year"
	}
	params := &stripe.SubscriptionScheduleUpdateParams{
		EndBehavior:       new("release"),
		ProrationBehavior: new("none"),
		Phases: []*stripe.SubscriptionScheduleUpdatePhaseParams{
			{StartDate: new(phaseStart), EndDate: new(current.CurrentPeriodEnd.Unix()), Items: []*stripe.SubscriptionScheduleUpdatePhaseItemParams{{Price: new(current.PriceID), Quantity: new(int64(1))}}},
			{StartDate: new(current.CurrentPeriodEnd.Unix()), Duration: &stripe.SubscriptionScheduleUpdatePhaseDurationParams{Interval: new(interval), IntervalCount: new(int64(1))}, Items: []*stripe.SubscriptionScheduleUpdatePhaseItemParams{{Price: new(nextPriceID), Quantity: new(int64(1))}}, Metadata: map[string]string{"plan_key": planKey}},
		},
	}
	params.SetIdempotencyKey(idempotencyKey + ":update")
	if _, err := provider.client.V1SubscriptionSchedules.Update(ctx, scheduleID, params); err != nil {
		return scheduleID, err
	}
	return scheduleID, nil
}

func (provider *stripeProvider) SetCancelAtPeriodEnd(ctx context.Context, subscriptionID string, cancel bool, idempotencyKey string) error {
	if ctx == nil || subscriptionID == "" || idempotencyKey == "" {
		return ErrInvalidBillingInput
	}
	params := &stripe.SubscriptionUpdateParams{CancelAtPeriodEnd: new(cancel)}
	params.SetIdempotencyKey(idempotencyKey)
	_, err := provider.client.V1Subscriptions.Update(ctx, subscriptionID, params)
	return err
}

func (provider *stripeProvider) ReleaseSchedule(ctx context.Context, scheduleID, idempotencyKey string) error {
	if ctx == nil || scheduleID == "" || idempotencyKey == "" {
		return ErrInvalidBillingInput
	}
	params := &stripe.SubscriptionScheduleReleaseParams{}
	params.SetIdempotencyKey(idempotencyKey)
	_, err := provider.client.V1SubscriptionSchedules.Release(ctx, scheduleID, params)
	return err
}
