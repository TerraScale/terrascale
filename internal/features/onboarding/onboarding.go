package onboarding

import (
	"cmp"
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strconv"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/features/payments"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/ui"
)

// pageData is the onboarding page. Error is shown above the step's form, or
// under the control named by ErrorField. Plan and ReturnTo come from the link
// that opened sign-up: the payment step preselects Plan, and a new account
// continues to ReturnTo, a dashboard path, instead of setup.
type pageData struct {
	Registration    bool
	Demo            bool
	DisplayName     string
	Email           string
	AcceptedTerms   bool
	Error           string
	ErrorField      string
	PaymentCanceled bool
	Plan            string
	ReturnTo        string
	Plans           []core.Plan
	Step            string
	Steps           []stepView
	CurrentStep     int
	TotalSteps      int
	Progress        int
}

type stepView struct {
	Key       string `json:"key"`
	Label     string `json:"label"`
	Number    string `json:"number"`
	State     string `json:"state" jsonschema:"COMPLETED, CURRENT, or NOT STARTED."`
	Completed bool   `json:"completed"`
	Current   bool   `json:"current"`
}

// Mount registers account setup and its tools. billing is nil in demo mode.
func Mount(mux *http.ServeMux, deps core.Deps, billing *payments.Stripe) {
	service := Service{deps: deps, billing: payments.NewService(deps, subscriptions.NewBillingService(deps.Store), billing)}
	mountTools(deps.Tools, service)
	mux.HandleFunc("GET /register", func(w http.ResponseWriter, r *http.Request) {
		target := "/onboarding"
		if r.URL.RawQuery != "" {
			target += "?" + r.URL.RawQuery
		}
		http.Redirect(w, r, target, http.StatusSeeOther)
	})
	mux.HandleFunc("GET /onboarding", getOnboarding(deps, service))
	mux.HandleFunc("POST /onboarding", register(deps))
	mux.HandleFunc("POST /onboarding/step", saveStep(deps, service))
}

func getOnboarding(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		query := r.URL.Query()
		ctx := r.Context()
		plans, err := paidPlans(ctx, deps.Store)
		if err != nil {
			if !core.WriteAccessError(w, err) {
				http.Error(w, "could not load plans", http.StatusInternalServerError)
			}
			return
		}
		// Landing pages link with return=; sign-in and the dashboard use returnTo.
		registration := pageData{Registration: true, Plan: offeredPlan(plans, query.Get("plan")), ReturnTo: returnPath(cmp.Or(query.Get("returnTo"), query.Get("return")))}
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			render(w, r, deps, http.StatusOK, registration)
			return
		}
		account, err := deps.Store.SnapshotAs(ctx, principal)
		if err != nil {
			if errors.Is(err, core.ErrUnavailable) {
				http.Error(w, "service unavailable", http.StatusServiceUnavailable)
				return
			}
			render(w, r, deps, http.StatusOK, registration)
			return
		}
		if account.OnboardingCompleted {
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		paymentOutcome := r.URL.Query().Get("payment")
		if paymentOutcome == "success" && account.OnboardingStep == "payment-method" {
			if err := service.ConfirmPayment(ctx, principal); err != nil {
				if errors.Is(err, core.ErrUnavailable) {
					http.Error(w, "service unavailable", http.StatusServiceUnavailable)
					return
				}
				http.Error(w, "could not confirm payment setup", http.StatusInternalServerError)
				return
			}
			account, err = deps.Store.SnapshotAs(ctx, principal)
			if err != nil {
				if errors.Is(err, core.ErrUnavailable) {
					http.Error(w, "service unavailable", http.StatusServiceUnavailable)
					return
				}
				http.Error(w, "account not found", http.StatusUnauthorized)
				return
			}
		}
		data := pageData{
			DisplayName:     account.User.Name,
			Email:           account.User.Email,
			Error:           r.URL.Query().Get("error"),
			PaymentCanceled: paymentOutcome == "canceled",
			Step:            account.OnboardingStep,
			Plans:           plans,
		}
		if paymentOutcome == "success" && data.Step == "payment-method" {
			data.Error = "Payment setup could not be confirmed. Finish checkout or continue without payment."
		}
		requested := query.Get("plan")
		if requested == "" && data.PaymentCanceled {
			// A canceled checkout comes back without the link's plan; offer
			// the plan it was for again.
			requested = checkoutPlan(account, principal, plans)
		}
		data.Plan = cmp.Or(offeredPlan(plans, requested), firstPlan(plans))
		render(w, r, deps, http.StatusOK, data)
	}
}

