package subscriptions

import (
	"errors"
	"fmt"
	"net/url"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
)

var (
	ErrBillingOwnerRequired = errors.New("organization owner role required for billing")
	ErrCheckoutUnavailable  = errors.New("checkout session is unavailable")
	ErrInvalidReturnPath    = errors.New("return path must stay inside the dashboard")
	ErrPlanUnavailable      = errors.New("selected billing plan is unavailable")
)

const (
	intervalMonthly = "monthly"
	intervalAnnual  = "annual"
	checkoutOpen    = "open"
	checkoutDone    = "complete"
	checkoutCancel  = "canceled"
)

type BillingSnapshot struct {
	Account      core.Account
	Organization core.Organization
	Plan         core.Plan
	Plans        []core.Plan
}

// BillingService owns billing state transitions while core.Store remains the
// sole owner of account and organization persistence.
type BillingService struct {
	store *core.Store
	now   func() time.Time
}

func NewBillingService(store *core.Store) BillingService {
	return BillingService{store: store, now: func() time.Time { return time.Now().UTC() }}
}

func (service BillingService) Snapshot(principal core.Principal) (BillingSnapshot, error) {
	account, err := service.store.SnapshotAs(principal)
	if err != nil {
		return BillingSnapshot{}, err
	}
	organization, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil {
		return BillingSnapshot{}, err
	}
	plan, _ := service.store.Plan(organization.PlanID)
	plans := service.availablePlans()
	return BillingSnapshot{Account: account, Organization: organization, Plan: plan, Plans: plans}, nil
}

func (service BillingService) availablePlans() []core.Plan {
	plans := service.store.Plans()
	plans = slices.DeleteFunc(plans, func(plan core.Plan) bool {
		return !plan.Visible || plan.PriceCents <= 0 || normalizePlanKey(plan.Key) == ""
	})
	slices.SortFunc(plans, func(left, right core.Plan) int {
		return strings.Compare(normalizePlanKey(left.Key), normalizePlanKey(right.Key))
	})
	return plans
}

// BeginDemoCheckout creates a one-use, tenant-owned local checkout. It stores
// only a plan selection and safe return path; the demo never collects card
// data and never calls Stripe.
func (service BillingService) BeginDemoCheckout(principal core.Principal, planKey, interval, returnPath string) (core.CheckoutSession, error) {
	planID, planKey, err := service.planCandidate(planKey, interval)
	if err != nil {
		return core.CheckoutSession{}, err
	}
	returnPath, err = NormalizeReturnPath(returnPath, "/subscriptions")
	if err != nil {
		return core.CheckoutSession{}, err
	}
	now := service.now()
	session := core.CheckoutSession{
		ID: core.NewID("cs_local"), OwnerUserID: principal.UserID,
		OrganizationID: principal.OrganizationID, Interval: interval,
		ReturnPath: returnPath, Status: checkoutOpen, Demo: true, CreatedAt: now,
		ExpiresAt: now.Add(30 * time.Minute),
	}
	err = service.store.UpdateAsWithPlan(principal, planID, func(account *core.Account, plan core.Plan) error {
		if err := validatePlanSelection(plan, planKey, interval); err != nil {
			return err
		}
		if account.ActiveOrganizationID != principal.OrganizationID {
			return core.ErrForbidden
		}
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Status != "active" || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		session.PlanID = plan.ID
		organization.CheckoutSessions = map[string]core.CheckoutSession{session.ID: session}
		organization.Billing.CheckoutID = session.ID
		organization.Billing.ReturnPath = returnPath
		account.Organizations[organization.ID] = organization
		return nil
	})
	if err != nil {
		if errors.Is(err, core.ErrNotFound) {
			return core.CheckoutSession{}, ErrPlanUnavailable
		}
		return core.CheckoutSession{}, err
	}
	return session, nil
}

