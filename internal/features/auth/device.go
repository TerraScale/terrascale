package auth

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json/v2"
	"mime"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/a-h/templ"
	"terrascale.tech/dashboard/internal/core"
)

const (
	deviceClient     = "terrascale-cli"
	deviceGrantType  = "urn:ietf:params:oauth:grant-type:device_code"
	deviceLifetime   = 10 * time.Minute
	cliTokenLifetime = 30 * 24 * time.Hour
	maxDeviceGrants  = 1000
	deviceAlphabet   = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
)

type deviceGrant struct {
	codeHash          [32]byte
	expires, nextPoll time.Time
	interval          time.Duration
	denied            bool
	principal         core.Principal
	session           func() (core.Principal, bool)
}

// DeviceAuthorization owns bounded, expiring device grants in the dashboard's
// in-memory demo. Only code digests are retained. Tokens are minted once, during
// redemption, after explicit approval by a live credential-bound browser session.
type DeviceAuthorization struct {
	deps   core.Deps
	secure bool
	mu     sync.Mutex
	grants map[[32]byte]*deviceGrant
	codes  map[[32]byte][32]byte
	now    func() time.Time
}

func NewDeviceAuthorization(deps core.Deps, secure bool) *DeviceAuthorization {
	return &DeviceAuthorization{deps: deps, secure: secure, grants: map[[32]byte]*deviceGrant{}, codes: map[[32]byte][32]byte{}, now: time.Now}
}

func (d *DeviceAuthorization) MountBrowser(mux *http.ServeMux) {
	mux.HandleFunc("GET /cli/approve", d.page)
	mux.HandleFunc("POST /cli/approve/submit", d.decide)
}

// PublicHandler sits outside cookie CSRF middleware. These endpoints never use
// browser credentials; the approval POST remains behind the dashboard CSRF guard.
func (d *DeviceAuthorization) PublicHandler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("POST /api/v1/dashboard/cli/device-authorization", d.issue)
	mux.HandleFunc("POST /api/v1/dashboard/cli/token", d.poll)
	return http.NewCrossOriginProtection().Handler(mux)
}

func deviceJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Pragma", "no-cache")
	w.Header().Set("Content-Type", "application/json")
	b, err := json.Marshal(value)
	if err != nil {
		http.Error(w, "could not encode authorization response", 500)
		return
	}
	w.WriteHeader(status)
	w.Write(b)
}

func deviceError(w http.ResponseWriter, status int, code string) {
	deviceJSON(w, status, map[string]string{"error": code})
}

func deviceForm(w http.ResponseWriter, r *http.Request) bool {
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	media, _, err := mime.ParseMediaType(r.Header.Get("Content-Type"))
	if err != nil || media != "application/x-www-form-urlencoded" || r.ParseForm() != nil {
		deviceError(w, 400, "invalid_request")
		return false
	}
	if r.PostForm.Get("client_id") != deviceClient {
		deviceError(w, 400, "invalid_client")
		return false
	}
	return true
}

func (d *DeviceAuthorization) allow(w http.ResponseWriter, r *http.Request, account string) bool {
	if d.deps.Throttle == nil {
		deviceError(w, 503, "temporarily_unavailable")
		return false
	}
	if wait, ok := d.deps.Throttle.Allow(r, account); !ok {
		w.Header().Set("Retry-After", strconv.FormatInt(max(1, int64(wait.Seconds())+1), 10))
		deviceError(w, 429, "slow_down")
		return false
	}
	return true
}

