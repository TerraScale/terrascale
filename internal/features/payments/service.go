package payments

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// ErrSubscriptionManaged means the organization already pays through a live
// subscription, which changes through plan changes or the billing portal.
var ErrSubscriptionManaged = fmt.Errorf("%w: the organization already has a subscription; change plans or open the billing portal instead", core.ErrConflict)

// Plan change outcomes.
const (
	OutcomeCurrent   = "current"
	OutcomeChanged   = "changed"
	OutcomeScheduled = "scheduled"
	OutcomePending   = "pending"
	OutcomeUndone    = "undone"
)

// unavailableError is a billing operation this server is not configured to
// perform.
type unavailableError string

func (e unavailableError) Error() string { return string(e) }

// providerError is a Stripe request that failed or returned an unusable answer.
type providerError string

func (e providerError) Error() string { return string(e) }

func conflict(message string) error {
	return fmt.Errorf("%w: %s", core.ErrConflict, message)
}

const (
	errNotConfigured       = unavailableError("Billing is not configured.")
	errPlanChangesDisabled = unavailableError("Plan changes are not available yet.")
)

// Service runs checkout, plan changes, and portal sessions for the
// principal's organization. HTTP handlers and MCP tools both call it; only
// the organization owner may change billing. Live changes are applied in
// Stripe and then synced into the organization like any other change.
type Service struct {
	store   core.Store
	demo    bool
	flow    BillingFlow
	billing *Stripe
}

// NewService returns the billing service. billing is nil in demo mode.
func NewService(deps core.Deps, flow BillingFlow, billing *Stripe) Service {
	return Service{store: deps.Store, demo: deps.Demo, flow: flow, billing: billing}
}

func (s Service) configured() error {
	if s.billing == nil {
		return errNotConfigured
	}
	return nil
}

// owner returns the principal's organization when the principal owns it and
// it is not archived or pending deletion.
func (s Service) owner(ctx context.Context, p core.Principal) (core.Account, core.Organization, error) {
	account, err := s.store.SnapshotAs(ctx, p)
	if err != nil {
		return core.Account{}, core.Organization{}, err
	}
	organization, err := account.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		return core.Account{}, core.Organization{}, err
	}
	if organization.OwnerUserID != p.UserID || organization.Archived || organization.DeletionRequested {
		return core.Account{}, core.Organization{}, fmt.Errorf("%w: only the organization owner can change billing", core.ErrForbidden)
	}
	return account, organization, nil
}

// activeOwner is owner for an organization that must also be active.
func (s Service) activeOwner(ctx context.Context, p core.Principal) (core.Account, core.Organization, error) {
	account, organization, err := s.owner(ctx, p)
	if err == nil && organization.Status != "active" {
		err = fmt.Errorf("%w: the organization is not active", core.ErrForbidden)
	}
	return account, organization, err
}

// demoChanges is the in-memory simulator's plan-change flow.
func (s Service) demoChanges() (planChangeFlow, error) {
	changes, ok := s.flow.(planChangeFlow)
	if !ok {
		return nil, errPlanChangesDisabled
	}
	return changes, nil
}

