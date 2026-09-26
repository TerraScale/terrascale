package payments

import (
	"fmt"
	"log/slog"
	"net/http"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// planChangeFlow is the demo counterpart of the Stripe plan-change calls.
// Live changes are applied in Stripe and then synced like any other change.
type planChangeFlow interface {
	ChangeDemoPlan(core.Principal, string, string) error
	ScheduleDemoChange(core.Principal, string, string, time.Time) error
	UndoDemoChange(core.Principal) error
}

func mountPlanChanges(mux *http.ServeMux, deps core.Deps, flow BillingFlow, billing *Stripe) {
	changes, ok := flow.(planChangeFlow)
	if !ok {
		return
	}
	mux.HandleFunc("POST /billing/change", func(w http.ResponseWriter, r *http.Request) {
		if !billingForm(w, r) || !validPlanChangeCSRF(deps, w, r) {
			return
		}
		principal, org, ok := planChangeOwner(deps, w, r)
		if !ok {
			return
		}
		key := strings.ToLower(strings.TrimSpace(r.Form.Get("plan")))
		interval := strings.ToLower(strings.TrimSpace(r.Form.Get("interval")))
		if key == "free" {
			if org.Kind != "personal" {
				http.Error(w, "Free is available only for Personal workspaces.", http.StatusBadRequest)
				return
			}
			interval = ""
		} else {
			if _, err := selectedPlan(deps.Store, key, interval); err != nil {
				writeBillingError(w, err)
				return
			}
		}
		if org.Billing.SubscriptionID == "" {
			http.Error(w, "Choose a paid plan through checkout first.", http.StatusConflict)
			return
		}
		if org.Billing.PlanKey == key && org.Billing.Interval == interval && org.Billing.ScheduledPlanKey == "" {
			http.Redirect(w, r, "/usage?tab=billing", http.StatusSeeOther)
			return
		}
		if org.Billing.ScheduledPlanKey != "" {
			if org.Billing.ScheduledPlanKey == key && org.Billing.ScheduledInterval == interval {
				http.Redirect(w, r, "/usage?tab=billing&billing=scheduled", http.StatusSeeOther)
				return
			}
			http.Error(w, "Undo the scheduled change before choosing another plan.", http.StatusConflict)
			return
		}
		if org.Billing.PendingPlanKey != "" {
			if org.Billing.PendingPlanKey == key && org.Billing.PendingInterval == interval {
				http.Redirect(w, r, "/usage?tab=billing&billing=pending", http.StatusSeeOther)
				return
			}
			http.Error(w, "Wait for the pending payment before changing plans again.", http.StatusConflict)
			return
		}
		if deps.Demo {
			if PlanRank(key) > PlanRank(org.Billing.PlanKey) {
				if err := changes.ChangeDemoPlan(principal, key, interval); err != nil {
					writeBillingError(w, err)
					return
				}
				http.Redirect(w, r, "/usage?tab=billing&billing=changed", http.StatusSeeOther)
				return
			}
			if err := changes.ScheduleDemoChange(principal, key, interval, org.BillingRenewal); err != nil {
				writeBillingError(w, err)
				return
			}
			http.Redirect(w, r, "/usage?tab=billing&billing=scheduled", http.StatusSeeOther)
			return
		}
		if billing == nil || !billing.config.PlanChangesEnabled() {
			http.Error(w, "Plan changes are not available yet.", http.StatusServiceUnavailable)
			return
		}
		config, changer := billing.config, billing.provider
		current, err := changer.RetrieveSubscription(r.Context(), org.Billing.SubscriptionID)
		if err != nil || current.ID != org.Billing.SubscriptionID || current.CustomerID != org.Billing.CustomerID || current.PriceID == "" || current.CurrentPeriodEnd.IsZero() {
			slog.Error("verify subscription before plan change", "subscription_id", org.Billing.SubscriptionID, "organization_id", org.ID, "error", err)
			http.Error(w, "Could not verify the current subscription.", http.StatusBadGateway)
			return
		}
		currentKey, currentInterval, mapped := config.planFromPriceID(current.PriceID)
		if !mapped || !IsEntitledStatus(current.Status) {
			http.Error(w, "Current subscription needs billing attention.", http.StatusConflict)
			return
		}
		if key == currentKey && interval == currentInterval {
			http.Redirect(w, r, "/usage?tab=billing&billing=pending", http.StatusSeeOther)
			return
		}
		idempotencyKey := fmt.Sprintf("plan:%s:%s:%s:%d:%d", org.ID, key, interval, current.CurrentPeriodEnd.Unix(), org.Billing.ChangeVersion)
		if key == "free" {
			if current.ScheduleID != "" || current.CancelAtPeriodEnd {
				http.Error(w, "A subscription change is already pending.", http.StatusConflict)
				return
			}
			if err := changer.SetCancelAtPeriodEnd(r.Context(), current.ID, true, idempotencyKey); err != nil {
				slog.Error("schedule Personal Free", "organization_id", org.ID, "error", err)
				http.Error(w, "Could not schedule Free.", http.StatusBadGateway)
				return
			}
			if billing.syncAfterChange(w, r, flow, org) {
				http.Redirect(w, r, "/usage?tab=billing&billing=scheduled", http.StatusSeeOther)
			}
			return
		}
		nextPriceID, configured := config.PriceID(key, interval)
		if !configured {
			http.Error(w, "Selected Stripe price is not configured.", http.StatusServiceUnavailable)
			return
		}
		if PlanRank(key) > PlanRank(currentKey) {
			if current.ScheduleID != "" || current.CancelAtPeriodEnd {
				http.Error(w, "Undo the scheduled change first.", http.StatusConflict)
				return
			}
			if err := changer.ChangePlanNow(r.Context(), current, nextPriceID, idempotencyKey); err != nil {
				slog.Error("change Stripe subscription", "organization_id", org.ID, "error", err)
				http.Error(w, "Could not confirm the plan change.", http.StatusBadGateway)
				return
			}
			if billing.syncAfterChange(w, r, flow, org) {
				http.Redirect(w, r, "/usage?tab=billing&billing=pending", http.StatusSeeOther)
			}
			return
		}
		if current.CancelAtPeriodEnd {
			http.Error(w, "Undo the scheduled cancellation first.", http.StatusConflict)
			return
		}
		if _, err := changer.SchedulePlanChange(r.Context(), current, nextPriceID, idempotencyKey); err != nil {
			slog.Error("schedule Stripe plan change", "organization_id", org.ID, "error", err)
			http.Error(w, "Could not schedule the plan change.", http.StatusBadGateway)
			return
		}
		if billing.syncAfterChange(w, r, flow, org) {
			http.Redirect(w, r, "/usage?tab=billing&billing=scheduled", http.StatusSeeOther)
		}
	})

	mux.HandleFunc("POST /billing/change/undo", func(w http.ResponseWriter, r *http.Request) {
		if !billingForm(w, r) || !validPlanChangeCSRF(deps, w, r) {
			return
		}
		principal, org, ok := planChangeOwner(deps, w, r)
		if !ok {
			return
		}
		if org.Billing.ScheduledPlanKey == "" || org.Billing.SubscriptionID == "" {
			http.Redirect(w, r, "/usage?tab=billing", http.StatusSeeOther)
			return
		}
		if !deps.Demo {
			if billing == nil || !billing.config.PlanChangesEnabled() {
				http.Error(w, "Plan changes are not available yet.", http.StatusServiceUnavailable)
				return
			}
			config, changer := billing.config, billing.provider
			current, err := changer.RetrieveSubscription(r.Context(), org.Billing.SubscriptionID)
			if err != nil || current.ID != org.Billing.SubscriptionID || current.CustomerID != org.Billing.CustomerID {
				http.Error(w, "Could not verify the current subscription.", http.StatusBadGateway)
				return
			}
			key := fmt.Sprintf("undo:%s:%s:%d:%d", org.ID, org.Billing.ScheduledPlanKey, org.Billing.ScheduledAt.Unix(), org.Billing.ChangeVersion)
			currentKey, currentInterval, mapped := config.planFromPriceID(current.PriceID)
			alreadyUndone := mapped && IsEntitledStatus(current.Status) && currentKey == org.Billing.PlanKey && currentInterval == org.Billing.Interval
			if org.Billing.ScheduledPlanKey == "free" {
				if !current.CancelAtPeriodEnd && !alreadyUndone {
					http.Error(w, "The scheduled cancellation changed in Stripe.", http.StatusConflict)
					return
				}
				if current.CancelAtPeriodEnd {
					err = changer.SetCancelAtPeriodEnd(r.Context(), current.ID, false, key)
				}
			} else {
				if org.Billing.ScheduleID == "" || current.ScheduleID != org.Billing.ScheduleID && (current.ScheduleID != "" || !alreadyUndone) {
					http.Error(w, "The scheduled plan changed in Stripe.", http.StatusConflict)
					return
				}
				if current.ScheduleID != "" {
					err = changer.ReleaseSchedule(r.Context(), current.ScheduleID, key)
				}
			}
			if err != nil {
				slog.Error("undo Stripe plan change", "organization_id", org.ID, "error", err)
				http.Error(w, "Could not undo the scheduled change.", http.StatusBadGateway)
				return
			}
			if !billing.syncAfterChange(w, r, flow, org) {
				return
			}
		} else if err := changes.UndoDemoChange(principal); err != nil {
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, "/usage?tab=billing&billing=undone", http.StatusSeeOther)
	})
}

// syncAfterChange projects what Stripe accepted, so the scheduled or pending
// change shown to the owner is Stripe's, not a local guess. It reports false
// after writing an error response.
func (billing *Stripe) syncAfterChange(w http.ResponseWriter, r *http.Request, flow BillingFlow, org core.Organization) bool {
	if _, err := billing.SyncCustomer(r.Context(), flow, org.Billing.CustomerID); err != nil {
		slog.Error("sync Stripe customer after plan change", "organization_id", org.ID, "error", err)
		http.Error(w, "Stripe accepted the change, but the dashboard could not refresh it yet. Reload in a moment.", http.StatusBadGateway)
		return false
	}
	return true
}

func validPlanChangeCSRF(deps core.Deps, w http.ResponseWriter, r *http.Request) bool {
	if deps.CSRF != nil && deps.CSRF.Verify(r) != nil {
		http.Error(w, "Invalid CSRF token.", http.StatusForbidden)
		return false
	}
	return true
}

func planChangeOwner(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, core.Organization, bool) {
	principal, ok := requestPrincipal(deps, w, r)
	if !ok {
		return core.Principal{}, core.Organization{}, false
	}
	account, err := deps.Store.SnapshotAs(principal)
	if err != nil || account.ActiveOrganizationID != principal.OrganizationID {
		http.Error(w, "Forbidden.", http.StatusForbidden)
		return core.Principal{}, core.Organization{}, false
	}
	org, err := account.OrganizationFor(principal, principal.OrganizationID)
	if err != nil || org.OwnerUserID != principal.UserID || org.Status != "active" || org.Archived || org.DeletionRequested {
		http.Error(w, "Only the owner can change billing.", http.StatusForbidden)
		return core.Principal{}, core.Organization{}, false
	}
	return principal, org, true
}
