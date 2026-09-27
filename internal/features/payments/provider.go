package payments

import (
	"context"
	"errors"
	"fmt"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"

	stripe "github.com/stripe/stripe-go/v86"
)

type CustomerRequest struct {
	Email          string
	Name           string
	OrganizationID string
	IdempotencyKey string
}

type Customer struct {
	ID string
}

type CheckoutRequest struct {
	CustomerID       string
	OrganizationID   string
	OrganizationName string
	PlanKey          string
	Interval         string
	TrialDays        int
	SuccessURL       string
	CancelURL        string
	ExpiresAt        time.Time
	IdempotencyKey   string
}

type CheckoutSession struct {
	ID                string
	URL               string
	CustomerID        string
	SubscriptionID    string
	ClientReferenceID string
	Status            string
	PaymentStatus     string
	ExpiresAt         time.Time
}

type PortalSession struct {
	ID  string
	URL string
}

// SubscriptionSnapshot is one subscription as Stripe reports it now.
type SubscriptionSnapshot struct {
	ID                 string
	CustomerID         string
	OrganizationID     string
	Status             string
	PriceID            string
	ItemID             string
	ScheduleID         string
	ScheduledPriceID   string
	PendingPriceID     string
	CancelAtPeriodEnd  bool
	TrialStarted       bool
	CreatedAt          time.Time
	CurrentPeriodStart time.Time
	CurrentPeriodEnd   time.Time
	PaymentMethod      *PaymentMethod
}

// StripeCustomer is everything SyncCustomer reads about one customer.
type StripeCustomer struct {
	ID                   string
	OrganizationID       string
	DefaultPaymentMethod *PaymentMethod
	Subscriptions        []SubscriptionSnapshot
	Invoices             []core.Invoice
}

type PlanChangeQuote struct {
	AmountDueCents int64
	ProrationCents int64
	Currency       string
	EffectiveAt    time.Time
}

// BillingProvider isolates Stripe from the HTTP handlers so tests can replace
// it. The demo path is implemented by the in-memory store and never calls it.
type BillingProvider interface {
	CreateCustomer(context.Context, CustomerRequest) (Customer, error)
	CreateCheckoutSession(context.Context, CheckoutRequest) (CheckoutSession, error)
	ExpireCheckoutSession(context.Context, string) error
	RetrieveCheckoutSession(context.Context, string) (CheckoutSession, error)
	RetrieveSubscription(context.Context, string) (SubscriptionSnapshot, error)
	FetchCustomer(context.Context, string) (StripeCustomer, error)
	CreatePortalSession(context.Context, string, string, string) (PortalSession, error)
	PreviewPlanChange(context.Context, SubscriptionSnapshot, string) (PlanChangeQuote, error)
	ChangePlanNow(context.Context, SubscriptionSnapshot, string, string) error
	SchedulePlanChange(context.Context, SubscriptionSnapshot, string, string) (string, error)
	SetCancelAtPeriodEnd(context.Context, string, bool, string) error
	ReleaseSchedule(context.Context, string, string) error
}

const (
	// stripeRequestTimeout bounds one Stripe API attempt. Retries of
	// connection errors, 409 lock timeouts, 429s, and retryable 5xx responses
	// reuse the request's idempotency key, so a retried mutation applies once.
	stripeRequestTimeout    = 20 * time.Second
	stripeMaxNetworkRetries = 3
)

type stripeProvider struct {
	config StripeConfig
	client *stripe.Client
}

func newStripeProvider(config StripeConfig) *stripeProvider {
	backends := stripe.NewBackendsWithConfig(&stripe.BackendConfig{
		HTTPClient:        newStripeHTTPClient(),
		MaxNetworkRetries: stripe.Int64(stripeMaxNetworkRetries),
		LeveledLogger:     slogStripeLogger{},
	})
	return &stripeProvider{config: config, client: stripe.NewClient(config.SecretKey, stripe.WithBackends(backends))}
}

