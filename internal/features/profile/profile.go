package profile

import (
	"crypto/rand"
	"encoding/hex"
	"errors"
	"net/http"
	"net/mail"
	"net/url"
	"strings"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
)

type pageData struct {
	AccountID            string
	UserID               string
	Role                 string
	OrganizationName     string
	ActiveOrganizationID string
	ActiveProjectID      string
	Name                 string
	Email                string
	Phone                string
	Locale               string
	Tab                  string
	Error                string
	Status               string
	Demo                 bool
	MFAEnabled           bool
	TOTPEnrollmentToken  string
	TOTPSecret           string
	RecoveryCodes        []string
	Passkeys             []passkeyView
	Sessions             []sessionView
}

type passkeyView struct {
	ID        string
	Name      string
	CreatedAt string
}

type sessionView struct {
	ID        string
	Current   bool
	ExpiresAt string
}

func Mount(mux *http.ServeMux, deps core.Deps) {
	mux.HandleFunc("GET /profile", getProfile(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile", updateProfile(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/password", changePassword(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/email-change", changeEmail(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/language", changeLanguage(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/mfa/enroll", beginTOTP(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/mfa/verify", verifyTOTP(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/mfa/remove", removeTOTP(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/mfa/recovery-codes", regenerateRecoveryCodes(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/passkeys/register", registerPasskey(deps))
	mux.HandleFunc("POST /api/v1/dashboard/profile/passkeys/remove", removePasskey(deps))
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
		if tab != "security" && tab != "sessions" {
			tab = "profile"
		}
		var totpSecret string
		var recoveryCodes []string
		if deps.Demo && tab == "security" {
			if token := r.URL.Query().Get("enroll"); token != "" || len(account.DemoSecurity.PendingRecoveryCodes) != 0 {
				if err := deps.Store.UpdateAs(principal, func(current *core.Account) error {
					if token != "" && current.DemoSecurity.PendingTOTPToken == token {
						totpSecret = current.DemoSecurity.PendingTOTPSecret
						current.DemoSecurity.PendingTOTPSecret = ""
					}
					recoveryCodes = current.DemoSecurity.PendingRecoveryCodes
					current.DemoSecurity.PendingRecoveryCodes = nil
					return nil
				}); err != nil {
					http.Error(w, "account scope is no longer available", http.StatusUnauthorized)
					return
				}
			}
		}
		organizationName := ""
		if org, ok := account.Organizations[account.ActiveOrganizationID]; ok {
			organizationName = org.Name
		}
		data := pageData{
			AccountID:            account.ID,
			UserID:               account.User.ID,
			Role:                 principal.Role,
			OrganizationName:     organizationName,
			ActiveOrganizationID: account.ActiveOrganizationID,
			ActiveProjectID:      account.ActiveProjectID,
			Name:                 account.User.Name,
			Email:                account.User.Email,
			Phone:                account.User.Phone,
			Locale:               account.User.Locale,
			Tab:                  tab,
			Error:                r.URL.Query().Get("error"),
			Status:               profileStatus(r.URL.Query().Get("status")),
			Demo:                 deps.Demo,
			MFAEnabled:           account.DemoSecurity.MFAEnabled,
			RecoveryCodes:        recoveryCodes,
			TOTPSecret:           totpSecret,
		}
		if token := r.URL.Query().Get("enroll"); token != "" && account.DemoSecurity.PendingTOTPToken == token {
			data.TOTPEnrollmentToken = token
		}
		for _, passkey := range account.DemoSecurity.Passkeys {
			data.Passkeys = append(data.Passkeys, passkeyView{
				ID: passkey.ID, Name: passkey.Name, CreatedAt: passkey.CreatedAt.UTC().Format("Jan 2, 2006"),
			})
		}
		if lister, ok := deps.Sessions.(interface {
			SessionsForUser(core.Principal, *http.Request) []core.SessionInfo
		}); ok {
			for _, session := range lister.SessionsForUser(principal, r) {
				data.Sessions = append(data.Sessions, sessionView{
					ID: session.ID, Current: session.Current, ExpiresAt: session.ExpiresAt.UTC().Format(time.RFC3339),
				})
			}
		}
		page := core.Page{
			Layout:    "shell",
			Title:     "Profile",
			Principal: principal,
			View: func(p core.Page) templ.Component {
				return profileView(data, p.CSRFToken)
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

func changeEmail(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		email := core.NormalizeEmail(r.Form.Get("newEmail"))
		parsed, err := mail.ParseAddress(email)
		if err != nil || parsed.Address != email || len(email) > 254 {
			redirectProfileError(w, r, "profile", "Enter a valid email address.")
			return
		}
		account, ok := verifiedProfilePassword(deps, w, r, principal, "profile")
		if !ok {
			return
		}
		if err := deps.Store.ChangeEmailAs(principal, email, account.User.PasswordHash); err != nil {
			if errors.Is(err, core.ErrConflict) {
				redirectProfileError(w, r, "profile", "Email is already in use or your account changed.")
				return
			}
			writeUpdateError(w, err)
			return
		}
		redirectProfileStatus(w, r, "profile", "email-changed")
	}
}

func changeLanguage(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		locale := r.Form.Get("locale")
		if locale != "en" && locale != "pt-BR" {
			redirectProfileError(w, r, "profile", "Choose an available language.")
			return
		}
		if err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
			account.User.Locale = locale
			return nil
		}); err != nil {
			writeUpdateError(w, err)
			return
		}
		redirectProfileStatus(w, r, "profile", "language-saved")
	}
}

func beginTOTP(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		account, ok := verifiedProfilePassword(deps, w, r, principal, "security")
		if !ok {
			return
		}
		var random [10]byte
		if _, err := rand.Read(random[:]); err != nil {
			http.Error(w, "could not start demo enrollment", http.StatusInternalServerError)
			return
		}
		secret := strings.ToUpper(hex.EncodeToString(random[:]))
		secret = strings.Join([]string{secret[:4], secret[4:8], secret[8:12], secret[12:16], secret[16:]}, " ")
		token := core.NewID("enr")
		if err := deps.Store.UpdateAs(principal, func(current *core.Account) error {
			if current.User.PasswordHash != account.User.PasswordHash {
				return core.ErrConflict
			}
			if current.DemoSecurity.MFAEnabled {
				return core.ErrConflict
			}
			current.DemoSecurity.PendingTOTPToken = token
			current.DemoSecurity.PendingTOTPSecret = secret
			return nil
		}); err != nil {
			redirectProfileError(w, r, "security", "Demo enrollment could not start. Reload and try again.")
			return
		}
		http.Redirect(w, r, "/profile?tab=security&enroll="+url.QueryEscape(token), http.StatusSeeOther)
	}
}

func verifyTOTP(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		code := strings.TrimSpace(r.Form.Get("code"))
		if len(code) != 6 || strings.IndexFunc(code, func(r rune) bool { return r < '0' || r > '9' }) >= 0 {
			redirectProfileError(w, r, "security", "Enter a six-digit demo code.")
			return
		}
		token := r.Form.Get("enrollment")
		if token == "" {
			redirectProfileError(w, r, "security", "Demo enrollment is unavailable.")
			return
		}
		codes, err := newDemoRecoveryCodes()
		if err != nil {
			http.Error(w, "could not create demo recovery codes", http.StatusInternalServerError)
			return
		}
		if err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
			if account.DemoSecurity.PendingTOTPToken != token || account.DemoSecurity.MFAEnabled {
				return core.ErrNotFound
			}
			account.DemoSecurity.MFAEnabled = true
			account.DemoSecurity.PendingTOTPToken = ""
			account.DemoSecurity.PendingTOTPSecret = ""
			account.DemoSecurity.PendingRecoveryCodes = codes
			return nil
		}); err != nil {
			redirectProfileError(w, r, "security", "Demo enrollment is unavailable.")
			return
		}
		redirectProfileStatus(w, r, "security", "mfa-added")
	}
}

func removeTOTP(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		if r.Form.Get("factor") != "totp" {
			redirectProfileError(w, r, "security", "Unknown demo factor.")
			return
		}
		account, ok := verifiedProfilePassword(deps, w, r, principal, "security")
		if !ok {
			return
		}
		if err := deps.Store.UpdateAs(principal, func(current *core.Account) error {
			if current.User.PasswordHash != account.User.PasswordHash {
				return core.ErrConflict
			}
			current.DemoSecurity.MFAEnabled = false
			current.DemoSecurity.PendingTOTPToken = ""
			current.DemoSecurity.PendingTOTPSecret = ""
			current.DemoSecurity.PendingRecoveryCodes = nil
			return nil
		}); err != nil {
			writeUpdateError(w, err)
			return
		}
		redirectProfileStatus(w, r, "security", "mfa-removed")
	}
}