// Checkout returns only the current active organization's own checkout, so a
// session ID cannot cross tenant boundaries or follow an organization switch.
func (service BillingService) Checkout(principal core.Principal, sessionID string) (core.CheckoutSession, error) {
	if sessionID == "" {
		return core.CheckoutSession{}, core.ErrNotFound
	}
	account, err := service.store.SnapshotAs(principal)
	if err != nil {
		return core.CheckoutSession{}, err
	}
	if account.ActiveOrganizationID != principal.OrganizationID {
		return core.CheckoutSession{}, core.ErrForbidden
	}
	organization, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil {
		return core.CheckoutSession{}, err
	}
	session, ok := organization.CheckoutSessions[sessionID]
	if !ok || session.OrganizationID != organization.ID || session.OwnerUserID != principal.UserID || session.ID != organization.Billing.CheckoutID || session.Status != checkoutOpen || !session.ExpiresAt.After(service.now()) {
		return core.CheckoutSession{}, core.ErrNotFound
	}
	return session, nil
}

// CheckoutForReturn permits an owner to reconcile their own completed session
// after the webhook has consumed it. A pending session must still be current
// and unexpired; terminal records remain tenant-bound by their stored owner
// and organization IDs.
func (service BillingService) CheckoutForReturn(principal core.Principal, sessionID string) (core.CheckoutSession, error) {
	if sessionID == "" {
		return core.CheckoutSession{}, core.ErrNotFound
	}
	account, err := service.store.SnapshotAs(principal)
	if err != nil {
		return core.CheckoutSession{}, err
	}
	if account.ActiveOrganizationID != principal.OrganizationID {
		return core.CheckoutSession{}, core.ErrForbidden
	}
	organization, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil {
		return core.CheckoutSession{}, err
	}
	session, ok := organization.CheckoutSessions[sessionID]
	if !ok || session.ID != sessionID || session.OwnerUserID != principal.UserID || session.OrganizationID != organization.ID {
		return core.CheckoutSession{}, core.ErrNotFound
	}
	if session.Status == checkoutOpen && (organization.Billing.CheckoutID != sessionID || !session.ExpiresAt.After(service.now())) {
		return core.CheckoutSession{}, core.ErrNotFound
	}
	if session.Status != checkoutOpen && session.Status != checkoutDone && session.Status != checkoutCancel {
		return core.CheckoutSession{}, core.ErrNotFound
	}
	return session, nil
}

// CompleteDemoCheckout consumes the current demo session exactly once. The
// session is looked up and the organization is updated in one scoped commit.
func (service BillingService) CompleteDemoCheckout(principal core.Principal, sessionID string, canceled bool) (string, error) {
	pending, err := service.Checkout(principal, sessionID)
	if err != nil {
		return "", ErrCheckoutUnavailable
	}
	var returnTo string
	mutate := func(account *core.Account, plan core.Plan) error {
		if account.ActiveOrganizationID != principal.OrganizationID {
			return core.ErrForbidden
		}
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		session, ok := organization.CheckoutSessions[sessionID]
		if !ok || session.ID != sessionID || session.OrganizationID != organization.ID || session.OwnerUserID != principal.UserID || session.Status != checkoutOpen || !session.ExpiresAt.After(service.now()) || organization.Billing.CheckoutID != sessionID {
			return ErrCheckoutUnavailable
		}
		path, err := NormalizeReturnPath(session.ReturnPath, "/subscriptions")
		if err != nil {
			return err
		}
		if canceled {
			session.Status = checkoutCancel
			organization.Billing.CheckoutID = ""
			organization.Billing.ReturnPath = ""
		} else {
			if plan.ID != session.PlanID || plan.PriceCents <= 0 || (session.Interval == intervalAnnual && plan.AnnualPriceCents <= 0) {
				return ErrPlanUnavailable
			}
			session.Status = checkoutDone
			organization.PlanID = plan.ID
			organization.Billing.PlanKey = normalizePlanKey(plan.Key)
			organization.Billing.Interval = session.Interval
			organization.Billing.Status = "active"
			organization.BillingStatus = "active"
			organization.PaymentMethodValidated = true
			period := 30 * 24 * time.Hour
			if session.Interval == intervalAnnual {
				period = 365 * 24 * time.Hour
			}
			organization.BillingRenewal = service.now().Add(period)
			organization.Billing.CheckoutID = ""
			organization.Billing.ReturnPath = ""
		}
		organization.CheckoutSessions[sessionID] = session
		account.Organizations[organization.ID] = organization
		returnTo = withOutcome(path, map[bool]string{true: "canceled", false: "success"}[canceled])
		return nil
	}
	if canceled {
		err = service.store.UpdateAs(principal, func(account *core.Account) error { return mutate(account, core.Plan{}) })
	} else {
		err = service.store.UpdateAsWithPlan(principal, pending.PlanID, mutate)
		if errors.Is(err, core.ErrNotFound) {
			err = ErrPlanUnavailable
		}
	}
	if err != nil {
		return "", err
	}
	return returnTo, nil
}