func register(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid registration form", http.StatusBadRequest)
			return
		}
		input := auth.RegistrationInput{
			DisplayName:     r.Form.Get("displayName"),
			Email:           r.Form.Get("email"),
			Password:        r.Form.Get("password"),
			ConfirmPassword: r.Form.Get("confirmPassword"),
			AcceptedTerms:   r.Form.Get("terms") == "true",
		}
		ctx := r.Context()
		plans, err := paidPlans(ctx, deps.Store)
		if err != nil {
			if !core.WriteAccessError(w, err) {
				http.Error(w, "could not load plans", http.StatusInternalServerError)
			}
			return
		}
		plan, returnTo := offeredPlan(plans, r.Form.Get("plan")), returnPath(r.Form.Get("returnTo"))
		account, err := auth.RegisterAccount(ctx, deps, input)
		if err != nil {
			// Render the form again with everything but the passwords kept.
			data := pageData{
				Registration: true, DisplayName: input.DisplayName, Email: input.Email,
				AcceptedTerms: input.AcceptedTerms, Plan: plan, ReturnTo: returnTo,
			}
			status := http.StatusUnprocessableEntity
			switch inputError, isInputError := errors.AsType[auth.InputError](err); {
			case isInputError:
				data.ErrorField, data.Error = inputError.Field, inputError.Message
			case errors.Is(err, core.ErrConflict):
				status = http.StatusConflict
				data.ErrorField, data.Error = "email", "An account with this email already exists. Sign in to it, or use another email."
			case errors.Is(err, core.ErrUnavailable):
				status = http.StatusServiceUnavailable
				data.Error = "Account creation is unavailable right now. Try again in a moment."
			default:
				status = http.StatusInternalServerError
				data.Error = "Could not create your account. Try again."
			}
			render(w, r, deps, status, data)
			return
		}
		if err := deps.Sessions.Start(w, r, account.Principal()); err != nil {
			http.Error(w, "could not create session", http.StatusInternalServerError)
			return
		}
		// Like sign-in, a return path wins; setup resumes at the next sign-in.
		location := "/onboarding"
		switch {
		case returnTo != "":
			location = returnTo
		case plan != "":
			location += "?" + url.Values{"plan": {plan}}.Encode()
		}
		http.Redirect(w, r, location, http.StatusSeeOther)
	}
}

func saveStep(deps core.Deps, service Service) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid onboarding form", http.StatusBadRequest)
			return
		}
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Redirect(w, r, "/login", http.StatusSeeOther)
			return
		}
		result, err := service.SaveStep(r.Context(), principal, stepInput{
			Step: r.Form.Get("step"), DisplayName: r.Form.Get("displayName"), Bypass: r.Form.Get("bypass") == "1",
			Plan: r.Form.Get("plan"), Interval: r.Form.Get("interval"), Name: r.Form.Get("name"), Region: r.Form.Get("region"),
		})
		switch message, isStepError := errors.AsType[stepError](err); {
		case isStepError:
			query := url.Values{"error": {string(message)}}
			// A refused payment step keeps the plan that was picked.
			// A plan catalog that cannot be read offers no plan to keep.
			plans, _ := paidPlans(r.Context(), deps.Store)
			if plan := offeredPlan(plans, r.Form.Get("plan")); r.Form.Get("step") == "payment-method" && plan != "" {
				query.Set("plan", plan)
			}
			http.Redirect(w, r, "/onboarding?"+query.Encode(), http.StatusSeeOther)
		case errors.Is(err, core.ErrUnauthorized), errors.Is(err, core.ErrForbidden), errors.Is(err, core.ErrNotFound):
			http.Error(w, "account scope is no longer available", http.StatusUnauthorized)
		case errors.Is(err, payments.ErrSubscriptionManaged):
			http.Redirect(w, r, "/billing/portal", http.StatusSeeOther)
		case errors.Is(err, core.ErrConflict):
			http.Error(w, err.Error(), http.StatusConflict)
		case err != nil:
			if status, ok := payments.ProviderStatus(err); ok {
				http.Error(w, err.Error(), status)
				return
			}
			if errors.Is(err, core.ErrUnavailable) {
				http.Error(w, "service unavailable", http.StatusServiceUnavailable)
				return
			}
			http.Error(w, "could not save onboarding step", http.StatusInternalServerError)
		case result.CheckoutURL != "":
			http.Redirect(w, r, result.CheckoutURL, http.StatusSeeOther)
		case result.Completed:
			http.Redirect(w, r, "/", http.StatusSeeOther)
		default:
			http.Redirect(w, r, "/onboarding", http.StatusSeeOther)
		}
	}
}

