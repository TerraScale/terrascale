package subscriptions

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
)

var (
	ErrBillingOwnerRequired = fmt.Errorf("%w: organization owner role required for billing", core.ErrForbidden)
	ErrCheckoutUnavailable  = fmt.Errorf("%w: checkout session is unavailable", core.ErrNotFound)
	ErrPlanUnavailable      = fmt.Errorf("%w: selected billing plan is unavailable", core.ErrInvalid)
)

const (
	intervalMonthly = payments.IntervalMonthly
	intervalAnnual  = payments.IntervalAnnual
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
	store core.Store
	now   func() time.Time
}

func NewBillingService(store core.Store) BillingService {
	return BillingService{store: store, now: func() time.Time { return time.Now().UTC() }}
}

func (service BillingService) Snapshot(ctx context.Context, principal core.Principal) (BillingSnapshot, error) {
	account, err := service.store.SnapshotAs(ctx, principal)
	if err != nil {
		return BillingSnapshot{}, err
	}
	organization, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil {
		return BillingSnapshot{}, err
	}
	plan, err := service.store.Plan(ctx, organization.PlanID)
	if err != nil && !errors.Is(err, core.ErrNotFound) {
		return BillingSnapshot{}, err
	}
	plans, err := service.availablePlans(ctx)
	if err != nil {
		return BillingSnapshot{}, err
	}
	return BillingSnapshot{Account: account, Organization: organization, Plan: plan, Plans: plans}, nil
}

// ScheduleDemoChange records a downgrade the demo applies at renewal. Live
// scheduled changes come from Stripe through ProjectCustomerState.
func (service BillingService) ScheduleDemoChange(ctx context.Context, principal core.Principal, planKey, interval string, effectiveAt time.Time) error {
	if planKey != "free" {
		if _, err := service.selectedPlan(ctx, planKey, interval); err != nil {
			return err
		}
	}
	return service.store.UpdateAs(ctx, principal, func(account *core.Account) error {
		org, ok := account.Organizations[principal.OrganizationID]
		if !ok || org.OwnerUserID != principal.UserID || org.Archived || org.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		if org.Billing.SubscriptionID == "" || effectiveAt.IsZero() {
			return core.ErrConflict
		}
		if org.Billing.PendingPlanKey != "" || org.Billing.ScheduledPlanKey != "" {
			if org.Billing.ScheduledPlanKey == planKey && org.Billing.ScheduledInterval == interval {
				return nil
			}
			return core.ErrConflict
		}
		// The callback may run again, so the captured interval stays as given.
		scheduledInterval := interval
		if planKey == "free" {
			if org.Kind != "personal" {
				return ErrPlanUnavailable
			}
			scheduledInterval = ""
		}
		org.Billing.ScheduledPlanKey = planKey
		org.Billing.ScheduledInterval = scheduledInterval
		org.Billing.ScheduledAt = effectiveAt
		org.Billing.ChangeVersion++
		account.Organizations[org.ID] = org
		return nil
	})
}

// UndoDemoChange clears the demo's scheduled downgrade.
func (service BillingService) UndoDemoChange(ctx context.Context, principal core.Principal) error {
	return service.store.UpdateAs(ctx, principal, func(account *core.Account) error {
		org, ok := account.Organizations[principal.OrganizationID]
		if !ok || org.OwnerUserID != principal.UserID {
			return ErrBillingOwnerRequired
		}
		if org.Billing.SubscriptionID == "" {
			return core.ErrConflict
		}
		clearScheduledChange(&org.Billing)
		account.Organizations[org.ID] = org
		return nil
	})
}

// ChangeDemoPlan applies an upgrade at once in the in-memory preview without
// contacting Stripe. Downgrades, including Free, are scheduled instead.
func (service BillingService) ChangeDemoPlan(ctx context.Context, principal core.Principal, planKey, interval string) error {
	target, err := service.selectedPlan(ctx, planKey, interval)
	if err != nil {
		return err
	}
	return service.store.UpdateAs(ctx, principal, func(account *core.Account) error {
		org, ok := account.Organizations[principal.OrganizationID]
		if !ok || org.OwnerUserID != principal.UserID || org.Archived || org.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		if org.Billing.SubscriptionID == "" {
			return ErrCheckoutUnavailable
		}
		org.PlanID = target.ID
		org.Billing.PlanKey = planKey
		org.Billing.Interval = interval
		org.Billing.Status = "active"
		org.Billing.ScheduledPlanKey = ""
		org.Billing.ScheduledInterval = ""
		org.Billing.ScheduledAt = time.Time{}
		org.Billing.PendingPlanKey = ""
		org.Billing.PendingInterval = ""
		account.Organizations[org.ID] = org
		return nil
	})
}

