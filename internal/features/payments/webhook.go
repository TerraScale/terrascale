package payments

import (
	"encoding/json/v2"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"slices"

	"terrascale.tech/dashboard/internal/core"

	stripe "github.com/stripe/stripe-go/v86"
	"github.com/stripe/stripe-go/v86/webhook"
)

// WebhookPath is the Stripe webhook endpoint. CSRF protection skips it
// because every request is authenticated by its Stripe signature instead.
const WebhookPath = "/api/v1/billing/stripe/webhook"

// LegacyWebhookPath keeps configured Stripe endpoints working during migration.
const LegacyWebhookPath = "/api/auth/stripe/webhook"

const maxStripeWebhookBody = 1 << 20

// WebhookEvents lists every event type that triggers a sync. Each one carries
// the customer on its data object; the payload itself is never projected. The
// Stripe webhook endpoint must subscribe to all of them.
var WebhookEvents = []stripe.EventType{
	stripe.EventTypeCheckoutSessionCompleted,
	stripe.EventTypeCheckoutSessionAsyncPaymentSucceeded,
	stripe.EventTypeCheckoutSessionAsyncPaymentFailed,
	stripe.EventTypeCheckoutSessionExpired,
	stripe.EventTypeCustomerSubscriptionCreated,
	stripe.EventTypeCustomerSubscriptionUpdated,
	stripe.EventTypeCustomerSubscriptionDeleted,
	stripe.EventTypeCustomerSubscriptionPaused,
	stripe.EventTypeCustomerSubscriptionResumed,
	stripe.EventTypeCustomerSubscriptionPendingUpdateApplied,
	stripe.EventTypeCustomerSubscriptionPendingUpdateExpired,
	stripe.EventTypeCustomerSubscriptionTrialWillEnd,
	stripe.EventTypeInvoiceFinalized,
	stripe.EventTypeInvoicePaid,
	stripe.EventTypeInvoicePaymentSucceeded,
	stripe.EventTypeInvoicePaymentFailed,
	stripe.EventTypeInvoicePaymentActionRequired,
	stripe.EventTypeInvoiceMarkedUncollectible,
	stripe.EventTypeInvoiceVoided,
}

// VerifyStripeWebhook verifies the raw request bytes with stripe-go's
// signature parser and five-minute timestamp tolerance. It also rejects events
// rendered with an API version other than the one stripe-go decodes.
func VerifyStripeWebhook(payload []byte, signature, secret string) (stripe.Event, error) {
	return webhook.ConstructEvent(payload, signature, secret)
}

// handleWebhook verifies a Stripe event and syncs its customer. A failed sync
// returns an error status so Stripe redelivers; syncing is idempotent, so
// duplicates and redeliveries are harmless.
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
			slog.WarnContext(r.Context(), "reject unsigned Stripe webhook", "error", err)
			http.Error(w, "Invalid Stripe webhook signature.", http.StatusBadRequest)
			return
		case err != nil:
			// A version mismatch means the endpoint must be recreated with
			// stripe.APIVersion; returning an error keeps Stripe retrying.
			slog.ErrorContext(r.Context(), "reject unusable Stripe webhook", "error", err, "expected_api_version", stripe.APIVersion)
			http.Error(w, "Unsupported Stripe webhook event.", http.StatusBadRequest)
			return
		}
		logger := slog.With("event_id", event.ID, "event_type", event.Type)
		if event.Livemode != (billing.config.Mode == ModeLive) {
			logger.Error("reject Stripe webhook from the wrong mode", "livemode", event.Livemode, "mode", billing.config.Mode)
			http.Error(w, "Stripe webhook mode mismatch.", http.StatusBadRequest)
			return
		}
		if !slices.Contains(WebhookEvents, event.Type) {
			w.WriteHeader(http.StatusOK)
			return
		}
		customerID, err := eventCustomer(event)
		if err != nil {
			logger.Error("read customer from Stripe event", "error", err)
			http.Error(w, "Invalid Stripe billing event.", http.StatusBadRequest)
			return
		}
		if customerID == "" {
			// Checkouts the dashboard creates always carry a customer; others
			// in the account are not ours and must not be retried.
			logger.Info("skip Stripe event without a customer")
			w.WriteHeader(http.StatusOK)
			return
		}
		logger = logger.With("customer_id", customerID)
		switch _, err := billing.SyncCustomer(r.Context(), flow, customerID); {
		case err == nil:
			logger.Info("synced Stripe customer")
		case errors.Is(err, ErrUnknownCustomer):
			logger.Info("skip Stripe event for an unbound customer")
		case errors.Is(err, core.ErrNotFound):
			// Redelivery cannot bring back a deleted organization.
			logger.Info("skip Stripe event for a missing organization", "error", err)
		default:
			logger.Error("sync Stripe customer", "error", err)
			http.Error(w, "Could not sync Stripe customer.", http.StatusInternalServerError)
			return
		}
		w.WriteHeader(http.StatusOK)
	}
}

// eventCustomer returns the customer ID a tracked event carries on its data
// object, whether Stripe sends it as an ID or an expanded object, or "" when
// the object has none.
func eventCustomer(event stripe.Event) (string, error) {
	if event.Data == nil || len(event.Data.Raw) == 0 {
		return "", errors.New("event has no data object")
	}
	var object struct {
		Customer *stripe.Customer `json:"customer"`
	}
	if err := json.Unmarshal(event.Data.Raw, &object); err != nil {
		return "", err
	}
	if object.Customer == nil {
		return "", nil
	}
	return object.Customer.ID, nil
}