// RecordCustomer stores the canonical Stripe customer ID only on the scoped
// owner organization. It returns an ID already stored by a concurrent request
// when another request won the same idempotent customer creation.
func (service BillingService) RecordCustomer(principal core.Principal, createdID string) (string, error) {
	if strings.TrimSpace(createdID) == "" {
		return "", core.ErrInvalid
	}
	var customerID string
	err := service.store.UpdateAs(principal, func(account *core.Account) error {
		if account.ActiveOrganizationID != principal.OrganizationID {
			return core.ErrForbidden
		}
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		if organization.Billing.CustomerID == "" {
			organization.Billing.CustomerID = createdID
		}
		customerID = organization.Billing.CustomerID
		account.Organizations[organization.ID] = organization
		return nil
	})
	return customerID, err
}

// RecordExternalCheckout binds the remote checkout ID to the exact tenant,
// selected plan, interval, and return path before the customer can pay.
func (service BillingService) RecordExternalCheckout(principal core.Principal, planKey, interval, returnPath string, providerSession payments.CheckoutSession) error {
	planID, planKey, err := service.planCandidate(planKey, interval)
	if err != nil {
		return err
	}
	returnPath, err = NormalizeReturnPath(returnPath, "/subscriptions")
	if err != nil {
		return err
	}
	if providerSession.ID == "" {
		return core.ErrInvalid
	}
	now := service.now()
	localSession := core.CheckoutSession{
		ID: providerSession.ID, OwnerUserID: principal.UserID, OrganizationID: principal.OrganizationID,
		Interval: interval, ReturnPath: returnPath, Status: checkoutOpen,
		CreatedAt: now, ExpiresAt: now.Add(24 * time.Hour),
	}
	err = service.store.UpdateAsWithPlan(principal, planID, func(account *core.Account, plan core.Plan) error {
		if err := validatePlanSelection(plan, planKey, interval); err != nil {
			return err
		}
		if account.ActiveOrganizationID != principal.OrganizationID {
			return core.ErrForbidden
		}
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Status != "active" || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		if organization.Billing.CustomerID == "" {
			return core.ErrConflict
		}
		if providerSession.CustomerID != organization.Billing.CustomerID || providerSession.ClientReferenceID != organization.ID {
			return core.ErrForbidden
		}
		for _, pending := range organization.CheckoutSessions {
			if pending.Status == checkoutOpen {
				return core.ErrConflict
			}
		}
		localSession.PlanID = plan.ID
		organization.CheckoutSessions = map[string]core.CheckoutSession{localSession.ID: localSession}
		organization.Billing.CheckoutID = localSession.ID
		organization.Billing.ReturnPath = returnPath
		account.Organizations[organization.ID] = organization
		return nil
	})
	if errors.Is(err, core.ErrNotFound) {
		return ErrPlanUnavailable
	}
	return err
}

// ExpireExternalCheckout consumes the current local checkout only after the
// provider confirms that the matching remote Checkout Session has expired.
func (service BillingService) ExpireExternalCheckout(principal core.Principal, sessionID string) error {
	return service.store.UpdateAs(principal, func(account *core.Account) error {
		if account.ActiveOrganizationID != principal.OrganizationID {
			return core.ErrForbidden
		}
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		if organization.Billing.CheckoutID != sessionID {
			return core.ErrConflict
		}
		session, ok := organization.CheckoutSessions[sessionID]
		if !ok || session.ID != sessionID || session.OwnerUserID != principal.UserID || session.OrganizationID != organization.ID || session.Status != checkoutOpen {
			return ErrCheckoutUnavailable
		}
		session.Status = checkoutCancel
		organization.CheckoutSessions[sessionID] = session
		organization.Billing.CheckoutID = ""
		organization.Billing.ReturnPath = ""
		account.Organizations[organization.ID] = organization
		return nil
	})
}