func (service BillingService) availablePlans(ctx context.Context) ([]core.Plan, error) {
	plans, err := service.store.Plans(ctx)
	if err != nil {
		return nil, err
	}
	plans = slices.DeleteFunc(plans, func(plan core.Plan) bool {
		return isInternalPlan(plan) || !plan.Visible || plan.PriceCents <= 0 || normalizePlanKey(plan.Key) == ""
	})
	slices.SortFunc(plans, func(left, right core.Plan) int {
		return cmp.Or(cmp.Compare(left.PriceCents, right.PriceCents), strings.Compare(normalizePlanKey(left.Key), normalizePlanKey(right.Key)))
	})
	return plans, nil
}

// BeginDemoCheckout creates a one-use, tenant-owned local checkout. It stores
// only a plan selection and safe return path; the demo never collects card
// data and never calls Stripe.
func (service BillingService) BeginDemoCheckout(ctx context.Context, principal core.Principal, planKey, interval, returnPath string) (core.CheckoutSession, error) {
	planID, planKey, err := service.planCandidate(ctx, planKey, interval)
	if err != nil {
		return core.CheckoutSession{}, err
	}
	returnPath, err = payments.NormalizeReturnPath(returnPath, "/organization?tab=billing")
	if err != nil {
		return core.CheckoutSession{}, err
	}
	now := service.now()
	session := core.CheckoutSession{
		ID: core.NewID(), OwnerUserID: principal.UserID,
		OrganizationID: principal.OrganizationID, Interval: interval,
		ReturnPath: returnPath, Status: checkoutOpen, Demo: true, CreatedAt: now,
		ExpiresAt: now.Add(30 * time.Minute),
	}
	err = service.store.UpdateAsWithPlan(ctx, principal, planID, func(account *core.Account, plan core.Plan) error {
		if err := validatePlanSelection(plan, planKey, interval); err != nil {
			return err
		}
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Billing.Internal || organization.Status != "active" || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		if organization.Billing.SubscriptionID != "" && organization.Billing.Status != "canceled" && organization.Billing.Status != "incomplete_expired" {
			return payments.ErrSubscriptionManaged
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

// CheckoutPrincipal scopes a browser principal to the organization that owns
// the checkout session. MCP tools start checkouts in any organization the
// owner names, not only the one selected in the dashboard.
func (service BillingService) CheckoutPrincipal(ctx context.Context, principal core.Principal, sessionID string) (core.Principal, error) {
	account, err := service.store.SnapshotAs(ctx, principal)
	if err != nil {
		return core.Principal{}, err
	}
	for id, organization := range account.Organizations {
		if _, ok := organization.CheckoutSessions[sessionID]; ok && id != principal.OrganizationID {
			return service.store.ScopedPrincipal(ctx, principal, core.Scope{OrganizationID: id})
		}
	}
	return principal, nil
}

// Checkout returns only the principal organization's own open checkout, so a
// session ID cannot cross tenant boundaries.
func (service BillingService) Checkout(ctx context.Context, principal core.Principal, sessionID string) (core.CheckoutSession, error) {
	if sessionID == "" {
		return core.CheckoutSession{}, core.ErrNotFound
	}
	account, err := service.store.SnapshotAs(ctx, principal)
	if err != nil {
		return core.CheckoutSession{}, err
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

// CompleteDemoCheckout consumes the current demo session exactly once. The
// session is looked up and the organization is updated in one scoped commit.
func (service BillingService) CompleteDemoCheckout(ctx context.Context, principal core.Principal, sessionID string, canceled bool) (string, error) {
	pending, err := service.Checkout(ctx, principal, sessionID)
	if errors.Is(err, core.ErrUnavailable) {
		return "", err
	}
	if err != nil {
		return "", ErrCheckoutUnavailable
	}
	var returnTo string
	mutate := func(account *core.Account, plan core.Plan) error {
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Billing.Internal || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		session, ok := organization.CheckoutSessions[sessionID]
		if !ok || session.ID != sessionID || session.OrganizationID != organization.ID || session.OwnerUserID != principal.UserID || session.Status != checkoutOpen || !session.ExpiresAt.After(service.now()) || organization.Billing.CheckoutID != sessionID {
			return ErrCheckoutUnavailable
		}
		path, err := payments.NormalizeReturnPath(session.ReturnPath, "/organization?tab=billing")
		if err != nil {
			return err
		}
		if canceled {
			session.Status = checkoutCancel
			organization.Billing.CheckoutID = ""
			organization.Billing.ReturnPath = ""
		} else {
			if isInternalPlan(plan) || plan.ID != session.PlanID || plan.PriceCents <= 0 || (session.Interval == intervalAnnual && plan.AnnualPriceCents <= 0) {
				return ErrPlanUnavailable
			}
			session.Status = checkoutDone
			organization.PlanID = plan.ID
			organization.Billing.SubscriptionID = core.NewID()
			organization.Billing.TrialUsed = true
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
		returnTo = payments.WithPaymentOutcome(path, map[bool]string{true: "canceled", false: "success"}[canceled])
		return nil
	}
	if canceled {
		err = service.store.UpdateAs(ctx, principal, func(account *core.Account) error { return mutate(account, core.Plan{}) })
	} else {
		err = service.store.UpdateAsWithPlan(ctx, principal, pending.PlanID, mutate)
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
func (service BillingService) RecordCustomer(ctx context.Context, principal core.Principal, createdID string) (string, error) {
	if strings.TrimSpace(createdID) == "" {
		return "", core.ErrInvalid
	}
	var customerID string
	err := service.store.UpdateAs(ctx, principal, func(account *core.Account) error {
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Billing.Internal || organization.Archived || organization.DeletionRequested {
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
func (service BillingService) RecordExternalCheckout(ctx context.Context, principal core.Principal, planKey, interval, returnPath string, providerSession payments.CheckoutSession) error {
	planID, planKey, err := service.planCandidate(ctx, planKey, interval)
	if err != nil {
		return err
	}
	returnPath, err = payments.NormalizeReturnPath(returnPath, "/organization?tab=billing")
	if err != nil {
		return err
	}
	now := service.now()
	if providerSession.ID == "" || !providerSession.ExpiresAt.After(now) {
		return core.ErrInvalid
	}
	localSession := core.CheckoutSession{
		ID: providerSession.ID, OwnerUserID: principal.UserID, OrganizationID: principal.OrganizationID,
		Interval: interval, ReturnPath: returnPath, Status: checkoutOpen,
		CreatedAt: now, ExpiresAt: providerSession.ExpiresAt,
	}
	err = service.store.UpdateAsWithPlan(ctx, principal, planID, func(account *core.Account, plan core.Plan) error {
		if err := validatePlanSelection(plan, planKey, interval); err != nil {
			return err
		}
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Billing.Internal || organization.Status != "active" || organization.Archived || organization.DeletionRequested {
			return ErrBillingOwnerRequired
		}
		if organization.Billing.CustomerID == "" {
			return core.ErrConflict
		}
		if organization.Billing.SubscriptionID != "" && organization.Billing.Status != "canceled" && organization.Billing.Status != "incomplete_expired" {
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
func (service BillingService) ExpireExternalCheckout(ctx context.Context, principal core.Principal, sessionID string) error {
	return service.store.UpdateAs(ctx, principal, func(account *core.Account) error {
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok || organization.OwnerUserID != principal.UserID || organization.Billing.Internal || organization.Archived || organization.DeletionRequested {
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

// CheckoutOutcome reports the return path and payment outcome of the owner's
// checkout once its customer has been synced. Sessions are looked up only in
// the current organization, so a session ID cannot reveal another tenant.
func (service BillingService) CheckoutOutcome(ctx context.Context, principal core.Principal, sessionID string) (string, string, error) {
	account, err := service.store.SnapshotAs(ctx, principal)
	if err != nil {
		return "", "", err
	}
	organization, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil {
		return "", "", err
	}
	session, ok := organization.CheckoutSessions[sessionID]
	if !ok || session.OwnerUserID != principal.UserID || session.OrganizationID != organization.ID {
		return "", "", core.ErrNotFound
	}
	switch session.Status {
	case checkoutDone:
		return session.ReturnPath, "success", nil
	case checkoutCancel:
		return session.ReturnPath, "canceled", nil
	default:
		return session.ReturnPath, "processing", nil
	}
}

// PendingCheckoutID returns the organization's open Checkout Session, which
// the Stripe sync reads so the projection can complete or release it.
func (service BillingService) PendingCheckoutID(ctx context.Context, organizationID string) (string, error) {
	organization, err := service.store.Organization(ctx, organizationID)
	if err != nil {
		return "", err
	}
	return organization.Billing.CheckoutID, nil
}

// ProjectCustomerState applies a customer's synced Stripe state to its
// organization. Every billing field is derived from the state alone, so
// applying the same state twice is harmless. Concurrent syncs save in order
// but can project out of order; a stale revision asks the sync to retry.
func (service BillingService) ProjectCustomerState(ctx context.Context, state payments.CustomerState, checkout *payments.CheckoutSession) error {
	return service.store.UpdateOrganization(ctx, state.OrganizationID, func(organization *core.Organization, plans map[string]core.Plan) error {
		if organization.Billing.Internal {
			return core.ErrForbidden
		}
		switch organization.Billing.CustomerID {
		case state.CustomerID:
		case "":
			organization.Billing.CustomerID = state.CustomerID
		default:
			return core.ErrConflict
		}
		if state.Revision == 0 {
			return payments.ErrInvalidBillingInput
		}
		if state.Revision < organization.Billing.Revision {
			return payments.ErrStaleState
		}
		organization.Billing.Revision = state.Revision
		organization.Billing.SyncedAt = state.SyncedAt
		if err := projectSubscription(organization, state, plans); err != nil {
			return err
		}
		projectChanges(organization, state)
		projectCheckout(organization, state, checkout)
		return nil
	})
}

func projectSubscription(organization *core.Organization, state payments.CustomerState, plans map[string]core.Plan) error {
	billing := &organization.Billing
	entitled := payments.IsEntitledStatus(state.Status)
	billing.SubscriptionID = state.SubscriptionID
	billing.Status = state.Status
	billing.CancelAtPeriodEnd = state.CancelAtPeriodEnd
	billing.TrialUsed = billing.TrialUsed || state.TrialUsed || entitled
	billing.Invoices = state.Invoices
	billing.PaymentMethodBrand, billing.PaymentMethodLastFour, billing.PaymentMethodExpiry = "", "", ""
	if method := state.PaymentMethod; method != nil {
		billing.PaymentMethodBrand = method.Brand
		billing.PaymentMethodLastFour = method.Last4
		billing.PaymentMethodExpiry = fmt.Sprintf("%02d/%02d", method.ExpMonth, method.ExpYear%100)
	}
	switch {
	case entitled && state.PlanKey == "":
		billing.Status = "unmapped_price"
		organization.BillingStatus = "billing_required"
	case entitled:
		plan, ok := plans[state.PlanKey]
		if !ok || isInternalPlan(plan) {
			return fmt.Errorf("%w: subscription plan %q is unavailable", ErrPlanUnavailable, state.PlanKey)
		}
		organization.PlanID = plan.ID
		billing.PlanKey = state.PlanKey
		billing.Interval = state.Interval
		organization.BillingStatus = "active"
		organization.PaymentMethodValidated = true
		organization.BillingRenewal = state.CurrentPeriodEnd
	case organization.Kind == "personal" && !payments.IsDunningStatus(state.Status):
		// Personal workspaces fall back to Free when no subscription grants
		// the plan, including while a first payment is pending or after it ends.
		organization.PlanID = "3BN8V1JQWTB3R3NSZ26SERTHWT"
		billing.PlanKey = "free"
		billing.Interval = ""
		organization.BillingStatus = "active"
		organization.PaymentMethodValidated = false
		organization.BillingRenewal = time.Time{}
	case organization.Kind == "personal":
		// Keep the current plan while Stripe retries payment, but surface the
		// account as requiring billing attention.
		organization.BillingStatus = "billing_required"
	default:
		organization.BillingStatus = "billing_required"
	}
	return nil
}

// projectChanges mirrors the scheduled and pending plan changes Stripe holds.
// ChangeVersion advances whenever they change, which gives each plan change
// its own Stripe idempotency keys while keeping retries of one change stable.
func projectChanges(organization *core.Organization, state payments.CustomerState) {
	billing := &organization.Billing
	before := [...]string{billing.ScheduledPlanKey, billing.ScheduledInterval, billing.ScheduleID, billing.PendingPlanKey, billing.PendingInterval}
	switch {
	case state.ScheduledPlanKey != "":
		billing.ScheduledPlanKey, billing.ScheduledInterval = state.ScheduledPlanKey, state.ScheduledInterval
		billing.ScheduledAt, billing.ScheduleID = state.CurrentPeriodEnd, state.ScheduleID
	case state.CancelAtPeriodEnd && organization.Kind == "personal" && payments.IsEntitledStatus(state.Status):
		billing.ScheduledPlanKey, billing.ScheduledInterval = "free", ""
		billing.ScheduledAt, billing.ScheduleID = state.CurrentPeriodEnd, ""
	default:
		clearScheduledChange(billing)
	}
	billing.PendingPlanKey, billing.PendingInterval = state.PendingPlanKey, state.PendingInterval
	after := [...]string{billing.ScheduledPlanKey, billing.ScheduledInterval, billing.ScheduleID, billing.PendingPlanKey, billing.PendingInterval}
	if before != after {
		billing.ChangeVersion++
	}
}

// projectCheckout completes the pending checkout once Stripe reports it
// complete and its subscription entitled, and releases it once expired.
func projectCheckout(organization *core.Organization, state payments.CustomerState, checkout *payments.CheckoutSession) {
	checkoutID := organization.Billing.CheckoutID
	if checkoutID == "" || checkout == nil || checkout.ID != checkoutID {
		return
	}
	session, ok := organization.CheckoutSessions[checkoutID]
	if !ok || session.Status != checkoutOpen {
		return
	}
	switch {
	case checkout.Status == "complete" && checkout.SubscriptionID != "" && checkout.SubscriptionID == state.SubscriptionID && payments.IsEntitledStatus(state.Status):
		session.Status = checkoutDone
	case checkout.Status == "expired":
		session.Status = checkoutCancel
	default:
		return
	}
	organization.CheckoutSessions[checkoutID] = session
	organization.Billing.CheckoutID = ""
	organization.Billing.ReturnPath = ""
}

func clearScheduledChange(billing *core.Billing) {
	billing.ScheduledPlanKey = ""
	billing.ScheduledInterval = ""
	billing.ScheduledAt = time.Time{}
	billing.ScheduleID = ""
}

func (service BillingService) selectedPlan(ctx context.Context, planKey, interval string) (core.Plan, error) {
	planID, planKey, err := service.planCandidate(ctx, planKey, interval)
	if err != nil {
		return core.Plan{}, err
	}
	plan, err := service.store.Plan(ctx, planID)
	if errors.Is(err, core.ErrNotFound) {
		return core.Plan{}, ErrPlanUnavailable
	}
	if err != nil {
		return core.Plan{}, err
	}
	if err := validatePlanSelection(plan, planKey, interval); err != nil {
		return core.Plan{}, err
	}
	return plan, nil
}

func (service BillingService) planCandidate(ctx context.Context, planKey, interval string) (string, string, error) {
	planKey = normalizePlanKey(planKey)
	if planKey == "" {
		return "", "", ErrPlanUnavailable
	}
	if interval != intervalMonthly && interval != intervalAnnual {
		return "", "", core.ErrInvalid
	}
	plan, err := service.store.Plan(ctx, planKey)
	if errors.Is(err, core.ErrNotFound) {
		return "", "", ErrPlanUnavailable
	}
	if err != nil {
		return "", "", err
	}
	if isInternalPlan(plan) {
		return "", "", ErrPlanUnavailable
	}
	return plan.ID, planKey, nil
}

func validatePlanSelection(plan core.Plan, planKey, interval string) error {
	if isInternalPlan(plan) || plan.ID == "" || normalizePlanKey(plan.Key) != planKey || !plan.Visible || plan.PriceCents <= 0 {
		return ErrPlanUnavailable
	}
	if interval == intervalAnnual && plan.AnnualPriceCents <= 0 {
		return ErrPlanUnavailable
	}
	return nil
}

func isInternalPlan(plan core.Plan) bool {
	return normalizePlanKey(plan.Key) == "internal"
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