func regenerateRecoveryCodes(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		account, ok := verifiedProfilePassword(deps, w, r, principal, "security")
		if !ok {
			return
		}
		codes, err := newDemoRecoveryCodes()
		if err != nil {
			http.Error(w, "could not create demo recovery codes", http.StatusInternalServerError)
			return
		}
		if err := deps.Store.UpdateAs(principal, func(current *core.Account) error {
			if current.User.PasswordHash != account.User.PasswordHash || !current.DemoSecurity.MFAEnabled {
				return core.ErrConflict
			}
			current.DemoSecurity.PendingRecoveryCodes = codes
			return nil
		}); err != nil {
			redirectProfileError(w, r, "security", "Demo recovery codes could not be renewed.")
			return
		}
		redirectProfileStatus(w, r, "security", "codes-renewed")
	}
}

func registerPasskey(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		name := strings.TrimSpace(r.Form.Get("name"))
		if name == "" || utf8.RuneCountInString(name) > 128 || !utf8.ValidString(name) || strings.IndexFunc(name, unicode.IsControl) >= 0 {
			redirectProfileError(w, r, "security", "Enter a valid passkey label.")
			return
		}
		if err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
			account.DemoSecurity.Passkeys = append(account.DemoSecurity.Passkeys, core.DemoPasskey{
				ID: core.NewID("pas"), Name: name, CreatedAt: time.Now().UTC(),
			})
			return nil
		}); err != nil {
			writeUpdateError(w, err)
			return
		}
		redirectProfileStatus(w, r, "security", "passkey-added")
	}
}