func (d *DeviceAuthorization) issue(w http.ResponseWriter, r *http.Request) {
	if !deviceForm(w, r) || !d.allow(w, r, "") {
		return
	}
	scheme := "http"
	if d.secure || r.TLS != nil {
		scheme = "https"
	}
	base := (&url.URL{Scheme: scheme, Host: r.Host}).String()
	now := d.now()
	d.mu.Lock()
	defer d.mu.Unlock()
	for key, grant := range d.grants {
		if !now.Before(grant.expires) {
			d.remove(key, grant)
		}
	}
	if len(d.grants) >= maxDeviceGrants {
		deviceError(w, 503, "temporarily_unavailable")
		return
	}
	var secret [32]byte
	rand.Read(secret[:])
	device := base64.RawURLEncoding.EncodeToString(secret[:])
	key := sha256.Sum256([]byte(device))
	var code string
	var codeHash [32]byte
	for {
		var random [8]byte
		rand.Read(random[:])
		var text [8]byte
		for i, b := range random {
			text[i] = deviceAlphabet[int(b)%len(deviceAlphabet)]
		}
		code = string(text[:])
		codeHash = sha256.Sum256([]byte(code))
		if _, exists := d.codes[codeHash]; !exists {
			break
		}
	}
	d.grants[key] = &deviceGrant{codeHash: codeHash, expires: now.Add(deviceLifetime), nextPoll: now.Add(5 * time.Second), interval: 5 * time.Second}
	d.codes[codeHash] = key
	code = code[:4] + "-" + code[4:]
	verification := base + "/cli/approve"
	deviceJSON(w, 200, map[string]any{"device_code": device, "user_code": code, "verification_uri": verification, "verification_uri_complete": verification + "?user_code=" + url.QueryEscape(code), "expires_in": int(deviceLifetime.Seconds()), "interval": 5})
}

func (d *DeviceAuthorization) remove(key [32]byte, grant *deviceGrant) {
	delete(d.grants, key)
	delete(d.codes, grant.codeHash)
}

func (d *DeviceAuthorization) poll(w http.ResponseWriter, r *http.Request) {
	if !deviceForm(w, r) {
		return
	}
	if r.PostForm.Get("grant_type") != deviceGrantType {
		deviceError(w, 400, "unsupported_grant_type")
		return
	}
	device := r.PostForm.Get("device_code")
	if len(device) != 43 {
		if d.allow(w, r, "") {
			deviceError(w, 400, "invalid_grant")
		}
		return
	}
	key := sha256.Sum256([]byte(device))
	d.mu.Lock()
	defer d.mu.Unlock()
	grant, ok := d.grants[key]
	if !ok {
		if d.allow(w, r, "") {
			deviceError(w, 400, "invalid_grant")
		}
		return
	}
	now := d.now()
	if !now.Before(grant.expires) {
		d.remove(key, grant)
		deviceError(w, 400, "expired_token")
		return
	}
	if now.Before(grant.nextPoll) {
		grant.interval += 5 * time.Second
		grant.nextPoll = now.Add(grant.interval)
		deviceError(w, 400, "slow_down")
		return
	}
	grant.nextPoll = now.Add(grant.interval)
	if grant.denied {
		d.remove(key, grant)
		deviceError(w, 400, "access_denied")
		return
	}
	if grant.session == nil {
		deviceError(w, 400, "authorization_pending")
		return
	}
	p, valid := grant.session()
	if !valid || p.AccountID != grant.principal.AccountID || p.UserID != grant.principal.UserID || p.AuthVersion != grant.principal.AuthVersion {
		d.remove(key, grant)
		deviceError(w, 400, "access_denied")
		return
	}
	_, token, err := d.deps.Store.CreateAccessToken(p, "TerraScale CLI", cliTokenLifetime)
	d.remove(key, grant)
	if err != nil {
		deviceError(w, 400, "access_denied")
		return
	}
	deviceJSON(w, 200, map[string]any{"access_token": token, "token_type": "Bearer", "expires_in": int(cliTokenLifetime.Seconds())})
}

func normalizedDeviceCode(raw string) (string, bool) {
	code := strings.ToUpper(strings.ReplaceAll(strings.TrimSpace(raw), "-", ""))
	if len(code) != 8 || strings.ContainsFunc(code, func(r rune) bool { return !strings.ContainsRune(deviceAlphabet, r) }) {
		return "", false
	}
	return code, true
}

type devicePage struct {
	Code, Email, Error, Result, SignInURL string
	Pending, Authenticated                bool
}

func (d *DeviceAuthorization) lookup(raw string) ([32]byte, *deviceGrant, bool) {
	code, valid := normalizedDeviceCode(raw)
	if !valid {
		return [32]byte{}, nil, false
	}
	key, exists := d.codes[sha256.Sum256([]byte(code))]
	if !exists {
		return key, nil, false
	}
	grant := d.grants[key]
	return key, grant, grant != nil && d.now().Before(grant.expires) && !grant.denied && grant.session == nil
}