// StartCheckout resumes the organization's open Stripe Checkout when it
// matches the requested plan, and otherwise creates one bound to the tenant.
// It returns the URL the owner opens in a browser to pay; relative URLs are
// dashboard pages.
func (s Service) StartCheckout(ctx context.Context, p core.Principal, planKey, interval, returnPath string) (string, error) {
	if s.demo {
		session, err := s.flow.BeginDemoCheckout(ctx, p, planKey, interval, returnPath)
		if err != nil {
			return "", err
		}
		return "/checkout/" + url.PathEscape(session.ID), nil
	}
	if err := s.configured(); err != nil {
		return "", err
	}
	account, organization, err := s.activeOwner(ctx, p)
	if err != nil {
		return "", err
	}
	if organization.Billing.SubscriptionID != "" && organization.Billing.Status != "canceled" && organization.Billing.Status != "incomplete_expired" {
		return "", ErrSubscriptionManaged
	}
	if organization.Billing.SubscriptionID == "" && organization.BillingStatus == "active" && organization.PlanID != "plan_free" {
		return "", conflict("an existing active plan must be managed through its billing account")
	}
	plan, err := selectedPlan(ctx, s.store, planKey, interval)
	if err != nil {
		return "", err
	}
	if organization.Billing.CheckoutID != "" {
		location, err := s.resumeCheckout(ctx, p, organization, plan, interval)
		if err != nil || location != "" {
			return location, err
		}
	}
	returnPath, err = NormalizeReturnPath(returnPath, "/organization?tab=billing")
	if err != nil {
		return "", err
	}
	customerID, err := s.billing.customerFor(ctx, account.User.Email, organization)
	if err != nil {
		slog.Error("bind Stripe customer", "organization_id", organization.ID, "error", err)
		return "", providerError("Could not start checkout.")
	}
	recorded, err := s.flow.RecordCustomer(ctx, p, customerID)
	if err != nil {
		return "", err
	}
	if recorded != customerID {
		slog.Error("organization records a different Stripe customer than its binding", "organization_id", organization.ID)
		return "", conflict("this organization's billing account needs attention")
	}
	trialDays := 0
	if !organization.Billing.TrialUsed {
		trialDays = plan.TrialDays
	}
	origin := s.billing.config.PublicOrigin
	created, err := s.billing.provider.CreateCheckoutSession(ctx, CheckoutRequest{
		CustomerID: customerID, OrganizationID: organization.ID, OrganizationName: organization.Name,
		PlanKey: plan.Key, Interval: interval, TrialDays: trialDays,
		SuccessURL:     origin + "/billing/checkout/return?session_id={CHECKOUT_SESSION_ID}",
		CancelURL:      origin + WithPaymentOutcome(returnPath, "canceled"),
		ExpiresAt:      time.Now().Add(checkoutLifetime),
		IdempotencyKey: core.NewID("checkout"),
	})
	if err != nil {
		slog.Error("create Stripe checkout session", "organization_id", organization.ID, "error", err)
		return "", providerError("Could not start checkout.")
	}
	if _, err := absoluteHTTPURL(created.URL); err != nil {
		s.billing.expireUnboundCheckout(ctx, created.ID)
		return "", providerError("Checkout provider returned an invalid URL.")
	}
	if created.CustomerID != customerID || created.ClientReferenceID != organization.ID {
		s.billing.expireUnboundCheckout(ctx, created.ID)
		return "", providerError("Checkout provider returned an unbound session.")
	}
	// Stripe has opened the session, so it is recorded even when the request
	// is canceled meanwhile; otherwise nothing would track it.
	if err := s.flow.RecordExternalCheckout(context.WithoutCancel(ctx), p, plan.Key, interval, returnPath, created); err != nil {
		s.billing.expireUnboundCheckout(ctx, created.ID)
		return "", err
	}
	return created.URL, nil
}

// resumeCheckout returns where an organization's pending checkout continues.
// It returns an empty location after consuming an expired checkout, so the
// caller starts a new one.
func (s Service) resumeCheckout(ctx context.Context, p core.Principal, organization core.Organization, plan core.Plan, interval string) (string, error) {
	pendingID := organization.Billing.CheckoutID
	pending, exists := organization.CheckoutSessions[pendingID]
	if !exists || pending.ID != pendingID || pending.OwnerUserID != p.UserID || pending.OrganizationID != organization.ID || pending.Status != "open" {
		return "", conflict("a checkout is already in progress for this organization")
	}
	pendingPlan, err := s.store.Plan(ctx, pending.PlanID)
	if err != nil && !errors.Is(err, core.ErrNotFound) {
		return "", err
	}
	if err != nil || NormalizePlanKey(pendingPlan.Key) != NormalizePlanKey(plan.Key) || pending.Interval != interval {
		return "", conflict("finish or cancel the current checkout before choosing another plan")
	}
	remote, err := s.billing.provider.RetrieveCheckoutSession(ctx, pendingID)
	if err != nil {
		slog.Error("retrieve pending Stripe checkout", "session_id", pendingID, "error", err)
		return "", providerError("Could not verify the existing checkout.")
	}
	if remote.ID != pendingID || remote.CustomerID != organization.Billing.CustomerID || remote.ClientReferenceID != organization.ID {
		return "", providerError("Existing checkout is not bound to this organization.")
	}
	switch remote.Status {
	case "open":
		if _, err := absoluteHTTPURL(remote.URL); err != nil {
			return "", providerError("Checkout provider returned an invalid URL.")
		}
		return remote.URL, nil
	case "complete":
		return "/billing/checkout/return?session_id=" + url.QueryEscape(pendingID), nil
	case "expired":
		return "", s.flow.ExpireExternalCheckout(ctx, p, pendingID)
	default:
		return "", conflict("the existing checkout has an unsupported state")
	}
}