func (provider *stripeProvider) CreateCustomer(ctx context.Context, request CustomerRequest) (Customer, error) {
	if strings.TrimSpace(request.Email) == "" || request.OrganizationID == "" || request.IdempotencyKey == "" {
		return Customer{}, ErrInvalidBillingInput
	}
	params := &stripe.CustomerCreateParams{
		Email:    new(strings.TrimSpace(request.Email)),
		Metadata: map[string]string{"organization_id": request.OrganizationID},
	}
	if name := strings.TrimSpace(request.Name); name != "" {
		params.Name = new(name)
	}
	params.SetIdempotencyKey(request.IdempotencyKey)
	created, err := provider.client.V1Customers.Create(ctx, params)
	if err != nil {
		return Customer{}, err
	}
	return Customer{ID: created.ID}, nil
}

func (provider *stripeProvider) CreateCheckoutSession(ctx context.Context, request CheckoutRequest) (CheckoutSession, error) {
	if request.CustomerID == "" || request.OrganizationID == "" || request.IdempotencyKey == "" || request.TrialDays < 0 || request.ExpiresAt.IsZero() {
		return CheckoutSession{}, ErrInvalidBillingInput
	}
	planKey := NormalizePlanKey(request.PlanKey)
	priceID, ok := provider.config.PriceID(planKey, request.Interval)
	if !ok {
		return CheckoutSession{}, ErrUnconfiguredPrice
	}
	successURL, err := absoluteHTTPURL(request.SuccessURL)
	if err != nil {
		return CheckoutSession{}, err
	}
	cancelURL, err := absoluteHTTPURL(request.CancelURL)
	if err != nil {
		return CheckoutSession{}, err
	}
	subscriptionMetadata := planMetadata(request.OrganizationID, planKey, request.Interval)
	sessionMetadata := planMetadata(request.OrganizationID, planKey, request.Interval)
	sessionMetadata["organization_name"] = request.OrganizationName
	params := &stripe.CheckoutSessionCreateParams{
		Mode:                    new("subscription"),
		PaymentMethodCollection: new("always"),
		// Cards (including Apple Pay and Google Pay) and Link only; methods
		// such as Cash App Pay cause most abandoned and disputed payments.
		PaymentMethodTypes: []*string{new("card"), new("link")},
		Customer:           new(request.CustomerID),
		ClientReferenceID:  new(request.OrganizationID),
		LineItems: []*stripe.CheckoutSessionCreateLineItemParams{{
			Price:    new(priceID),
			Quantity: new(int64(1)),
		}},
		SuccessURL:       new(successURL),
		CancelURL:        new(cancelURL),
		ExpiresAt:        new(request.ExpiresAt.Unix()),
		Metadata:         sessionMetadata,
		SubscriptionData: &stripe.CheckoutSessionCreateSubscriptionDataParams{Metadata: subscriptionMetadata},
	}
	if request.TrialDays > 0 {
		params.SubscriptionData.TrialPeriodDays = new(int64(request.TrialDays))
	}
	params.SetIdempotencyKey(request.IdempotencyKey)
	created, err := provider.client.V1CheckoutSessions.Create(ctx, params)
	if err != nil {
		return CheckoutSession{}, err
	}
	return checkoutSessionFromStripe(created), nil
}

func (provider *stripeProvider) RetrieveCheckoutSession(ctx context.Context, sessionID string) (CheckoutSession, error) {
	if sessionID == "" {
		return CheckoutSession{}, ErrInvalidBillingInput
	}
	session, err := provider.client.V1CheckoutSessions.Retrieve(ctx, sessionID, nil)
	if err != nil {
		return CheckoutSession{}, err
	}
	return checkoutSessionFromStripe(session), nil
}

func (provider *stripeProvider) ExpireCheckoutSession(ctx context.Context, sessionID string) error {
	if sessionID == "" {
		return ErrInvalidBillingInput
	}
	_, err := provider.client.V1CheckoutSessions.Expire(ctx, sessionID, nil)
	return err
}

func (provider *stripeProvider) RetrieveSubscription(ctx context.Context, subscriptionID string) (SubscriptionSnapshot, error) {
	if subscriptionID == "" {
		return SubscriptionSnapshot{}, ErrInvalidBillingInput
	}
	subscription, err := provider.client.V1Subscriptions.Retrieve(ctx, subscriptionID, nil)
	if err != nil {
		return SubscriptionSnapshot{}, err
	}
	return subscriptionSnapshot(subscription), nil
}