func steps(current string) []stepView {
	definitions := []struct{ key, label, number string }{
		{key: "account-information", label: "Account", number: "1"},
		{key: "payment-method", label: "Payment", number: "2"},
		{key: "project", label: "Project", number: "3"},
	}
	result := make([]stepView, 0, len(definitions))
	currentIndex := -1
	for i, definition := range definitions {
		if definition.key == current {
			currentIndex = i
		}
	}
	for i, definition := range definitions {
		completed := currentIndex < 0 || i < currentIndex
		state := "NOT STARTED"
		if completed {
			state = "COMPLETED"
		} else if i == currentIndex {
			state = "CURRENT"
		}
		result = append(result, stepView{
			Key: definition.key, Label: definition.label, Number: definition.number,
			State: state, Completed: completed, Current: i == currentIndex,
		})
	}
	return result
}

func render(w http.ResponseWriter, r *http.Request, deps core.Deps, status int, data pageData) {
	data.Demo = deps.Demo
	if data.Registration {
		data.Step = "account-information"
	}
	data.Steps = steps(data.Step)
	data.TotalSteps = len(data.Steps)
	data.CurrentStep = 1
	for i, step := range data.Steps {
		if step.Current {
			data.CurrentStep = i + 1
			break
		}
	}
	if data.TotalSteps > 0 {
		data.Progress = data.CurrentStep * 100 / data.TotalSteps
	}
	title := "Activate your TerraScale account"
	if data.Registration {
		title = "Create account"
	}
	page := core.Page{
		Layout: "auth",
		Title:  title,
		Status: status,
		View: func(p core.Page) templ.Component {
			return onboardingView(data, p.CSRFToken)
		},
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render onboarding page", http.StatusInternalServerError)
	}
}

// returnPath is the dashboard path a sign-up link asks to return to, or ""
// when there is none.
func returnPath(raw string) string {
	if target := auth.SafeRedirectTarget(raw); target != "/" {
		return target
	}
	return ""
}

// offeredPlan returns requested when it is an offered plan's key, otherwise "".
func offeredPlan(plans []core.Plan, requested string) string {
	if slices.ContainsFunc(plans, func(plan core.Plan) bool { return plan.Key == requested }) {
		return requested
	}
	return ""
}

func firstPlan(plans []core.Plan) string {
	if len(plans) == 0 {
		return ""
	}
	return plans[0].Key
}

// checkoutPlan is the key of the plan in the principal's latest onboarding
// checkout, if it is still offered.
func checkoutPlan(account core.Account, principal core.Principal, plans []core.Plan) string {
	var latest core.CheckoutSession
	for _, checkout := range account.Organizations[principal.OrganizationID].CheckoutSessions {
		if checkout.OwnerUserID == principal.UserID && checkout.ReturnPath == "/onboarding" && checkout.CreatedAt.After(latest.CreatedAt) {
			latest = checkout
		}
	}
	for _, plan := range plans {
		if plan.ID == latest.PlanID && latest.PlanID != "" {
			return plan.Key
		}
	}
	return ""
}

// signInURL links to sign-in, passing on the page to return to afterwards.
func signInURL(returnTo string) string {
	if returnTo == "" {
		return "/login"
	}
	return "/login?returnTo=" + url.QueryEscape(returnTo)
}

// field labels one registration control and attaches the submission's error
// when it belongs to that control.
func field(data pageData, id, label, hint string) ui.FieldProps {
	props := ui.FieldProps{ID: id, Label: label, Hint: hint}
	if data.ErrorField == id {
		props.Error = data.Error
	}
	return props
}

func onboardingStepCurrent(step stepView) string {
	if step.Current {
		return "step"
	}
	return ""
}

func onboardingStepGlyph(step stepView) string {
	if step.Completed {
		return "✓"
	}
	return step.Number
}

func onboardingProgressValue(value int) string {
	return strconv.Itoa(value)
}

// planTone gives each paid plan card its own accent.
func planTone(index int) string {
	return [...]string{"blue", "violet", "fuchsia", "emerald"}[index%4]
}
