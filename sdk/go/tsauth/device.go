package tsauth

import (
	"cmp"
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/url"
	"time"
)

// CLIClientID is ts-auth's built-in public client for the ts CLI, the
// client of the device authorization grant.
const CLIClientID = "00000000000000000000000007"

const deviceGrant = "urn:ietf:params:oauth:grant-type:device_code"

// Device signs a user in as the ts CLI with the device authorization grant
// (RFC 8628): the user approves on ts-auth's verification page while Wait
// polls. It also refreshes and revokes the CLI's tokens.
//
//	d, err := tsauth.NewDevice(tsauth.DeviceConfig{})
//	auth, err := d.Start(ctx)
//	fmt.Println("Open", auth.VerificationURIComplete)
//	tokens, err := d.Wait(ctx, auth)
type Device struct {
	issuer string // without a trailing slash
	hc     *http.Client
}

// DeviceConfig configures a Device.
type DeviceConfig struct {
	// Issuer is the tenant issuer; empty means DefaultIssuer.
	Issuer string
	// HTTPClient nil uses a client with a 30 s timeout. It is copied and
	// never follows redirects.
	HTTPClient *http.Client
}

// DeviceAuthorization is a pending sign-in the user approves at
// VerificationURIComplete, or at VerificationURI by typing UserCode.
type DeviceAuthorization struct {
	DeviceCode              string `json:"device_code"`
	UserCode                string `json:"user_code"`
	VerificationURI         string `json:"verification_uri"`
	VerificationURIComplete string `json:"verification_uri_complete"`
	ExpiresIn               int    `json:"expires_in"`
	Interval                int    `json:"interval"`
	expiresAt               time.Time
}

// NewDevice returns a device sign-in client.
func NewDevice(cfg DeviceConfig) (*Device, error) {
	u, err := parseIssuer(cfg.Issuer)
	if err != nil {
		return nil, err
	}
	return &Device{issuer: u.String(), hc: noRedirects(cfg.HTTPClient)}, nil
}

// Start requests a device authorization for openid, email and
// offline_access, so Wait's tokens carry a refresh token.
func (d *Device) Start(ctx context.Context) (*DeviceAuthorization, error) {
	started := time.Now()
	body, err := oauthPost(ctx, d.hc, d.issuer+"/oauth/device", url.Values{"client_id": {CLIClientID}, "scope": {"openid email offline_access"}}, CLIClientID, "")
	if err != nil {
		return nil, err
	}
	var a DeviceAuthorization
	if json.Unmarshal(body, &a) != nil || a.DeviceCode == "" || a.UserCode == "" || a.ExpiresIn <= 0 || a.ExpiresIn > 1800 ||
		!d.onIssuer(a.VerificationURI) || a.VerificationURIComplete != "" && !d.onIssuer(a.VerificationURIComplete) {
		return nil, errors.New("tsauth: the device authorization response is invalid")
	}
	if a.VerificationURIComplete == "" {
		a.VerificationURIComplete = a.VerificationURI
	}
	a.Interval = min(max(cmp.Or(a.Interval, 5), 1), 600) // RFC 8628 section 3.2: 5 s when absent
	a.expiresAt = started.Add(time.Duration(a.ExpiresIn) * time.Second)
	return &a, nil
}

// onIssuer keeps the printed verification page on the issuer's host, so a
// tampered response cannot send the user elsewhere to approve.
func (d *Device) onIssuer(raw string) bool {
	u, err := url.Parse(raw)
	i, _ := url.Parse(d.issuer)
	return err == nil && u.Scheme == i.Scheme && u.Host == i.Host && u.User == nil && u.Fragment == ""
}

// Wait polls until the user approves a, then returns the tokens. A denial
// or an expired authorization is *Error access_denied or expired_token.
func (d *Device) Wait(ctx context.Context, a *DeviceAuthorization) (*Tokens, error) {
	if a == nil || a.DeviceCode == "" || a.expiresAt.IsZero() || a.Interval < 1 || a.Interval > 600 {
		return nil, errors.New("tsauth: Wait needs a valid authorization returned by Start")
	}
	caller := ctx
	ctx, cancel := context.WithDeadline(ctx, a.expiresAt)
	defer cancel()
	failure := func(err error) error {
		if caller.Err() != nil {
			return caller.Err()
		}
		if !time.Now().Before(a.expiresAt) {
			return &Error{Status: 400, Code: "expired_token"}
		}
		return err
	}
	interval := time.Duration(a.Interval) * time.Second
	form := url.Values{"grant_type": {deviceGrant}, "device_code": {a.DeviceCode}}
	for {
		timer := time.NewTimer(interval)
		select {
		case <-ctx.Done():
			timer.Stop()
			return nil, failure(ctx.Err())
		case <-timer.C:
		}
		t, err := d.tokens(ctx, form)
		if ctx.Err() != nil {
			return nil, failure(ctx.Err())
		}
		var e *Error
		switch {
		case errors.As(err, &e) && e.Code == "authorization_pending":
		case errors.As(err, &e) && e.Code == "slow_down":
			interval += 5 * time.Second
		default:
			return t, err
		}
	}
}

// Refresh redeems a refresh token for new tokens; ts-auth rotates it, so
// keep the new RefreshToken.
func (d *Device) Refresh(ctx context.Context, refreshToken string) (*Tokens, error) {
	return d.tokens(ctx, url.Values{"grant_type": {"refresh_token"}, "refresh_token": {refreshToken}})
}

// Revoke revokes a refresh or access token (RFC 7009); revoking a refresh
// token ends the sign-in.
func (d *Device) Revoke(ctx context.Context, token string) error {
	_, err := oauthPost(ctx, d.hc, d.issuer+"/oauth/revoke", url.Values{"token": {token}, "client_id": {CLIClientID}}, CLIClientID, "")
	return err
}

func (d *Device) tokens(ctx context.Context, form url.Values) (*Tokens, error) {
	form.Set("client_id", CLIClientID) // a public client names itself in the form
	start := time.Now()
	tr, err := requestToken(ctx, d.hc, d.issuer+"/oauth/token", form, CLIClientID, "")
	if err != nil {
		return nil, err
	}
	return &Tokens{AccessToken: tr.AccessToken, RefreshToken: tr.RefreshToken, IDToken: tr.IDToken, Scope: tr.Scope,
		Expiry: start.Add(time.Duration(tr.ExpiresIn) * time.Second)}, nil
}
