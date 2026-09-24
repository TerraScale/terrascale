package profile

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
	AccountID string
	Name      string
	Email     string
	Phone     string
	Locale    string
	Tab       string
	Error     string
}

//go:embed templates/*.html
var featureTemplates embed.FS

func Mount(mux *http.ServeMux, deps core.Deps) {
	templates, err := fs.Sub(featureTemplates, "templates")
	if err != nil {
		panic(err)
	}
	if err := deps.Render.Register("profile", templates, "profile.html"); err != nil {
		panic(err)
	}

	mux.HandleFunc("GET /profile", getProfile(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile", updateProfile(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/password", changePassword(deps))
}

func getProfile(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Redirect(w, r, "/login", http.StatusSeeOther)
			return
		}
		account, err := deps.Store.SnapshotAs(principal)
		if err != nil {
			http.Error(w, "account scope is no longer available", http.StatusUnauthorized)
			return
		}
		tab := r.URL.Query().Get("tab")
		if tab != "security" {
			tab = "profile"
		}
		page := core.Page{
			Template:  "profile",
			Layout:    "shell",
			Title:     "Profile",
			Principal: principal,
			Data: pageData{
				AccountID: account.ID,
				Name:      account.User.Name,
				Email:     account.User.Email,
				Phone:     account.User.Phone,
				Locale:    account.User.Locale,
				Tab:       tab,
				Error:     r.URL.Query().Get("error"),
			},
		}
		if err := deps.Render.Page(w, r, page); err != nil {
			http.Error(w, "could not render profile page", http.StatusInternalServerError)
		}
	}
}

func updateProfile(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid profile form", http.StatusBadRequest)
			return
		}
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Redirect(w, r, "/login", http.StatusSeeOther)
			return
		}
		name := strings.TrimSpace(r.Form.Get("name"))
		phone := strings.TrimSpace(r.Form.Get("phoneNumber"))
		if !utf8.ValidString(name) || strings.IndexFunc(name, unicode.IsControl) >= 0 {
			http.Redirect(w, r, "/profile?error="+url.QueryEscape("Enter a valid name."), http.StatusSeeOther)
			return
		}
		if utf8.RuneCountInString(name) > 128 {
			http.Redirect(w, r, "/profile?error="+url.QueryEscape("Use a name with 128 characters or fewer."), http.StatusSeeOther)
			return
		}
		if !utf8.ValidString(phone) || len(phone) > 64 || strings.IndexFunc(phone, unicode.IsControl) >= 0 {
			http.Redirect(w, r, "/profile?error="+url.QueryEscape("Enter a valid phone number."), http.StatusSeeOther)
			return
		}
		err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
			if name != "" {
				account.User.Name = name
				if organization, ok := account.Organizations[principal.OrganizationID]; ok {
					if member, ok := organization.Members[account.User.ID]; ok {
						member.Name = name
						organization.Members[account.User.ID] = member
						account.Organizations[principal.OrganizationID] = organization
					}
				}
			}
			account.User.Phone = phone
			return nil
		})
		if err != nil {
			writeUpdateError(w, err)
			return
		}
		http.Redirect(w, r, "/profile", http.StatusSeeOther)
	}
}

func changePassword(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid password form", http.StatusBadRequest)
			return
		}
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			http.Redirect(w, r, "/login", http.StatusSeeOther)
			return
		}
		account, err := deps.Store.SnapshotAs(principal)
		if err != nil {
			http.Error(w, "account scope is no longer available", http.StatusUnauthorized)
			return
		}
		current := r.Form.Get("currentPassword")
		next := r.Form.Get("newPassword")
		confirm := r.Form.Get("confirmPassword")
		if !auth.VerifyPassword(account.User.PasswordHash, current) {
			redirectPasswordError(w, r, "Current password is incorrect.")
			return
		}
		if utf8.RuneCountInString(next) < 12 {
			redirectPasswordError(w, r, "Use at least 12 characters.")
			return
		}
		if len(next) > 1024 {
			redirectPasswordError(w, r, "Use a password with 1024 bytes or fewer.")
			return
		}
		if next != confirm {
			redirectPasswordError(w, r, "Passwords do not match.")
			return
		}
		passwordHash, err := auth.HashPassword(next)
		if err != nil {
			http.Error(w, "could not update password", http.StatusInternalServerError)
			return
		}
		previousHash := account.User.PasswordHash
		if err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
			if account.User.PasswordHash != previousHash {
				return core.ErrConflict
			}
			account.User.PasswordHash = passwordHash
			return nil
		}); err != nil {
			if errors.Is(err, core.ErrConflict) {
				deps.Sessions.End(w, r)
				http.Redirect(w, r, "/login", http.StatusSeeOther)
				return
			}
			writeUpdateError(w, err)
			return
		}
		deps.Sessions.RevokeUser(principal.AccountID, principal.UserID)
		deps.Sessions.End(w, r)
		http.Redirect(w, r, "/login", http.StatusSeeOther)
	}
}

func redirectPasswordError(w http.ResponseWriter, r *http.Request, message string) {
	http.Redirect(w, r, "/profile?tab=security&error="+url.QueryEscape(message), http.StatusSeeOther)
}

func writeUpdateError(w http.ResponseWriter, err error) {
	if errors.Is(err, core.ErrForbidden) || errors.Is(err, core.ErrNotFound) {
		http.Error(w, "account scope is no longer available", http.StatusUnauthorized)
		return
	}
	http.Error(w, "could not update profile", http.StatusInternalServerError)
}
