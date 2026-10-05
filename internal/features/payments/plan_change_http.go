package payments

import (
	"context"
	"net/http"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// planChangeFlow is the demo counterpart of the Stripe plan-change calls.
// Live changes are applied in Stripe and then synced like any other change.
type planChangeFlow interface {
	ChangeDemoPlan(context.Context, core.Principal, string, string) error
	ScheduleDemoChange(context.Context, core.Principal, string, string, time.Time) error
	UndoDemoChange(context.Context, core.Principal) error
}

func mountPlanChanges(mux *http.ServeMux, deps core.Deps, flow BillingFlow, service Service) {
	if _, ok := flow.(planChangeFlow); !ok {
		return
	}
	mux.HandleFunc("POST /billing/change", func(w http.ResponseWriter, r *http.Request) {
		if !billingForm(w, r) || !validPlanChangeCSRF(deps, w, r) {
			return
		}
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		outcome, err := service.ChangePlan(r.Context(), principal, r.Form.Get("plan"), r.Form.Get("interval"))
		if err != nil {
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, billingOutcomePath(outcome), http.StatusSeeOther)
	})
	mux.HandleFunc("POST /billing/change/undo", func(w http.ResponseWriter, r *http.Request) {
		if !billingForm(w, r) || !validPlanChangeCSRF(deps, w, r) {
			return
		}
		principal, ok := requestPrincipal(deps, w, r)
		if !ok {
			return
		}
		outcome, err := service.UndoPlanChange(r.Context(), principal)
		if err != nil {
			writeBillingError(w, err)
			return
		}
		http.Redirect(w, r, billingOutcomePath(outcome), http.StatusSeeOther)
	})
}

// billingOutcomePath is the billing tab announcing a plan change outcome.
func billingOutcomePath(outcome string) string {
	if outcome == OutcomeCurrent {
		return "/usage?tab=billing"
	}
	return "/usage?tab=billing&billing=" + outcome
}

func validPlanChangeCSRF(deps core.Deps, w http.ResponseWriter, r *http.Request) bool {
	if deps.CSRF != nil && deps.CSRF.Verify(r) != nil {
		http.Error(w, "Invalid CSRF token.", http.StatusForbidden)
		return false
	}
	return true
}
