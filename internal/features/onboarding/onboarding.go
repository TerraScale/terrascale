package onboarding

import (
	"cmp"
	"errors"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"
	"unicode"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/features/subscriptions"
	"terrascale.tech/dashboard/internal/ui"
)

// pageData is the onboarding page. Error is shown above the step's form, or
// under the control named by ErrorField.
type pageData struct {
	Registration    bool
	Demo            bool
	DisplayName     string
	Email           string
	AcceptedTerms   bool
	Error           string
	ErrorField      string
	PaymentCanceled bool
	Plans           []core.Plan
	Step            string
	Steps           []stepView
	CurrentStep     int
	TotalSteps      int
	Progress        int
}

type stepView struct {
	Key       string
	Label     string
	Number    string
	State     string
	Completed bool
	Current   bool
}

func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /register", func(w http.ResponseWriter, r *http.Request) {
		target := "/onboarding"
		if r.URL.RawQuery != "" {
			target += "?" + r.URL.RawQuery
		}
		http.Redirect(w, r, target, http.StatusSeeOther)
	})
	mux.HandleFunc("GET /onboarding", getOnboarding(deps))
	mux.HandleFunc("POST /onboarding", register(deps))
	mux.HandleFunc("POST /onboarding/step", saveStep(deps))
}

func getOnboarding(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			render(w, r, deps, http.StatusOK, pageData{Registration: true})
			return
		}
		account, err := deps.Store.SnapshotAs(principal)
		if err != nil {
			render(w, r, deps, http.StatusOK, pageData{Registration: true})
			return
		}
		if account.OnboardingCompleted {
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		paymentOutcome := r.URL.Query().Get("payment")
		if paymentOutcome == "success" && account.OnboardingStep == "payment-method" {
			if err := deps.Store.UpdateAs(principal, func(current *core.Account) error {
				if current.OnboardingStep != "payment-method" || current.ActiveOrganizationID != principal.OrganizationID {
					return core.ErrConflict
				}
				organization, ok := current.Organizations[principal.OrganizationID]
				if !ok || organization.Kind != "personal" || !organization.PaymentMethodValidated {
					return core.ErrForbidden
				}
				for _, checkout := range organization.CheckoutSessions {
					if checkout.OwnerUserID == principal.UserID && checkout.OrganizationID == organization.ID &&
						checkout.Status == "complete" && checkout.ReturnPath == "/onboarding" {
						current.OnboardingStep = "project"
						return nil
					}
				}
				return core.ErrForbidden
			}); err == nil {
				account, err = deps.Store.SnapshotAs(principal)
				if err != nil {
					http.Error(w, "account not found", http.StatusUnauthorized)
					return
				}
			} else if !errors.Is(err, core.ErrForbidden) && !errors.Is(err, core.ErrConflict) {
				http.Error(w, "could not confirm payment setup", http.StatusInternalServerError)
				return
			}
		}
		data := pageData{
			DisplayName:     account.User.Name,
			Email:           account.User.Email,
			Error:           r.URL.Query().Get("error"),
			PaymentCanceled: paymentOutcome == "canceled",
			Step:            account.OnboardingStep,
			Steps:           steps(account.OnboardingStep),
		}
		if paymentOutcome == "success" && data.Step == "payment-method" {
			data.Error = "Payment setup could not be confirmed. Finish checkout or continue without payment."
		}
		for _, plan := range deps.Store.Plans() {
			if plan.Visible && plan.PriceCents > 0 {
				data.Plans = append(data.Plans, plan)
			}
		}
		planRank := map[string]int{"starter": 0, "pro": 1, "business": 2}
		slices.SortFunc(data.Plans, func(a, b core.Plan) int {
			aRank, aKnown := planRank[a.Key]
			bRank, bKnown := planRank[b.Key]
			if !aKnown {
				aRank = len(planRank)
			}
			if !bKnown {
				bRank = len(planRank)
			}
			if order := cmp.Compare(aRank, bRank); order != 0 {
				return order
			}
			return cmp.Compare(a.Key, b.Key)
		})
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
		account, err := auth.RegisterAccount(deps, input)
		if err != nil {
			// Render the form again with everything but the passwords kept.
			data := pageData{Registration: true, DisplayName: input.DisplayName, Email: input.Email, AcceptedTerms: input.AcceptedTerms}
			status := http.StatusUnprocessableEntity
			switch inputError, isInputError := errors.AsType[auth.InputError](err); {
			case isInputError:
				data.ErrorField, data.Error = inputError.Field, inputError.Message
			case errors.Is(err, core.ErrConflict):
				status = http.StatusConflict
				data.ErrorField, data.Error = "email", "An account with this email already exists. Sign in to it, or use another email."
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
		http.Redirect(w, r, "/onboarding", http.StatusSeeOther)
	}
}

func saveStep(deps core.Deps) http.HandlerFunc {
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
		account, err := deps.Store.SnapshotAs(principal)
		if err != nil {
			http.Error(w, "account not found", http.StatusUnauthorized)
			return
		}
		if account.OnboardingCompleted {
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}

		var message string
		switch r.Form.Get("step") {
		case "account-information":
			name := strings.TrimSpace(r.Form.Get("displayName"))
			if name == "" {
				message = "This field is required."
			} else if !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl) {
				message = "Enter a valid name."
			} else if utf8.RuneCountInString(name) > 128 {
				message = "Use a name with 128 characters or fewer."
			} else {
				err = deps.Store.UpdateAs(principal, func(account *core.Account) error {
					account.User.Name = name
					if account.OnboardingStep == "account-information" {
						account.OnboardingStep = "payment-method"
					}
					return nil
				})
			}
		case "payment-method":
			if account.OnboardingStep != "payment-method" {
				message = "Complete the current setup step first."
			} else if r.Form.Get("bypass") == "1" {
				err = deps.Store.UpdateAs(principal, func(account *core.Account) error {
					if account.OnboardingStep != "payment-method" {
						return core.ErrConflict
					}
					account.OnboardingStep = "project"
					return nil
				})
			} else if !deps.Demo {
				organization, ok := account.Organizations[principal.OrganizationID]
				if !ok || organization.Kind != "personal" || account.ActiveOrganizationID != organization.ID {
					message = "Return to your Personal workspace to finish account setup."
				} else {
					http.Redirect(w, r, "/billing/checkout", http.StatusTemporaryRedirect)
					return
				}
			} else {
				organization, ok := account.Organizations[principal.OrganizationID]
				if !ok || organization.Kind != "personal" || account.ActiveOrganizationID != organization.ID {
					message = "Return to your Personal workspace to finish account setup."
				} else {
					checkout, checkoutErr := subscriptions.NewBillingService(deps.Store).BeginDemoCheckout(
						principal, r.Form.Get("plan"), r.Form.Get("interval"), "/onboarding",
					)
					if checkoutErr == nil {
						http.Redirect(w, r, "/checkout/"+url.PathEscape(checkout.ID), http.StatusSeeOther)
						return
					}
					if errors.Is(checkoutErr, subscriptions.ErrPlanUnavailable) || errors.Is(checkoutErr, core.ErrInvalid) {
						message = "Choose an available plan and billing interval."
					} else {
						err = checkoutErr
					}
				}
			}
		case "project":
			name := strings.TrimSpace(r.Form.Get("name"))
			if account.OnboardingStep != "project" {
				message = "Complete the current setup step first."
			} else if !utf8.ValidString(name) || strings.ContainsFunc(name, unicode.IsControl) {
				message = "Enter a valid project name."
			} else if utf8.RuneCountInString(name) > 100 {
				message = "Use a project name with 100 characters or fewer."
			} else if region := normalizedRegion(r.Form.Get("region")); region == "" {
				message = "Choose a supported project region."
			} else {
				err = completeProjectStep(deps, principal, name, region)
			}
		default:
			message = "Choose a valid setup step."
		}

		if err != nil {
			if errors.Is(err, core.ErrForbidden) || errors.Is(err, core.ErrNotFound) {
				http.Error(w, "account scope is no longer available", http.StatusUnauthorized)
				return
			}
			http.Error(w, "could not save onboarding step", http.StatusInternalServerError)
			return
		}
		if message != "" {
			http.Redirect(w, r, "/onboarding?error="+url.QueryEscape(message), http.StatusSeeOther)
			return
		}
		if r.Form.Get("step") == "project" {
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		http.Redirect(w, r, "/onboarding", http.StatusSeeOther)
	}
}

func completeProjectStep(deps core.Deps, principal core.Principal, name, region string) error {
	return deps.Store.UpdateAs(principal, func(account *core.Account) error {
		organization, ok := account.Organizations[principal.OrganizationID]
		if !ok {
			return core.ErrNotFound
		}
		project, ok := organization.Projects[account.ActiveProjectID]
		if !ok || project.OrganizationID != organization.ID {
			return core.ErrNotFound
		}
		if name != "" {
			project.Name = name
		}
		project.Region = region
		project.Regions = []string{region}
		organization.Projects[project.ID] = project
		account.Organizations[organization.ID] = organization
		account.OnboardingStep = ""
		account.OnboardingCompleted = true
		return nil
	})
}

func normalizedRegion(region string) string {
	switch strings.ToUpper(strings.TrimSpace(region)) {
	case "GRU", "SFO", "FRA":
		return strings.ToUpper(strings.TrimSpace(region))
	default:
		return ""
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