func (d *DeviceAuthorization) page(w http.ResponseWriter, r *http.Request) {
	data := devicePage{Code: r.URL.Query().Get("user_code")}
	if data.Code != "" {
		if !d.allow(w, r, "") {
			return
		}
		d.mu.Lock()
		_, _, data.Pending = d.lookup(data.Code)
		d.mu.Unlock()
		if !data.Pending {
			data.Error = "This code is invalid, expired, or already decided. Start a new CLI login."
		}
	}
	if p, ok := d.deps.Sessions.Principal(r); ok {
		if account, err := d.deps.Store.SnapshotAs(p); err == nil {
			data.Authenticated = true
			data.Email = account.User.Email
		}
	}
	data.SignInURL = "/login?returnTo=" + url.QueryEscape("/cli/approve?user_code="+url.QueryEscape(data.Code))
	d.render(w, r, data)
}

func (d *DeviceAuthorization) render(w http.ResponseWriter, r *http.Request, data devicePage) {
	w.Header().Set("Cache-Control", "no-store")
	// Preserve the same-origin form POST's Origin for CSRF verification while
	// keeping the device code out of referrers sent to other origins.
	w.Header().Set("Referrer-Policy", "same-origin")
	page := core.Page{Layout: "auth", Title: "Approve CLI access", View: func(p core.Page) templ.Component { return deviceAuthorizationView(data, p.CSRFToken) }}
	if err := d.deps.Render.Page(w, r, page); err != nil {
		http.Error(w, "could not render device authorization", 500)
	}
}

func (d *DeviceAuthorization) decide(w http.ResponseWriter, r *http.Request) {
	// Enforce CSRF here too: this handler must stay safe when mounted in isolation.
	r.Body = http.MaxBytesReader(w, r.Body, 4096)
	if d.deps.CSRF == nil || d.deps.CSRF.Verify(r) != nil {
		http.Error(w, "forbidden", 403)
		return
	}
	if err := r.ParseForm(); err != nil {
		http.Error(w, "invalid approval form", 400)
		return
	}
	p, ok := d.deps.Sessions.Principal(r)
	if !ok {
		redirectToLogin(w, r)
		return
	}
	account, err := d.deps.Store.SnapshotAs(p)
	if err != nil {
		http.Error(w, "forbidden", 403)
		return
	}
	if !d.allow(w, r, account.User.Email) {
		return
	}
	code := r.PostForm.Get("user_code")
	data := devicePage{Code: code, Email: account.User.Email, Authenticated: true, Pending: true}
	action := r.PostForm.Get("decision")
	if action != "approve" && action != "deny" {
		http.Error(w, "invalid decision", 400)
		return
	}
	var validate func() (core.Principal, bool)
	if action == "approve" {
		if !VerifyPassword(account.User.PasswordHash, r.PostForm.Get("password")) {
			data.Error = "Current password is incorrect."
			d.render(w, r, data)
			return
		}
		if sessions, ok := d.deps.Sessions.(core.CLIGrantSessions); ok {
			validate, ok = sessions.BindCLIGrant(r)
			if !ok {
				validate = nil
			}
		}
		if validate == nil {
			http.Error(w, "browser session cannot authorize this CLI", 403)
			return
		}
		fresh, valid := validate()
		if !valid || fresh.AuthVersion != p.AuthVersion || fresh.UserID != p.UserID || fresh.AccountID != p.AccountID {
			http.Error(w, "browser credentials changed", 403)
			return
		}
	}
	d.mu.Lock()
	_, grant, valid := d.lookup(code)
	if valid {
		grant.denied = action == "deny"
		grant.principal = p
		grant.session = validate
	}
	d.mu.Unlock()
	if !valid {
		data.Error = "This code is invalid, expired, or already decided."
		data.Pending = false
		d.render(w, r, data)
		return
	}
	d.deps.Throttle.Forgive(r, account.User.Email)
	data.Pending = false
	data.Result = action
	d.render(w, r, data)
}
