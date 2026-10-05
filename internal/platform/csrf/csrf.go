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

var (
	ErrInvalidToken = errors.New("invalid CSRF token")
	ErrBodyTooLarge = errors.New("request body too large")
)

// maxFormBody bounds the body Protect reads while it looks for a form token,
// before any handler can set its own limit; the largest form a feature
// accepts is a 1 MiB document.
const maxFormBody = 2 << 20

type Options struct {
	CookieName string
	Secure     bool
	// Secret keys the HMAC that makes tokens unforgeable. Every process
	// serving the dashboard must share it; when it is empty, New draws a
	// random one, which only suits a single-process demo.
	Secret []byte
}

type Guard struct {
	sessions core.Sessions
	opts     Options
	secret   []byte
}

func New(sessions core.Sessions, opts Options) *Guard {
	if opts.CookieName == "" {
		opts.CookieName = "ts_dashboard_csrf"
	}
	g := &Guard{sessions: sessions, opts: opts, secret: append([]byte(nil), opts.Secret...)}
	if len(g.secret) == 0 {
		g.secret = make([]byte, 32)
		rand.Read(g.secret)
	}
	return g
}

func (g *Guard) Token(w http.ResponseWriter, r *http.Request) string {
	if cookie, err := r.Cookie(g.opts.CookieName); err == nil && g.validToken(cookie.Value) {
		return cookie.Value
	}
	var random [32]byte
	rand.Read(random[:])
	mac := hmac.New(sha256.New, g.secret)
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
			if _, ok := errors.AsType[*http.MaxBytesError](err); ok {
				return ErrBodyTooLarge
			}
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
		r.Body = http.MaxBytesReader(w, r.Body, maxFormBody)
		switch err := g.Verify(r); {
		case errors.Is(err, ErrBodyTooLarge):
			http.Error(w, "request body too large", http.StatusRequestEntityTooLarge)
			return
		case err != nil:
			http.Error(w, "forbidden", http.StatusForbidden)
			return
		}
		next.ServeHTTP(w, r)
	})
}

// validToken checks a 32-byte random value followed by its HMAC, encoded as
// 86 unpadded base64url characters, decoding into fixed-size buffers.
func (g *Guard) validToken(value string) bool {
	var data [64]byte
	if len(value) != 86 {
		return false
	}
	if n, err := base64.RawURLEncoding.Decode(data[:], []byte(value)); err != nil || n != len(data) {
		return false
	}
	mac := hmac.New(sha256.New, g.secret)
	_, _ = mac.Write(data[:32])
	var sum [sha256.Size]byte
	return hmac.Equal(mac.Sum(sum[:0]), data[32:])
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