// CancelCheckout abandons the organization's checkout in progress.
func (s Service) CancelCheckout(ctx context.Context, p core.Principal) error {
	_, organization, err := s.owner(ctx, p)
	if err != nil {
		return err
	}
	sessionID := organization.Billing.CheckoutID
	if sessionID == "" {
		return fmt.Errorf("%w: no checkout is in progress", core.ErrNotFound)
	}
	if s.demo {
		_, err := s.flow.CompleteDemoCheckout(ctx, p, sessionID, true)
		return err
	}
	if err := s.configured(); err != nil {
		return err
	}
	if err := s.billing.provider.ExpireCheckoutSession(ctx, sessionID); err != nil {
		slog.Error("expire Stripe checkout session", "session_id", sessionID, "error", err)
		return providerError("Could not cancel the checkout.")
	}
	return s.flow.ExpireExternalCheckout(context.WithoutCancel(ctx), p, sessionID)
}

// ReturnFromCheckout syncs the organization after its owner returns from
// Stripe Checkout and returns the dashboard page to show with the payment
// outcome.
func (s Service) ReturnFromCheckout(ctx context.Context, p core.Principal, sessionID string) (string, error) {
	sessionID = strings.TrimSpace(sessionID)
	if sessionID == "" {
		return "", core.Invalid("checkout session is missing")
	}
	if s.demo {
		return "/checkout/" + url.PathEscape(sessionID), nil
	}
	if err := s.configured(); err != nil {
		return "", err
	}
	// MCP starts checkouts in any organization the owner names, so the
	// return acts on the checkout's organization, not the selected one.
	p, err := s.flow.CheckoutPrincipal(ctx, p, sessionID)
	if errors.Is(err, core.ErrUnavailable) {
		return "", err
	}
	if err != nil {
		return "", core.ErrNotFound
	}
	// The owner usually returns before Stripe's webhooks arrive, so sync
	// eagerly. The session ID only selects the return path; the state always
	// comes from the organization's own Stripe customer.
	customerID, bound, err := s.billing.store.CustomerFor(ctx, p.OrganizationID)
	if err != nil {
		slog.Error("look up Stripe customer after checkout", "organization_id", p.OrganizationID, "error", err)
		return "", providerError("Could not verify checkout status.")
	}
	if !bound {
		return "", core.ErrNotFound
	}
	if _, err := s.billing.SyncCustomer(ctx, s.flow, customerID); err != nil {
		if errors.Is(err, core.ErrUnavailable) {
			return "", err
		}
		slog.Error("sync Stripe customer after checkout", "organization_id", p.OrganizationID, "error", err)
		return "", providerError("Could not verify checkout status.")
	}
	returnPath, outcome, err := s.flow.CheckoutOutcome(ctx, p, sessionID)
	if err != nil {
		return "", err
	}
	return WithPaymentOutcome(returnPath, outcome), nil
}

// PortalURL opens a billing portal session where the owner manages payment
// methods and invoices. Relative URLs are dashboard pages.
func (s Service) PortalURL(ctx context.Context, p core.Principal) (string, error) {
	if !s.demo {
		if err := s.configured(); err != nil {
			return "", err
		}
	}
	_, organization, err := s.owner(ctx, p)
	if err != nil {
		return "", err
	}
	if s.demo {
		return "/billing/portal/local", nil
	}
	if organization.Billing.CustomerID == "" {
		return "", conflict("no billing account is available for this organization")
	}
	portal, err := s.billing.provider.CreatePortalSession(ctx, organization.Billing.CustomerID, s.billing.config.PublicOrigin+"/organization?tab=billing", core.NewID("portal"))
	if err != nil {
		slog.Error("create Stripe billing portal", "organization_id", organization.ID, "error", err)
		return "", providerError("Could not open the billing portal.")
	}
	if _, err := absoluteHTTPURL(portal.URL); err != nil {
		return "", providerError("Billing provider returned an invalid URL.")
	}
	return portal.URL, nil
}

