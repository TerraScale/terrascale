package tsauth

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// Headless is ts-auth's headless authentication API (TS-582) for a
// legacy confidential-client declarations. The historical JSON steps have
// retired; the remaining password/email-code grants are not served either.
//
// Deprecated: use hosted/registered OIDC sign-in or Device. This remaining
// legacy grant surface will be removed; it does not establish shipped support.
//
//	h, err := tsauth.NewHeadless(tsauth.HeadlessConfig{ClientID: "ts-dashboard", ClientSecret: secret})
//	tokens, err := h.SignInWithPassword(ctx, email, password, clientIP, []string{"openid", "email", "offline_access"})
type Headless struct {
	issuer   string // without a trailing slash
	clientID string
	secret   string
	hc       *http.Client
}

// HeadlessConfig configures a Headless client.
type HeadlessConfig struct {
	// Issuer is the tenant issuer; empty means DefaultIssuer.
	Issuer string
	// ClientID and ClientSecret are a confidential client allowed the
	// headless grants.
	ClientID, ClientSecret string
	// HTTPClient nil uses a client with a 30 s timeout. It is copied and
	// never follows redirects.
	HTTPClient *http.Client
}

// The headless grants of the token endpoint.
const (
	passwordGrant  = "urn:terrascale:params:oauth:grant-type:password"
	emailCodeGrant = "urn:terrascale:params:oauth:grant-type:email-code"
)

// Headless JSON API error codes, besides ErrInvalidRequest.
const (
	ErrInvalidPassword ErrorCode = "invalid_password" // outside the policy: 8 to 1024 UTF-8 bytes
	ErrInvalidCode     ErrorCode = "invalid_code"     // a wrong, expired, used or superseded code, or an unknown address
	ErrInvalidClient   ErrorCode = "invalid_client"   // 401: the client's id or secret
	ErrRateLimited     ErrorCode = "rate_limited"     // 429: see APIError.RetryAfter
)

// NewHeadless returns a headless client.
func NewHeadless(cfg HeadlessConfig) (*Headless, error) {
	if cfg.ClientID == "" || cfg.ClientSecret == "" {
		return nil, errors.New("tsauth: the headless API needs a confidential client: ClientID and ClientSecret")
	}
	u, err := parseIssuer(cfg.Issuer)
	if err != nil {
		return nil, err
	}
	return &Headless{issuer: u.String(), clientID: cfg.ClientID, secret: cfg.ClientSecret, hc: noRedirects(cfg.HTTPClient)}, nil
}

// Tokens are a sign-in's tokens.
type Tokens struct {
	AccessToken string
	// RefreshToken is set with the offline_access scope.
	RefreshToken string
	// IDToken is set with the openid scope.
	IDToken string
	Scope   string
	Expiry  time.Time
}

// SignInWithPassword signs a user in with email and password. A wrong email
// or password is *Error invalid_grant, the same for an unknown address.
func (h *Headless) SignInWithPassword(ctx context.Context, email, password, ip string, scopes []string) (*Tokens, error) {
	return h.grant(ctx, url.Values{"grant_type": {passwordGrant}, "email": {email}, "password": {password}}, ip, scopes)
}

// SignInWithEmailCode signs a user in with an emailed code, from
// StartEmailCode or SignUp. A sign-up code also needs the pendingToken
// SignUp returned, and creates the account; otherwise pass "".
func (h *Headless) SignInWithEmailCode(ctx context.Context, email, code, ip, pendingToken string, scopes []string) (*Tokens, error) {
	form := url.Values{"grant_type": {emailCodeGrant}, "email": {email}, "code": {code}}
	if pendingToken != "" {
		form.Set("pending_token", pendingToken)
	}
	return h.grant(ctx, form, ip, scopes)
}

// Refresh redeems a refresh token for new tokens; ts-auth rotates it, so
// keep the new RefreshToken. Empty scopes keep the original ones.
func (h *Headless) Refresh(ctx context.Context, refreshToken string, scopes []string) (*Tokens, error) {
	form := url.Values{"grant_type": {"refresh_token"}, "refresh_token": {refreshToken}}
	if len(scopes) > 0 {
		form.Set("scope", strings.Join(scopes, " "))
	}
	return h.tokens(ctx, form)
}

// Revoke revokes a refresh or access token (RFC 7009); revoking a refresh
// token ends its sign-in. An unknown token is not an error.
func (h *Headless) Revoke(ctx context.Context, token string) error {
	_, err := oauthPost(ctx, h.hc, h.issuer+"/oauth/revoke", url.Values{"token": {token}}, h.clientID, h.secret)
	return err
}

func (h *Headless) grant(ctx context.Context, form url.Values, ip string, scopes []string) (*Tokens, error) {
	if ip == "" {
		return nil, errors.New("tsauth: the end user's IP address is required")
	}
	form.Set("ip_address", ip)
	if len(scopes) > 0 {
		form.Set("scope", strings.Join(scopes, " "))
	}
	return h.tokens(ctx, form)
}

func (h *Headless) tokens(ctx context.Context, form url.Values) (*Tokens, error) {
	start := time.Now()
	tr, err := requestToken(ctx, h.hc, h.issuer+"/oauth/token", form, h.clientID, h.secret)
	if err != nil {
		return nil, err
	}
	return &Tokens{AccessToken: tr.AccessToken, RefreshToken: tr.RefreshToken, IDToken: tr.IDToken, Scope: tr.Scope,
		Expiry: start.Add(time.Duration(tr.ExpiresIn) * time.Second)}, nil
}
