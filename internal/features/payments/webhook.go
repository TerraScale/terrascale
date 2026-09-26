package payments

import (
	"cmp"
	"context"
	"encoding/json/v2"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"time"

	"terrascale.tech/dashboard/internal/core"

	stripe "github.com/stripe/stripe-go/v86"
	"github.com/stripe/stripe-go/v86/webhook"
)

// WebhookPath is the Stripe webhook endpoint. CSRF protection skips it
// because every request is authenticated by its Stripe signature instead.
const WebhookPath = "/api/auth/stripe/webhook"

const maxStripeWebhookBody = 1 << 20

// WebhookEvents lists every event type the billing projection consumes. The
// Stripe webhook endpoint must subscribe to all of them.
var WebhookEvents = []stripe.EventType{
	stripe.EventTypeCheckoutSessionCompleted,
	stripe.EventTypeCheckoutSessionAsyncPaymentSucceeded,
	stripe.EventTypeCheckoutSessionExpired,
	stripe.EventTypeCustomerSubscriptionCreated,
	stripe.EventTypeCustomerSubscriptionUpdated,
	stripe.EventTypeCustomerSubscriptionDeleted,
	stripe.EventTypeInvoiceFinalized,
	stripe.EventTypeInvoicePaid,
	stripe.EventTypeInvoicePaymentFailed,
	stripe.EventTypeInvoiceVoided,
}

// BillingEvent contains only provider fields used by the billing state
// projection. The signed Stripe object is converted to this shape before it
// crosses the payment boundary.
type BillingEvent struct {
	ID                string
	Type              string
	CreatedAt         time.Time
	CheckoutID        string
	CustomerID        string
	SubscriptionID    string
	ClientReferenceID string
	OrganizationID    string
	PlanKey           string
	Interval          string
	CheckoutStatus    string
	PaymentStatus     string
	SubscriptionState string
	PeriodEnd         time.Time
	ProviderCurrent   bool
	PendingUpdate     bool
	ObservedAt        time.Time
	Invoice           *core.Invoice
}

// VerifyStripeWebhook verifies the raw request bytes with stripe-go's
// signature parser and five-minute timestamp tolerance. It also rejects events
// rendered with an API version other than the one stripe-go decodes, because
// fields such as invoice.parent and per-item billing periods would otherwise be
// silently dropped.
func VerifyStripeWebhook(payload []byte, signature, secret string) (stripe.Event, error) {
	return webhook.ConstructEvent(payload, signature, secret)
}

func handleWebhook(flow BillingFlow, billing *Stripe) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if billing == nil {
			http.Error(w, "Stripe webhook is not configured.", http.StatusServiceUnavailable)
			return
		}
		r.Body = http.MaxBytesReader(w, r.Body, maxStripeWebhookBody)
		payload, err := io.ReadAll(r.Body)
		if err != nil {
			if _, ok := errors.AsType[*http.MaxBytesError](err); ok {
				http.Error(w, "Stripe webhook body is too large.", http.StatusRequestEntityTooLarge)
				return
			}
			http.Error(w, "Could not read Stripe webhook.", http.StatusBadRequest)
			return
		}
		event, err := VerifyStripeWebhook(payload, r.Header.Get("Stripe-Signature"), billing.config.WebhookSecret)
		switch {
		case errors.Is(err, webhook.ErrNotSigned), errors.Is(err, webhook.ErrInvalidHeader),
			errors.Is(err, webhook.ErrNoValidSignature), errors.Is(err, webhook.ErrTooOld):
			slog.Warn("reject unsigned Stripe webhook", "error", err)
			http.Error(w, "Invalid Stripe webhook signature.", http.StatusBadRequest)
			return
		case err != nil:
			// A version mismatch means the endpoint must be recreated with
			// stripe.APIVersion; returning an error keeps Stripe retrying.
			slog.Error("reject unusable Stripe webhook", "error", err, "expected_api_version", stripe.APIVersion)
			http.Error(w, "Unsupported Stripe webhook event.", http.StatusBadRequest)
			return
		}
		logger := slog.With("event_id", event.ID, "event_type", event.Type)
		if event.Livemode != (billing.config.Mode == ModeLive) {
			logger.Error("reject Stripe webhook from the wrong mode", "livemode", event.Livemode, "mode", billing.config.Mode)
			http.Error(w, "Stripe webhook mode mismatch.", http.StatusBadRequest)
			return
		}
		projection, handled, err := billingEventFromStripe(event)
		if err != nil {
			logger.Error("decode Stripe billing event", "error", err)
			http.Error(w, "Invalid Stripe billing event.", http.StatusBadRequest)
			return
		}
		if !handled {
			w.WriteHeader(http.StatusOK)
			return
		}
		if projection.Invoice == nil && projection.SubscriptionID != "" {
			if err := billing.attachSubscriptionSnapshot(r.Context(), &projection); err != nil {
				logger.Error("verify Stripe subscription for webhook", "subscription_id", projection.SubscriptionID, "error", err)
				http.Error(w, "Could not verify Stripe subscription.", http.StatusBadGateway)
				return
			}
		}
		switch err := flow.ApplyStripeEvent(projection); {
		case err == nil:
			logger.Info("applied Stripe billing event")
		case errors.Is(err, core.ErrAlreadyProcessed):
			logger.Debug("skip duplicate Stripe billing event")
		case errors.Is(err, core.ErrNotFound):
			logger.Info("skip Stripe billing event for an unknown customer", "customer_id", projection.CustomerID)
		default:
			logger.Error("apply Stripe billing event", "error", err)
			http.Error(w, "Could not apply Stripe billing event.", http.StatusInternalServerError)
			return
		}
		w.WriteHeader(http.StatusOK)
	}
}