// planChange validates a requested plan for an owned organization with a
// subscription. The free plan has no interval and is Personal only.
func (s Service) planChange(ctx context.Context, p core.Principal, key, interval string) (core.Organization, string, string, error) {
	_, org, err := s.activeOwner(ctx, p)
	if err != nil {
		return core.Organization{}, "", "", err
	}
	key = strings.ToLower(strings.TrimSpace(key))
	interval = strings.ToLower(strings.TrimSpace(interval))
	if key == "free" {
		if org.Kind != "personal" {
			return core.Organization{}, "", "", core.Invalid("Free is available only for Personal workspaces")
		}
		interval = ""
	} else if _, err := selectedPlan(ctx, s.store, key, interval); err != nil {
		return core.Organization{}, "", "", err
	}
	if org.Billing.SubscriptionID == "" {
		return core.Organization{}, "", "", conflict("choose a paid plan through checkout first")
	}
	return org, key, interval, nil
}

// ChangePlan upgrades immediately or schedules a downgrade for renewal and
// reports which happened. Repeating a change already recorded reports it
// again without calling Stripe.
func (s Service) ChangePlan(ctx context.Context, p core.Principal, key, interval string) (string, error) {
	org, key, interval, err := s.planChange(ctx, p, key, interval)
	if err != nil {
		return "", err
	}
	if org.Billing.PlanKey == key && org.Billing.Interval == interval && org.Billing.ScheduledPlanKey == "" {
		return OutcomeCurrent, nil
	}
	if org.Billing.ScheduledPlanKey != "" {
		if org.Billing.ScheduledPlanKey == key && org.Billing.ScheduledInterval == interval {
			return OutcomeScheduled, nil
		}
		return "", conflict("undo the scheduled change before choosing another plan")
	}
	if org.Billing.PendingPlanKey != "" {
		if org.Billing.PendingPlanKey == key && org.Billing.PendingInterval == interval {
			return OutcomePending, nil
		}
		return "", conflict("wait for the pending payment before changing plans again")
	}
	if s.demo {
		changes, err := s.demoChanges()
		if err != nil {
			return "", err
		}
		if PlanRank(key) > PlanRank(org.Billing.PlanKey) {
			return OutcomeChanged, changes.ChangeDemoPlan(ctx, p, key, interval)
		}
		return OutcomeScheduled, changes.ScheduleDemoChange(ctx, p, key, interval, org.BillingRenewal)
	}
	current, err := s.currentSubscription(ctx, org)
	if err != nil {
		return "", err
	}
	if current.PriceID == "" || current.CurrentPeriodEnd.IsZero() {
		return "", providerError("Could not verify the current subscription.")
	}
	config, changer := s.billing.config, s.billing.provider
	currentKey, currentInterval, mapped := config.planFromPriceID(current.PriceID)
	if !mapped || !IsEntitledStatus(current.Status) {
		return "", conflict("the current subscription needs billing attention")
	}
	if key == currentKey && interval == currentInterval {
		return OutcomePending, nil
	}
	idempotencyKey := fmt.Sprintf("plan:%s:%s:%s:%d:%d", org.ID, key, interval, current.CurrentPeriodEnd.Unix(), org.Billing.ChangeVersion)
	if key == "free" {
		if current.ScheduleID != "" || current.CancelAtPeriodEnd {
			return "", conflict("a subscription change is already pending")
		}
		if err := changer.SetCancelAtPeriodEnd(ctx, current.ID, true, idempotencyKey); err != nil {
			slog.Error("schedule Personal Free", "organization_id", org.ID, "error", err)
			return "", providerError("Could not schedule Free.")
		}
		return OutcomeScheduled, s.syncAfterChange(ctx, org)
	}
	nextPriceID, configured := config.PriceID(key, interval)
	if !configured {
		return "", unavailableError("Selected Stripe price is not configured.")
	}
	if PlanRank(key) > PlanRank(currentKey) {
		if current.ScheduleID != "" || current.CancelAtPeriodEnd {
			return "", conflict("undo the scheduled change first")
		}
		if err := changer.ChangePlanNow(ctx, current, nextPriceID, idempotencyKey); err != nil {
			slog.Error("change Stripe subscription", "organization_id", org.ID, "error", err)
			return "", providerError("Could not confirm the plan change.")
		}
		return OutcomePending, s.syncAfterChange(ctx, org)
	}
	if current.CancelAtPeriodEnd {
		return "", conflict("undo the scheduled cancellation first")
	}
	if _, err := changer.SchedulePlanChange(ctx, current, nextPriceID, idempotencyKey); err != nil {
		slog.Error("schedule Stripe plan change", "organization_id", org.ID, "error", err)
		return "", providerError("Could not schedule the plan change.")
	}
	return OutcomeScheduled, s.syncAfterChange(ctx, org)
}

