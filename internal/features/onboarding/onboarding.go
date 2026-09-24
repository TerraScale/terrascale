package onboarding

import (
	"embed"
	"errors"
	"io/fs"
	"net/http"
	"net/url"
	"strings"
	"unicode"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
)

type pageData struct {
	Registration bool
	DisplayName  string
	Email        string
	Error        string
	Step         string
	Steps        []stepView
}

type stepView struct {
	Key       string
	Label     string
	Completed bool
	Current   bool
}

//go:embed templates/*.html
var featureTemplates embed.FS

func Mount(mux *http.ServeMux, deps core.Deps) {
	templates, err := fs.Sub(featureTemplates, "templates")
	if err != nil {
		panic(err)
	}
	if err := deps.Render.Register("onboarding", templates, "onboarding.html"); err != nil {
		panic(err)
	}

	mux.HandleFunc("GET /onboarding", getOnboarding(deps))
	mux.HandleFunc("POST /onboarding/register", register(deps))
	mux.HandleFunc("POST /onboarding/step", saveStep(deps))
}

func getOnboarding(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			data := pageData{Registration: true, Error: r.URL.Query().Get("error")}
			render(w, r, deps, data)
			return
		}
		account, err := deps.Store.SnapshotAs(principal)
		if err != nil {
			render(w, r, deps, pageData{Registration: true})
			return
		}
		if account.OnboardingCompleted {
			http.Redirect(w, r, "/", http.StatusSeeOther)
			return
		}
		data := pageData{
			DisplayName: account.User.Name,
			Email:       account.User.Email,
			Error:       r.URL.Query().Get("error"),
			Step:        account.OnboardingStep,
			Steps:       steps(account.OnboardingStep),
		}
		render(w, r, deps, data)
	}
}

func register(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid registration form", http.StatusBadRequest)
			return
		}
		account, err := auth.RegisterAccount(deps, auth.RegistrationInput{
			DisplayName:     r.Form.Get("displayName"),
			Email:           r.Form.Get("email"),
			Password:        r.Form.Get("password"),
			ConfirmPassword: r.Form.Get("confirmPassword"),
			AcceptedTerms:   r.Form.Get("terms") == "true",
		})
		if err != nil {
			message := "Could not create account. Try again."
			if inputError, ok := errors.AsType[auth.InputError](err); ok {
				message = inputError.Error()
			} else if errors.Is(err, core.ErrConflict) {
				message = "An account with this email already exists."
			}
			http.Redirect(w, r, "/onboarding?error="+url.QueryEscape(message), http.StatusSeeOther)
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
			} else if !utf8.ValidString(name) || strings.IndexFunc(name, unicode.IsControl) >= 0 {
				message = "Enter a valid name."
			} else if utf8.RuneCountInString(name) > 128 {
				message = "Use a name with 128 characters or fewer."
			} else {
				err = deps.Store.UpdateAs(principal, func(account *core.Account) error {
					account.User.Name = name
					if account.OnboardingStep == "account-information" {
						account.OnboardingStep = "payment-method"
					}
					for id, org := range account.Organizations {
						if org.Kind == "personal" {
							member := org.Members[account.User.ID]
							member.Name = name
							org.Members[account.User.ID] = member
							account.Organizations[id] = org
						}
					}
					return nil
				})
			}
		case "payment-method":
			if account.OnboardingStep != "payment-method" {
				message = "Complete the current setup step first."
			} else if r.Form.Get("bypass") != "1" {
				message = "Payment setup is optional. Continue without adding a payment method."
			} else {
				err = deps.Store.UpdateAs(principal, func(account *core.Account) error {
					account.OnboardingStep = "project"
					return nil
				})
			}
		case "project":
			name := strings.TrimSpace(r.Form.Get("name"))
			if account.OnboardingStep != "project" {
				message = "Complete the current setup step first."
			} else if !utf8.ValidString(name) || strings.IndexFunc(name, unicode.IsControl) >= 0 {
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
	definitions := []struct{ key, label string }{
		{key: "account-information", label: "Account"},
		{key: "payment-method", label: "Payment"},
		{key: "project", label: "Project"},
	}
	result := make([]stepView, 0, len(definitions))
	currentIndex := -1
	for i, definition := range definitions {
		if definition.key == current {
			currentIndex = i
		}
	}
	for i, definition := range definitions {
		result = append(result, stepView{
			Key: definition.key, Label: definition.label,
			Completed: currentIndex < 0 || i < currentIndex,
			Current:   i == currentIndex,
		})
	}
	return result
}

func render(w http.ResponseWriter, r *http.Request, deps core.Deps, data pageData) {
	page := core.Page{
		Template: "onboarding",
		Layout:   "auth",
		Title:    "Activate your TerraScale account",
		Data:     data,
	}
	if err := deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render onboarding page", http.StatusInternalServerError)
	}
}
