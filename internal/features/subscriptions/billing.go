package subscriptions

import (
	"cmp"
	"errors"
	"fmt"
	"slices"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/payments"
)

var (
	ErrBillingOwnerRequired = errors.New("organization owner role required for billing")
	ErrCheckoutUnavailable  = errors.New("checkout session is unavailable")
	ErrPlanUnavailable      = errors.New("selected billing plan is unavailable")
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

// ScheduleDemoChange records a downgrade the demo applies at renewal. Live
// scheduled changes come from Stripe through ProjectCustomerState.
func (service BillingService) ScheduleDemoChange(principal core.Principal, planKey, interval string, effectiveAt time.Time) error {
	if planKey != "free" {
		if _, err := service.selectedPlan(planKey, interval); err != nil {
			return err
		}
	}
	return service.store.UpdateAs(principal, func(account *core.Account) error {
		org, ok := account.Organizations[principal.OrganizationID]
		if !ok || account.ActiveOrganizationID != org.ID || org.OwnerUserID != principal.UserID || org.Archived || org.DeletionRequested {
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
		if planKey == "free" {
			if org.Kind != "personal" {
				return ErrPlanUnavailable
			}
			interval = ""
		}
		org.Billing.ScheduledPlanKey = planKey
		org.Billing.ScheduledInterval = interval
		org.Billing.ScheduledAt = effectiveAt
		org.Billing.ChangeVersion++
		account.Organizations[org.ID] = org
		return nil
	})
}

// UndoDemoChange clears the demo's scheduled downgrade.
func (service BillingService) UndoDemoChange(principal core.Principal) error {
	return service.store.UpdateAs(principal, func(account *core.Account) error {
		org, ok := account.Organizations[principal.OrganizationID]
		if !ok || account.ActiveOrganizationID != org.ID || org.OwnerUserID != principal.UserID {
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
func (service BillingService) ChangeDemoPlan(principal core.Principal, planKey, interval string) error {
	target, err := service.selectedPlan(planKey, interval)
	if err != nil {
		return err
	}
	return service.store.UpdateAs(principal, func(account *core.Account) error {
		org, ok := account.Organizations[principal.OrganizationID]
		if !ok || account.ActiveOrganizationID != org.ID || org.OwnerUserID != principal.UserID || org.Archived || org.DeletionRequested {
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

func (service BillingService) availablePlans() []core.Plan {
	plans := service.store.Plans()
	plans = slices.DeleteFunc(plans, func(plan core.Plan) bool {
		return !plan.Visible || plan.PriceCents <= 0 || normalizePlanKey(plan.Key) == ""
	})
	slices.SortFunc(plans, func(left, right core.Plan) int {
		return cmp.Or(cmp.Compare(left.PriceCents, right.PriceCents), strings.Compare(normalizePlanKey(left.Key), normalizePlanKey(right.Key)))
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
	returnPath, err = payments.NormalizeReturnPath(returnPath, "/billing")
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
		if organization.Billing.SubscriptionID != "" && organization.Billing.Status != "canceled" && organization.Billing.Status != "incomplete_expired" {
			return core.ErrConflict
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
		path, err := payments.NormalizeReturnPath(session.ReturnPath, "/billing")
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
			organization.Billing.SubscriptionID = core.NewID("sub_local")
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
	returnPath, err = payments.NormalizeReturnPath(returnPath, "/billing")
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

// CheckoutOutcome reports the return path and payment outcome of the owner's
// checkout once its customer has been synced. Sessions are looked up only in
// the current organization, so a session ID cannot reveal another tenant.
func (service BillingService) CheckoutOutcome(principal core.Principal, sessionID string) (string, string, error) {
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
func (service BillingService) PendingCheckoutID(organizationID string) (string, error) {
	organization, ok := service.store.Organization(organizationID)
	if !ok {
		return "", core.ErrNotFound
	}
	return organization.Billing.CheckoutID, nil
}

// ProjectCustomerState applies a customer's synced Stripe state to its
// organization. Every billing field is derived from the state alone, so
// applying the same state twice is harmless. Concurrent syncs save in order
// but can project out of order, so a state read from Stripe before the
// projected one is ignored.
func (service BillingService) ProjectCustomerState(state payments.CustomerState, checkout *payments.CheckoutSession) error {
	return service.store.UpdateOrganization(state.OrganizationID, func(organization *core.Organization, plans map[string]core.Plan) error {
		switch organization.Billing.CustomerID {
		case state.CustomerID:
		case "":
			organization.Billing.CustomerID = state.CustomerID
		default:
			return core.ErrConflict
		}
		if state.SyncedAt.Before(organization.Billing.SyncedAt) {
			return nil
		}
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
		if !ok {
			return fmt.Errorf("%w: subscription plan %q no longer exists", ErrPlanUnavailable, state.PlanKey)
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
		organization.PlanID = "plan_free"
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
