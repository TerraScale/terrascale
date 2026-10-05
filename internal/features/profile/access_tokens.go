package profile

import (
	"errors"
	"net/http"
	"slices"
	"strconv"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
)

// errIncorrectPassword rejects creating a token without the current password.
var errIncorrectPassword = profileError("Current password is incorrect.")

// accessTokenLifetimes are the expirations, in days, a user may choose.
var accessTokenLifetimes = []int{7, 30, 90, 365}

const defaultAccessTokenDays = 90

type accessTokenView struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	LastFour  string    `json:"lastFour" jsonschema:"Last four characters of the secret."`
	CreatedAt time.Time `json:"createdAt"`
	ExpiresAt time.Time `json:"expiresAt"`
}

type createdAccessToken struct {
	Token  accessTokenView `json:"token"`
	Secret string          `json:"secret"`
}

func accessTokenViews(tokens []core.AccessToken) []accessTokenView {
	now := time.Now()
	views := make([]accessTokenView, 0, len(tokens))
	for _, token := range tokens {
		if !token.Expired(now) {
			views = append(views, accessTokenView{
				ID: token.ID, Name: token.Name, LastFour: token.LastFour, CreatedAt: token.CreatedAt, ExpiresAt: token.ExpiresAt,
			})
		}
	}
	slices.SortFunc(views, func(a, b accessTokenView) int { return b.CreatedAt.Compare(a.CreatedAt) })
	return views
}

// accessTokenLifetime parses a lifetime in days, defaulting when empty.
func accessTokenLifetime(raw string) (time.Duration, error) {
	days := defaultAccessTokenDays
	if raw != "" {
		parsed, err := strconv.Atoi(raw)
		if err != nil || !slices.Contains(accessTokenLifetimes, parsed) {
			return 0, profileError("Choose an expiration of 7, 30, 90, or 365 days.")
		}
		days = parsed
	}
	return time.Duration(days) * 24 * time.Hour, nil
}

// mountAccessTokens serves token creation and revocation. Browser posts get
// the profile page back, which reveals a new secret exactly once; API
// clients get JSON.
func mountAccessTokens(mux *http.ServeMux, deps core.Deps, service Service) {
	mux.HandleFunc("POST /api/v1/dashboard/profile/access-tokens", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			core.WriteAccessError(w, core.ErrUnauthorized)
			return
		}
		if err := r.ParseForm(); err != nil {
			http.Error(w, "invalid access token form", http.StatusBadRequest)
			return
		}
		// A token outlives the session that creates it, so creating one asks
		// for the password like the other credential changes.
		ctx := r.Context()
		account, err := deps.Store.SnapshotAs(ctx, principal)
		if err != nil {
			if !errors.Is(err, core.ErrUnavailable) {
				err = core.ErrUnauthorized
			}
			writeAccessTokenError(w, r, err)
			return
		}
		if !auth.VerifyPassword(account.User.PasswordHash, r.Form.Get("password")) {
			writeAccessTokenError(w, r, errIncorrectPassword)
			return
		}
		created, err := service.CreateAccessToken(ctx, principal, r.Form.Get("name"), r.Form.Get("expiresInDays"))
		if err != nil {
			writeAccessTokenError(w, r, err)
			return
		}
		// The response carries the one-time secret, so neither caches nor
		// referrers may keep it.
		w.Header().Set("Cache-Control", "no-store")
		w.Header().Set("Referrer-Policy", "no-referrer")
		if core.WantsHTML(r) {
			renderProfile(w, r, deps, principal, profileRequest{Tab: tabAccessTokens, Created: &created})
			return
		}
		core.WriteJSON(w, http.StatusCreated, created)
	})
	mux.HandleFunc("POST /api/v1/dashboard/profile/access-tokens/{tokenID}/revoke", func(w http.ResponseWriter, r *http.Request) {
		principal, ok := deps.Sessions.Principal(r)
		if !ok {
			core.WriteAccessError(w, core.ErrUnauthorized)
			return
		}
		if err := service.RevokeAccessToken(r.Context(), principal, r.PathValue("tokenID")); err != nil {
			writeAccessTokenError(w, r, err)
			return
		}
		if core.WantsHTML(r) {
			redirectProfileStatus(w, r, tabAccessTokens, "token-revoked")
			return
		}
		w.WriteHeader(http.StatusNoContent)
	})
}

func writeAccessTokenError(w http.ResponseWriter, r *http.Request, err error) {
	message := "Could not update access tokens."
	status := http.StatusInternalServerError
	switch {
	case errors.Is(err, core.ErrUnauthorized), errors.Is(err, core.ErrForbidden):
		core.WriteAccessError(w, err)
		return
	case errors.Is(err, core.ErrNotFound):
		message, status = "The access token was not found.", http.StatusNotFound
	case errors.Is(err, core.ErrConflict):
		message, status = "Access token limit reached. Revoke an unused token first.", http.StatusConflict
	case errors.Is(err, core.ErrInvalid):
		message, status = err.Error(), http.StatusBadRequest
	case errors.Is(err, core.ErrUnavailable):
		message, status = "Access tokens are unavailable right now. Try again in a moment.", http.StatusServiceUnavailable
	}
	if core.WantsHTML(r) {
		redirectProfileError(w, r, tabAccessTokens, message)
		return
	}
	http.Error(w, message, status)
}