// UndoPlanChange cancels a scheduled downgrade. It reports OutcomeCurrent
// when nothing is scheduled.
func (s Service) UndoPlanChange(ctx context.Context, p core.Principal) (string, error) {
	_, org, err := s.activeOwner(ctx, p)
	if err != nil {
		return "", err
	}
	if org.Billing.ScheduledPlanKey == "" || org.Billing.SubscriptionID == "" {
		return OutcomeCurrent, nil
	}
	if s.demo {
		changes, err := s.demoChanges()
		if err != nil {
			return "", err
		}
		return OutcomeUndone, changes.UndoDemoChange(ctx, p)
	}
	current, err := s.currentSubscription(ctx, org)
	if err != nil {
		return "", err
	}
	config, changer := s.billing.config, s.billing.provider
	key := fmt.Sprintf("undo:%s:%s:%d:%d", org.ID, org.Billing.ScheduledPlanKey, org.Billing.ScheduledAt.Unix(), org.Billing.ChangeVersion)
	currentKey, currentInterval, mapped := config.planFromPriceID(current.PriceID)
	alreadyUndone := mapped && IsEntitledStatus(current.Status) && currentKey == org.Billing.PlanKey && currentInterval == org.Billing.Interval
	if org.Billing.ScheduledPlanKey == "free" {
		if !current.CancelAtPeriodEnd && !alreadyUndone {
			return "", conflict("the scheduled cancellation changed in Stripe")
		}
		if current.CancelAtPeriodEnd {
			err = changer.SetCancelAtPeriodEnd(ctx, current.ID, false, key)
		}
	} else {
		if org.Billing.ScheduleID == "" || current.ScheduleID != org.Billing.ScheduleID && (current.ScheduleID != "" || !alreadyUndone) {
			return "", conflict("the scheduled plan changed in Stripe")
		}
		if current.ScheduleID != "" {
			err = changer.ReleaseSchedule(ctx, current.ScheduleID, key)
		}
	}
	if err != nil {
		slog.Error("undo Stripe plan change", "organization_id", org.ID, "error", err)
		return "", providerError("Could not undo the scheduled change.")
	}
	return OutcomeUndone, s.syncAfterChange(ctx, org)
}

// PlanChangePreview estimates what a plan change costs and when it applies.
type PlanChangePreview struct {
	PlanKey        string    `json:"planKey"`
	Interval       string    `json:"interval,omitempty"`
	Immediate      bool      `json:"immediate" jsonschema:"Whether the change applies now (upgrade) instead of at renewal (downgrade)."`
	EffectiveAt    time.Time `json:"effectiveAt"`
	AmountDueCents int64     `json:"amountDueCents" jsonschema:"Estimated charge today in the smallest currency unit."`
	ProrationCents int64     `json:"prorationCents"`
	Currency       string    `json:"currency,omitempty"`
	Note           string    `json:"note"`
}