func removePasskey(deps core.Deps) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		principal, ok := demoProfileForm(deps, w, r)
		if !ok {
			return
		}
		id := r.Form.Get("passkeyId")
		if id == "" {
			redirectProfileError(w, r, "security", "Choose a demo passkey record.")
			return
		}
		if err := deps.Store.UpdateAs(principal, func(account *core.Account) error {
			for index, passkey := range account.DemoSecurity.Passkeys {
				if passkey.ID == id {
					account.DemoSecurity.Passkeys = append(account.DemoSecurity.Passkeys[:index], account.DemoSecurity.Passkeys[index+1:]...)
					return nil
				}
			}
			return core.ErrNotFound
		}); err != nil {
			redirectProfileError(w, r, "security", "Demo passkey record was not found.")
			return
		}
		redirectProfileStatus(w, r, "security", "passkey-removed")
	}
}

func demoProfileForm(deps core.Deps, w http.ResponseWriter, r *http.Request) (core.Principal, bool) {
	if !deps.Demo {
		http.NotFound(w, r)
		return core.Principal{}, false
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "invalid profile form", http.StatusBadRequest)
		return core.Principal{}, false
	}
	principal, ok := deps.Sessions.Principal(r)
	if !ok {
		http.Redirect(w, r, "/login", http.StatusSeeOther)
		return core.Principal{}, false
	}
	return principal, true
}

func verifiedProfilePassword(deps core.Deps, w http.ResponseWriter, r *http.Request, principal core.Principal, tab string) (core.Account, bool) {
	account, err := deps.Store.SnapshotAs(principal)
	if err != nil {
		http.Error(w, "account scope is no longer available", http.StatusUnauthorized)
		return core.Account{}, false
	}
	if !auth.VerifyPassword(account.User.PasswordHash, r.Form.Get("password")) {
		redirectProfileError(w, r, tab, "Current password is incorrect.")
		return core.Account{}, false
	}
	return account, true
}

func newDemoRecoveryCodes() ([]string, error) {
	codes := make([]string, 8)
	for index := range codes {
		var random [5]byte
		if _, err := rand.Read(random[:]); err != nil {
			return nil, err
		}
		codes[index] = hex.EncodeToString(random[:])
	}
	return codes, nil
}

func redirectProfileError(w http.ResponseWriter, r *http.Request, tab, message string) {
	http.Redirect(w, r, "/profile?tab="+tab+"&error="+url.QueryEscape(message), http.StatusSeeOther)
}

func redirectProfileStatus(w http.ResponseWriter, r *http.Request, tab, status string) {
	http.Redirect(w, r, "/profile?tab="+tab+"&status="+status, http.StatusSeeOther)
}

func profileStatus(status string) string {
	switch status {
	case "email-changed":
		return "Email changed in this demo. No verification message was sent."
	case "language-saved":
		return "Language preference saved."
	case "mfa-added":
		return "Demo authenticator record added. It does not protect sign-in."
	case "mfa-removed":
		return "Demo authenticator record removed."
	case "codes-renewed":
		return "Demo recovery codes renewed. They do not authenticate sign-in."
	case "passkey-added":
		return "Demo passkey record added. It does not authenticate sign-in."
	case "passkey-removed":
		return "Demo passkey record removed."
	default:
		return ""
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