// customerSubscriptionLimit bounds the subscriptions read per sync. Checkout
// allows one current subscription, so older entries only inform TrialUsed.
const (
	customerSubscriptionLimit = 10
	customerInvoiceLimit      = 24
)

func (provider *stripeProvider) FetchCustomer(ctx context.Context, customerID string) (StripeCustomer, error) {
	if customerID == "" {
		return StripeCustomer{}, ErrInvalidBillingInput
	}
	customerParams := &stripe.CustomerRetrieveParams{}
	customerParams.AddExpand("invoice_settings.default_payment_method")
	customer, err := provider.client.V1Customers.Retrieve(ctx, customerID, customerParams)
	if err != nil {
		return StripeCustomer{}, err
	}
	result := StripeCustomer{ID: customer.ID, OrganizationID: customer.Metadata["organization_id"]}
	if customer.InvoiceSettings != nil {
		result.DefaultPaymentMethod = paymentMethod(customer.InvoiceSettings.DefaultPaymentMethod)
	}

	subscriptionParams := &stripe.SubscriptionListParams{Customer: new(customerID), Status: new("all")}
	subscriptionParams.Limit = new(int64(customerSubscriptionLimit))
	subscriptionParams.AddExpand("data.default_payment_method")
	subscriptionParams.AddExpand("data.schedule")
	subscriptions := provider.client.V1Subscriptions.List(ctx, subscriptionParams)
	for _, subscription := range subscriptions.Data() {
		result.Subscriptions = append(result.Subscriptions, subscriptionSnapshot(subscription))
	}
	if err := subscriptions.Err(); err != nil {
		return StripeCustomer{}, err
	}

	invoiceParams := &stripe.InvoiceListParams{Customer: new(customerID)}
	invoiceParams.Limit = new(int64(customerInvoiceLimit))
	invoices := provider.client.V1Invoices.List(ctx, invoiceParams)
	for _, invoice := range invoices.Data() {
		if invoice.Status == stripe.InvoiceStatusDraft {
			continue
		}
		result.Invoices = append(result.Invoices, core.Invoice{
			ID: invoice.ID, Number: invoice.Number, Status: string(invoice.Status), AmountCents: invoice.AmountDue,
			Currency: string(invoice.Currency), CreatedAt: fromUnix(invoice.Created),
			PeriodStart: fromUnix(invoice.PeriodStart), PeriodEnd: fromUnix(invoice.PeriodEnd),
			HostedURL: invoice.HostedInvoiceURL, PDFURL: invoice.InvoicePDF,
		})
	}
	if err := invoices.Err(); err != nil {
		return StripeCustomer{}, err
	}
	return result, nil
}

func (provider *stripeProvider) CreatePortalSession(ctx context.Context, customerID, returnURL, idempotencyKey string) (PortalSession, error) {
	if customerID == "" || idempotencyKey == "" {
		return PortalSession{}, ErrInvalidBillingInput
	}
	validatedReturn, err := absoluteHTTPURL(returnURL)
	if err != nil {
		return PortalSession{}, err
	}
	params := &stripe.BillingPortalSessionCreateParams{
		Customer:  new(customerID),
		ReturnURL: new(validatedReturn),
	}
	params.SetIdempotencyKey(idempotencyKey)
	created, err := provider.client.V1BillingPortalSessions.Create(ctx, params)
	if err != nil {
		return PortalSession{}, err
	}
	return PortalSession{ID: created.ID, URL: created.URL}, nil
}