// attachSubscriptionSnapshot replaces event-carried subscription fields with a
// fresh read, so out-of-order deliveries and portal changes always project the
// subscription's current price, status, and period.
func (billing *Stripe) attachSubscriptionSnapshot(ctx context.Context, projection *BillingEvent) error {
	snapshot, err := billing.provider.RetrieveSubscription(ctx, projection.SubscriptionID)
	if err != nil {
		return err
	}
	if snapshot.ID != projection.SubscriptionID || snapshot.CustomerID == "" || snapshot.Status == "" ||
		(projection.CustomerID != "" && snapshot.CustomerID != projection.CustomerID) ||
		(projection.OrganizationID != "" && snapshot.OrganizationID != "" && snapshot.OrganizationID != projection.OrganizationID) {
		return errors.New("subscription snapshot does not match the signed event")
	}
	projection.CustomerID = snapshot.CustomerID
	projection.OrganizationID = cmp.Or(projection.OrganizationID, snapshot.OrganizationID)
	projection.SubscriptionState = snapshot.Status
	projection.PeriodEnd = snapshot.CurrentPeriodEnd
	projection.PlanKey, projection.Interval, _ = billing.config.planFromPriceID(snapshot.PriceID)
	projection.ProviderCurrent = true
	projection.PendingUpdate = snapshot.PendingUpdate
	projection.ObservedAt = time.Now().UTC()
	return nil
}

func billingEventFromStripe(event stripe.Event) (BillingEvent, bool, error) {
	projection := BillingEvent{ID: event.ID, Type: string(event.Type), CreatedAt: time.Unix(event.Created, 0).UTC()}
	switch event.Type {
	case stripe.EventTypeInvoiceFinalized, stripe.EventTypeInvoicePaid, stripe.EventTypeInvoicePaymentFailed, stripe.EventTypeInvoiceVoided:
		var invoice stripe.Invoice
		if err := decodeEventObject(event, &invoice); err != nil {
			return BillingEvent{}, false, err
		}
		projection.CustomerID = stripeCustomerID(invoice.Customer)
		if invoice.Parent != nil && invoice.Parent.SubscriptionDetails != nil {
			projection.SubscriptionID = stripeSubscriptionID(invoice.Parent.SubscriptionDetails.Subscription)
		}
		if projection.CustomerID == "" || projection.SubscriptionID == "" || invoice.ID == "" {
			// Other Stripe products can send one-off invoices to this endpoint.
			return BillingEvent{}, false, nil
		}
		projection.Invoice = &core.Invoice{
			ID: invoice.ID, Number: invoice.Number, Status: string(invoice.Status), AmountCents: invoice.AmountDue,
			Currency: string(invoice.Currency), CreatedAt: time.Unix(invoice.Created, 0).UTC(),
			PeriodStart: time.Unix(invoice.PeriodStart, 0).UTC(), PeriodEnd: time.Unix(invoice.PeriodEnd, 0).UTC(),
			HostedURL: invoice.HostedInvoiceURL, PDFURL: invoice.InvoicePDF,
		}
		return projection, true, nil
	case stripe.EventTypeCheckoutSessionCompleted, stripe.EventTypeCheckoutSessionAsyncPaymentSucceeded, stripe.EventTypeCheckoutSessionExpired:
		var session stripe.CheckoutSession
		if err := decodeEventObject(event, &session); err != nil {
			return BillingEvent{}, false, err
		}
		if session.Mode != stripe.CheckoutSessionModeSubscription {
			return BillingEvent{}, false, nil
		}
		projection.CheckoutID = session.ID
		projection.CustomerID = stripeCustomerID(session.Customer)
		projection.SubscriptionID = stripeSubscriptionID(session.Subscription)
		projection.ClientReferenceID = session.ClientReferenceID
		projection.OrganizationID = session.Metadata["organization_id"]
		projection.PlanKey = session.Metadata["plan_key"]
		projection.Interval = session.Metadata["interval"]
		projection.CheckoutStatus = string(session.Status)
		projection.PaymentStatus = string(session.PaymentStatus)
		return projection, true, nil
	case stripe.EventTypeCustomerSubscriptionCreated, stripe.EventTypeCustomerSubscriptionUpdated, stripe.EventTypeCustomerSubscriptionDeleted:
		var subscription stripe.Subscription
		if err := decodeEventObject(event, &subscription); err != nil {
			return BillingEvent{}, false, err
		}
		projection.CustomerID = stripeCustomerID(subscription.Customer)
		projection.SubscriptionID = subscription.ID
		projection.OrganizationID = subscription.Metadata["organization_id"]
		projection.PlanKey = subscription.Metadata["plan_key"]
		projection.Interval = subscription.Metadata["interval"]
		projection.SubscriptionState = string(subscription.Status)
		projection.PeriodEnd = currentPeriodEnd(subscription.Items)
		return projection, true, nil
	default:
		return BillingEvent{}, false, nil
	}
}

func decodeEventObject(event stripe.Event, target any) error {
	if event.Data == nil || len(event.Data.Raw) == 0 {
		return errors.New("event has no data object")
	}
	return json.Unmarshal(event.Data.Raw, target)
}
