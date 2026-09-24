package csrf

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"net/http"
	"net/url"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

var ErrInvalidToken = errors.New("invalid CSRF token")

type Options struct {
	CookieName string
	Secure     bool
}

type Guard struct {
	sessions core.Sessions
	opts     Options
	secret   [32]byte
}

func New(sessions core.Sessions, opts Options) *Guard {
	if opts.CookieName == "" {
		opts.CookieName = "ts_dashboard_csrf"
	}
	g := &Guard{sessions: sessions, opts: opts}
	if _, err := rand.Read(g.secret[:]); err != nil {
		panic("crypto/rand unavailable: " + err.Error())
	}
	return g
}

func (g *Guard) Token(w http.ResponseWriter, r *http.Request) string {
	if cookie, err := r.Cookie(g.opts.CookieName); err == nil && g.validToken(cookie.Value) {
		return cookie.Value
	}
	var random [32]byte
	if _, err := rand.Read(random[:]); err != nil {
		panic("crypto/rand unavailable: " + err.Error())
	}
	mac := hmac.New(sha256.New, g.secret[:])
	_, _ = mac.Write(random[:])
	token := base64.RawURLEncoding.EncodeToString(append(random[:], mac.Sum(nil)...))
	http.SetCookie(w, &http.Cookie{
		Name: g.opts.CookieName, Value: token, Path: "/", Secure: g.opts.Secure,
		HttpOnly: true, SameSite: http.SameSiteLaxMode,
	})
	return token
}

func (g *Guard) Verify(r *http.Request) error {
	if !sameOrigin(r, g.opts.Secure) {
		return ErrInvalidToken
	}
	cookie, err := r.Cookie(g.opts.CookieName)
	if err != nil || !g.validToken(cookie.Value) {
		return ErrInvalidToken
	}
	token := r.Header.Get("X-CSRF-Token")
	if token == "" {
		if err := r.ParseForm(); err != nil {
			return ErrInvalidToken
		}
		token = r.PostForm.Get("_csrf")
	}
	if !g.validToken(token) || subtle.ConstantTimeCompare([]byte(cookie.Value), []byte(token)) != 1 {
		return ErrInvalidToken
	}
	return nil
}

func (g *Guard) Protect(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.Method {
		case http.MethodGet, http.MethodHead, http.MethodOptions:
			next.ServeHTTP(w, r)
			return
		}
		if r.Method == http.MethodPost && r.URL.Path == "/api/auth/stripe/webhook" {
			next.ServeHTTP(w, r)
			return
		}
		if _, ok := g.sessions.BearerPrincipal(r); ok {
			next.ServeHTTP(w, r)
			return
		}
		if err := g.Verify(r); err != nil {
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (g *Guard) validToken(value string) bool {
	if len(value) != 86 {
		return false
	}
	data, err := base64.RawURLEncoding.DecodeString(value)
	if err != nil || len(data) != 64 {
		return false
	}
	mac := hmac.New(sha256.New, g.secret[:])
	_, _ = mac.Write(data[:32])
	return subtle.ConstantTimeCompare(mac.Sum(nil), data[32:]) == 1
}

func sameOrigin(r *http.Request, secure bool) bool {
	if strings.EqualFold(r.Header.Get("Sec-Fetch-Site"), "cross-site") {
		return false
	}
	raw := r.Header.Get("Origin")
	if raw == "" {
		raw = r.Header.Get("Referer")
	}
	if raw == "" {
		return true
	}
	u, err := url.Parse(raw)
	if err != nil || !strings.EqualFold(u.Host, r.Host) {
		return false
	}
	if secure {
		return u.Scheme == "https"
	}
	return u.Scheme == "http" || u.Scheme == "https"
}