// ReconcileCheckoutReturn verifies that Stripe's retrieved session matches
// this tenant's current pending checkout. The webhook alone may activate a
// paid subscription; a browser redirect is only a notification that Stripe
// returned control to the dashboard.
func (service BillingService) ReconcileCheckoutReturn(principal core.Principal, sessionID string, providerSession payments.CheckoutSession) (string, string, error) {
	account, err := service.store.SnapshotAs(principal)
	if err != nil {
		return "", "", err
	}
	if account.ActiveOrganizationID != principal.OrganizationID {
		return "", "", core.ErrForbidden
	}
	organization, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil {
		return "", "", err
	}
	localSession, ok := organization.CheckoutSessions[sessionID]
	if !ok || localSession.ID != sessionID || localSession.OwnerUserID != principal.UserID || localSession.OrganizationID != organization.ID || providerSession.ID != sessionID {
		return "", "", core.ErrNotFound
	}
	if localSession.Status == checkoutOpen && organization.Billing.CheckoutID != sessionID {
		return "", "", core.ErrNotFound
	}
	if localSession.Status != checkoutOpen && localSession.Status != checkoutDone {
		return "", "", core.ErrNotFound
	}
	if providerSession.ClientReferenceID != organization.ID || organization.Billing.CustomerID == "" || providerSession.CustomerID != organization.Billing.CustomerID {
		return "", "", core.ErrForbidden
	}
	status := "pending"
	if providerSession.Status == "complete" && (providerSession.PaymentStatus == "paid" || providerSession.PaymentStatus == "no_payment_required") {
		status = "processing"
		if localSession.Status == checkoutDone && organization.Billing.SubscriptionID != "" && organization.Billing.SubscriptionID == providerSession.SubscriptionID && organization.BillingStatus == "active" {
			status = "success"
		}
	}
	return localSession.ReturnPath, status, nil
}

// ApplyStripeEvent projects only a fresh event associated with the stored
// customer or subscription. A checkout completion must match the pending
// checkout ID and its server-side tenant, plan, and interval selection.
func (service BillingService) ApplyStripeEvent(event payments.BillingEvent) error {
	if event.ID == "" || event.CreatedAt.IsZero() || (event.CustomerID == "" && event.SubscriptionID == "") {
		return core.ErrInvalid
	}
	orderAt := event.CreatedAt
	if event.ProviderCurrent && !event.ObservedAt.IsZero() {
		orderAt = event.ObservedAt
	}
	return service.store.ApplyBillingEventWithPlans(event.ID, event.CustomerID, event.SubscriptionID, func(organization *core.Organization, plans map[string]core.Plan) error {
		if event.CustomerID != "" && event.CustomerID != organization.Billing.CustomerID {
			return nil
		}
		if event.OrganizationID != organization.ID || orderAt.Before(organization.Billing.LastEventAt) {
			return nil
		}
		if orderAt.Equal(organization.Billing.LastEventAt) && event.ProviderCurrent && isEntitledSubscriptionState(event.SubscriptionState) && !isEntitledSubscriptionState(organization.Billing.Status) {
			// Stripe event timestamps are second-granular. If two provider
			// snapshots tie exactly, keep the more restrictive state until a
			// later observation resolves the subscription.
			return nil
		}
		if event.Type == "checkout.session.completed" || event.Type == "checkout.session.async_payment_succeeded" {
			return applyCheckoutEvent(plans, organization, event, orderAt)
		}
		return applySubscriptionEvent(plans, organization, event, orderAt)
	})
}