func (provider *stripeProvider) PreviewPlanChange(ctx context.Context, current SubscriptionSnapshot, nextPriceID string) (PlanChangeQuote, error) {
	if current.ID == "" || current.ItemID == "" || nextPriceID == "" {
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

// ChangePlanNow swaps the subscription's single item and invoices the
// proration immediately. The update stays pending until payment succeeds.
func (provider *stripeProvider) ChangePlanNow(ctx context.Context, current SubscriptionSnapshot, nextPriceID, idempotencyKey string) error {
	if current.ID == "" || current.ItemID == "" || nextPriceID == "" || idempotencyKey == "" {
		return ErrInvalidBillingInput
	}
	params := &stripe.SubscriptionUpdateParams{
		Items:             []*stripe.SubscriptionUpdateItemParams{{ID: new(current.ItemID), Price: new(nextPriceID), Quantity: new(int64(1))}},
		ProrationBehavior: new("always_invoice"),
		PaymentBehavior:   new("pending_if_incomplete"),
	}
	params.SetIdempotencyKey(idempotencyKey)
	_, err := provider.client.V1Subscriptions.Update(ctx, current.ID, params)
	return err
}

// SchedulePlanChange keeps the current price until the period ends and then
// releases the subscription on the next price. A retry after a partial failure
// resumes the schedule this change key created.
func (provider *stripeProvider) SchedulePlanChange(ctx context.Context, current SubscriptionSnapshot, nextPriceID, idempotencyKey string) (string, error) {
	planKey, interval, mapped := provider.config.planFromPriceID(nextPriceID)
	if current.ID == "" || current.PriceID == "" || !mapped || current.CurrentPeriodEnd.IsZero() || idempotencyKey == "" {
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
			return "", errors.New("subscription schedule does not belong to this plan change")
		}
		phaseStart = existing.CurrentPhase.StartDate
	}
	periodEnd := current.CurrentPeriodEnd.Unix()
	if phaseStart >= periodEnd {
		return "", ErrInvalidBillingInput
	}
	stripeInterval := "month"
	if interval == IntervalAnnual {
		stripeInterval = "year"
	}
	params := &stripe.SubscriptionScheduleUpdateParams{
		EndBehavior:       new("release"),
		ProrationBehavior: new("none"),
		Phases: []*stripe.SubscriptionScheduleUpdatePhaseParams{
			{
				StartDate: new(phaseStart),
				EndDate:   new(periodEnd),
				Items:     []*stripe.SubscriptionScheduleUpdatePhaseItemParams{{Price: new(current.PriceID), Quantity: new(int64(1))}},
			},
			{
				StartDate: new(periodEnd),
				Duration:  &stripe.SubscriptionScheduleUpdatePhaseDurationParams{Interval: new(stripeInterval), IntervalCount: new(int64(1))},
				Items:     []*stripe.SubscriptionScheduleUpdatePhaseItemParams{{Price: new(nextPriceID), Quantity: new(int64(1))}},
				// Phase metadata is copied onto the subscription when the phase
				// starts, so it must keep the organization binding.
				Metadata: planMetadata(current.OrganizationID, planKey, interval),
			},
		},
	}
	params.SetIdempotencyKey(idempotencyKey + ":update")
	if _, err := provider.client.V1SubscriptionSchedules.Update(ctx, scheduleID, params); err != nil {
		return scheduleID, err
	}
	return scheduleID, nil
}

func (provider *stripeProvider) SetCancelAtPeriodEnd(ctx context.Context, subscriptionID string, cancel bool, idempotencyKey string) error {
	if subscriptionID == "" || idempotencyKey == "" {
		return ErrInvalidBillingInput
	}
	params := &stripe.SubscriptionUpdateParams{CancelAtPeriodEnd: new(cancel)}
	params.SetIdempotencyKey(idempotencyKey)
	_, err := provider.client.V1Subscriptions.Update(ctx, subscriptionID, params)
	return err
}

func (provider *stripeProvider) ReleaseSchedule(ctx context.Context, scheduleID, idempotencyKey string) error {
	if scheduleID == "" || idempotencyKey == "" {
		return ErrInvalidBillingInput
	}
	params := &stripe.SubscriptionScheduleReleaseParams{}
	params.SetIdempotencyKey(idempotencyKey)
	_, err := provider.client.V1SubscriptionSchedules.Release(ctx, scheduleID, params)
	return err
}

func subscriptionSnapshot(subscription *stripe.Subscription) SubscriptionSnapshot {
	snapshot := SubscriptionSnapshot{
		ID:                subscription.ID,
		CustomerID:        stripeCustomerID(subscription.Customer),
		OrganizationID:    subscription.Metadata["organization_id"],
		Status:            string(subscription.Status),
		CancelAtPeriodEnd: subscription.CancelAtPeriodEnd,
		TrialStarted:      subscription.TrialStart > 0,
		CreatedAt:         fromUnix(subscription.Created),
		PaymentMethod:     paymentMethod(subscription.DefaultPaymentMethod),
	}
	if subscription.Items != nil {
		for _, item := range subscription.Items.Data {
			if item == nil {
				continue
			}
			if start := fromUnix(item.CurrentPeriodStart); !start.IsZero() && (snapshot.CurrentPeriodStart.IsZero() || start.Before(snapshot.CurrentPeriodStart)) {
				snapshot.CurrentPeriodStart = start
			}
		}
		snapshot.CurrentPeriodEnd = currentPeriodEnd(subscription.Items)
		if len(subscription.Items.Data) == 1 && subscription.Items.Data[0] != nil && subscription.Items.Data[0].Price != nil {
			snapshot.PriceID = subscription.Items.Data[0].Price.ID
			snapshot.ItemID = subscription.Items.Data[0].ID
		}
	}
	if pending := subscription.PendingUpdate; pending != nil && len(pending.SubscriptionItems) == 1 && pending.SubscriptionItems[0] != nil && pending.SubscriptionItems[0].Price != nil {
		snapshot.PendingPriceID = pending.SubscriptionItems[0].Price.ID
	}
	if schedule := subscription.Schedule; schedule != nil {
		snapshot.ScheduleID = schedule.ID
		snapshot.ScheduledPriceID = nextPhasePrice(schedule)
	}
	return snapshot
}

// nextPhasePrice returns the single price of the schedule phase after the
// current one, which is how the dashboard schedules downgrades.
func nextPhasePrice(schedule *stripe.SubscriptionSchedule) string {
	if schedule.CurrentPhase == nil {
		return ""
	}
	for _, phase := range schedule.Phases {
		if phase != nil && phase.StartDate >= schedule.CurrentPhase.EndDate && len(phase.Items) == 1 && phase.Items[0] != nil && phase.Items[0].Price != nil {
			return phase.Items[0].Price.ID
		}
	}
	return ""
}

func paymentMethod(method *stripe.PaymentMethod) *PaymentMethod {
	if method == nil || method.Card == nil || method.Card.Last4 == "" {
		return nil
	}
	return &PaymentMethod{
		Brand: string(method.Card.Brand), Last4: method.Card.Last4,
		ExpMonth: method.Card.ExpMonth, ExpYear: method.Card.ExpYear,
	}
}

func planMetadata(organizationID, planKey, interval string) map[string]string {
	metadata := map[string]string{"plan_key": planKey, "interval": interval}
	if organizationID != "" {
		metadata["organization_id"] = organizationID
	}
	return metadata
}

func checkoutSessionFromStripe(session *stripe.CheckoutSession) CheckoutSession {
	result := CheckoutSession{
		ID:                session.ID,
		URL:               session.URL,
		CustomerID:        stripeCustomerID(session.Customer),
		SubscriptionID:    stripeSubscriptionID(session.Subscription),
		ClientReferenceID: session.ClientReferenceID,
		Status:            string(session.Status),
		PaymentStatus:     string(session.PaymentStatus),
	}
	if session.ExpiresAt > 0 {
		result.ExpiresAt = time.Unix(session.ExpiresAt, 0).UTC()
	}
	return result
}

// currentPeriodEnd returns the earliest item period end. Since the 2025-03-31
// API version Stripe reports billing periods per subscription item.
func currentPeriodEnd(items *stripe.SubscriptionItemList) time.Time {
	var end time.Time
	if items == nil {
		return end
	}
	for _, item := range items.Data {
		if item == nil || item.CurrentPeriodEnd <= 0 {
			continue
		}
		if candidate := time.Unix(item.CurrentPeriodEnd, 0).UTC(); end.IsZero() || candidate.Before(end) {
			end = candidate
		}
	}
	return end
}

func stripeCustomerID(value *stripe.Customer) string {
	if value == nil {
		return ""
	}
	return value.ID
}

func stripeSubscriptionID(value *stripe.Subscription) string {
	if value == nil {
		return ""
	}
	return value.ID
}

func absoluteHTTPURL(value string) (string, error) {
	parsed, err := url.ParseRequestURI(strings.TrimSpace(value))
	if err != nil || parsed.Host == "" || (parsed.Scheme != "https" && parsed.Scheme != "http") {
		return "", fmt.Errorf("%w: %q is not an absolute HTTP(S) URL", ErrInvalidBillingInput, value)
	}
	return parsed.String(), nil
}