// PreviewPlanChange estimates a plan change without changing billing.
func (s Service) PreviewPlanChange(ctx context.Context, p core.Principal, key, interval string) (PlanChangePreview, error) {
	org, key, interval, err := s.planChange(ctx, p, key, interval)
	if err != nil {
		return PlanChangePreview{}, err
	}
	if org.Billing.PlanKey == key && org.Billing.Interval == interval {
		return PlanChangePreview{}, conflict("this is already the current plan")
	}
	preview := PlanChangePreview{PlanKey: key, Interval: interval, Immediate: PlanRank(key) > PlanRank(org.Billing.PlanKey)}
	switch {
	case !preview.Immediate:
		preview.EffectiveAt, preview.Note = org.BillingRenewal, "No charge today; the change applies at renewal."
	case s.demo:
		preview.EffectiveAt, preview.Note = time.Now().UTC(), "Demo only; no Stripe charge."
	default:
		quote, err := s.billing.PreviewPlanChange(ctx, org.Billing.CustomerID, org.Billing.SubscriptionID, key, interval)
		if errors.Is(err, ErrStripeUnavailable) {
			return PlanChangePreview{}, errPlanChangesDisabled
		}
		if err != nil {
			slog.Error("preview Stripe plan change", "organization_id", org.ID, "error", err)
			return PlanChangePreview{}, providerError("Stripe could not calculate the estimated charge.")
		}
		preview.EffectiveAt, preview.AmountDueCents, preview.ProrationCents, preview.Currency = quote.EffectiveAt, quote.AmountDueCents, quote.ProrationCents, quote.Currency
		preview.Note = "Estimated by Stripe; the change applies after Stripe confirms payment."
	}
	return preview, nil
}

// currentSubscription verifies the organization's Stripe subscription before
// a plan change.
func (s Service) currentSubscription(ctx context.Context, org core.Organization) (SubscriptionSnapshot, error) {
	if s.billing == nil || !s.billing.config.PlanChangesEnabled() {
		return SubscriptionSnapshot{}, errPlanChangesDisabled
	}
	current, err := s.billing.provider.RetrieveSubscription(ctx, org.Billing.SubscriptionID)
	if err != nil || current.ID != org.Billing.SubscriptionID || current.CustomerID != org.Billing.CustomerID {
		slog.Error("verify subscription before plan change", "subscription_id", org.Billing.SubscriptionID, "organization_id", org.ID, "error", err)
		return SubscriptionSnapshot{}, providerError("Could not verify the current subscription.")
	}
	return current, nil
}

// syncAfterChange projects what Stripe accepted, so the scheduled or pending
// change shown to the owner is Stripe's, not a local guess.
func (s Service) syncAfterChange(ctx context.Context, org core.Organization) error {
	// Stripe has accepted the change, so it is synced even when the request is
	// canceled meanwhile.
	if _, err := s.billing.SyncCustomer(context.WithoutCancel(ctx), s.flow, org.Billing.CustomerID); err != nil {
		if errors.Is(err, core.ErrUnavailable) {
			return err
		}
		slog.Error("sync Stripe customer after plan change", "organization_id", org.ID, "error", err)
		return providerError("Stripe accepted the change, but the dashboard could not refresh it yet. Reload in a moment.")
	}
	return nil
}

// ProviderStatus reports the HTTP status of an error raised because billing is
// not configured (503) or Stripe failed (502).
func ProviderStatus(err error) (int, bool) {
	var unavailable unavailableError
	var provider providerError
	switch {
	case errors.As(err, &unavailable):
		return http.StatusServiceUnavailable, true
	case errors.As(err, &provider):
		return http.StatusBadGateway, true
	}
	return 0, false
}

// billingErrorStatus maps a billing error to its HTTP status and message.
func billingErrorStatus(err error) (int, string) {
	if status, ok := ProviderStatus(err); ok {
		return status, err.Error()
	}
	switch {
	case errors.Is(err, core.ErrUnauthorized):
		return http.StatusUnauthorized, "authentication required"
	case errors.Is(err, core.ErrForbidden):
		return http.StatusForbidden, err.Error()
	case errors.Is(err, core.ErrNotFound):
		return http.StatusNotFound, err.Error()
	case errors.Is(err, core.ErrInvalid):
		return http.StatusBadRequest, err.Error()
	case errors.Is(err, core.ErrUnavailable):
		return http.StatusServiceUnavailable, "service unavailable"
	default:
		return http.StatusConflict, err.Error()
	}
}