func applyCheckoutEvent(plans map[string]core.Plan, organization *core.Organization, event payments.BillingEvent, orderAt time.Time) error {
	if event.CheckoutID == "" || event.SubscriptionID == "" || event.CheckoutID != organization.Billing.CheckoutID ||
		event.ClientReferenceID != organization.ID || event.CheckoutStatus != "complete" {
		return nil
	}
	session, ok := organization.CheckoutSessions[event.CheckoutID]
	if !ok || session.ID != event.CheckoutID || session.OrganizationID != organization.ID || session.PlanID == "" || session.Status != checkoutOpen || !session.ExpiresAt.After(event.CreatedAt) {
		return nil
	}
	planKey := normalizePlanKey(event.PlanKey)
	if session.Interval != event.Interval || planKey == "" {
		return nil
	}
	plan, planExists := plans[session.PlanID]
	if planExists && (normalizePlanKey(plan.Key) == "" || normalizePlanKey(plan.Key) != planKey) {
		return nil
	}
	if event.PaymentStatus != "paid" && event.PaymentStatus != "no_payment_required" {
		if planExists {
			organization.Billing.LastEventAt = orderAt
		}
		return nil
	}
	trial := event.PaymentStatus == "no_payment_required"
	if event.ProviderCurrent {
		if event.SubscriptionState != "active" && event.SubscriptionState != "trialing" {
			if planExists {
				organization.Billing.LastEventAt = orderAt
			}
			return nil
		}
		trial = event.SubscriptionState == "trialing"
	}
	if !planExists {
		return fmt.Errorf("%w: checkout plan %q no longer exists", ErrPlanUnavailable, session.PlanID)
	}
	activateSubscription(organization, session, plan, event.SubscriptionID, event.CreatedAt, orderAt, trial)
	if !event.PeriodEnd.IsZero() {
		organization.BillingRenewal = event.PeriodEnd
	}
	return nil
}

func applySubscriptionEvent(plans map[string]core.Plan, organization *core.Organization, event payments.BillingEvent, orderAt time.Time) error {
	if event.SubscriptionID == "" {
		return nil
	}
	if organization.Billing.SubscriptionID != "" {
		if organization.Billing.SubscriptionID != event.SubscriptionID {
			return nil
		}
	} else {
		if organization.Billing.CheckoutID == "" {
			return nil
		}
		session, ok := organization.CheckoutSessions[organization.Billing.CheckoutID]
		if !ok || session.Status != checkoutOpen || session.OrganizationID != organization.ID || !session.ExpiresAt.After(event.CreatedAt) {
			return nil
		}
		if event.SubscriptionState != "active" && event.SubscriptionState != "trialing" {
			return nil
		}
		planKey := normalizePlanKey(event.PlanKey)
		if planKey == "" || session.Interval != event.Interval {
			return nil
		}
		plan, planExists := plans[session.PlanID]
		if !planExists {
			return fmt.Errorf("%w: checkout plan %q no longer exists", ErrPlanUnavailable, session.PlanID)
		}
		if normalizePlanKey(plan.Key) != planKey {
			return nil
		}
		activateSubscription(organization, session, plan, event.SubscriptionID, event.CreatedAt, orderAt, event.SubscriptionState == "trialing")
		if !event.PeriodEnd.IsZero() {
			organization.BillingRenewal = event.PeriodEnd
		}
		return nil
	}
	if event.ProviderCurrent && (event.PlanKey == "" || (event.Interval != intervalMonthly && event.Interval != intervalAnnual)) {
		organization.Billing.Status = "unmapped_price"
		organization.BillingStatus = "billing_required"
		organization.Billing.LastEventAt = orderAt
		return nil
	}

	switch event.SubscriptionState {
	case "active", "trialing":
		planKey := normalizePlanKey(event.PlanKey)
		if planKey != "" && (event.Interval == intervalMonthly || event.Interval == intervalAnnual) {
			plan, ok := plans[planKey]
			if !ok {
				return fmt.Errorf("%w: subscription plan %q no longer exists", ErrPlanUnavailable, planKey)
			}
			organization.PlanID = plan.ID
			organization.Billing.PlanKey = normalizePlanKey(plan.Key)
			organization.Billing.Interval = event.Interval
		}
		organization.Billing.Status = event.SubscriptionState
		organization.BillingStatus = "active"
		organization.PaymentMethodValidated = true
		if !event.PeriodEnd.IsZero() {
			organization.BillingRenewal = event.PeriodEnd
		}
	case "canceled", "incomplete_expired", "past_due", "unpaid", "paused":
		organization.Billing.Status = event.SubscriptionState
		organization.BillingStatus = "billing_required"
	}
	organization.Billing.LastEventAt = orderAt
	return nil
}

func activateSubscription(organization *core.Organization, session core.CheckoutSession, plan core.Plan, subscriptionID string, at, orderAt time.Time, trial bool) {
	organization.PlanID = plan.ID
	organization.Billing.PlanKey = normalizePlanKey(plan.Key)
	organization.Billing.Interval = session.Interval
	organization.Billing.SubscriptionID = subscriptionID
	organization.Billing.Status = "active"
	organization.BillingStatus = "active"
	organization.PaymentMethodValidated = true
	period := 30 * 24 * time.Hour
	if session.Interval == intervalAnnual {
		period = 365 * 24 * time.Hour
	}
	if trial {
		organization.Billing.Status = "trialing"
		period = time.Duration(plan.TrialDays) * 24 * time.Hour
	}
	organization.BillingRenewal = at.Add(period)
	organization.Billing.CheckoutID = ""
	organization.Billing.ReturnPath = ""
	organization.Billing.LastEventAt = orderAt
	session.Status = checkoutDone
	organization.CheckoutSessions[session.ID] = session
}

func isEntitledSubscriptionState(status string) bool {
	return status == "active" || status == "trialing"
}

func (service BillingService) selectedPlan(planKey, interval string) (core.Plan, error) {
	planID, planKey, err := service.planCandidate(planKey, interval)
	if err != nil {
		return core.Plan{}, err
	}
	plan, ok := service.store.Plan(planID)
	if !ok {
		return core.Plan{}, ErrPlanUnavailable
	}
	if err := validatePlanSelection(plan, planKey, interval); err != nil {
		return core.Plan{}, err
	}
	return plan, nil
}

func (service BillingService) planCandidate(planKey, interval string) (string, string, error) {
	planKey = normalizePlanKey(planKey)
	if planKey == "" {
		return "", "", ErrPlanUnavailable
	}
	if interval != intervalMonthly && interval != intervalAnnual {
		return "", "", core.ErrInvalid
	}
	plan, ok := service.store.Plan(planKey)
	if !ok {
		return "", "", ErrPlanUnavailable
	}
	return plan.ID, planKey, nil
}

func validatePlanSelection(plan core.Plan, planKey, interval string) error {
	if plan.ID == "" || normalizePlanKey(plan.Key) != planKey || !plan.Visible || plan.PriceCents <= 0 {
		return ErrPlanUnavailable
	}
	if interval == intervalAnnual && plan.AnnualPriceCents <= 0 {
		return ErrPlanUnavailable
	}
	return nil
}

// NormalizeReturnPath rejects absolute, protocol-relative, escaped authority,
// and backslash paths so checkout cannot be used as an open redirect.
func NormalizeReturnPath(value, fallback string) (string, error) {
	if strings.TrimSpace(value) == "" {
		value = fallback
	}
	value = strings.TrimSpace(value)
	if strings.ContainsAny(value, "\\\r\n") {
		return "", ErrInvalidReturnPath
	}
	parsed, err := url.ParseRequestURI(value)
	if err != nil || parsed.IsAbs() || parsed.Host != "" || parsed.User != nil || parsed.Fragment != "" || !strings.HasPrefix(parsed.Path, "/") || strings.HasPrefix(parsed.Path, "//") {
		return "", ErrInvalidReturnPath
	}
	if decoded, err := url.PathUnescape(parsed.EscapedPath()); err != nil || strings.HasPrefix(decoded, "//") || strings.Contains(decoded, "\\") {
		return "", ErrInvalidReturnPath
	}
	return parsed.RequestURI(), nil
}

func normalizePlanKey(value string) string {
	value = strings.ToLower(strings.TrimSpace(value))
	if len(value) == 0 || len(value) > 64 {
		return ""
	}
	for index, character := range value {
		valid := character >= 'a' && character <= 'z' || character >= '0' && character <= '9' ||
			(index > 0 && (character == '-' || character == '_'))
		if !valid {
			return ""
		}
	}
	return value
}

func withOutcome(returnPath, outcome string) string {
	parsed, _ := url.Parse(returnPath)
	query := parsed.Query()
	query.Set("payment", outcome)
	parsed.RawQuery = query.Encode()
	return parsed.RequestURI()
}
