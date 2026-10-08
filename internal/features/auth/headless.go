package auth

import (
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"golang.org/x/oauth2"
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/sdk/tsauth"
)

// ts-auth's headless API (TS-582). The dashboard calls it from its backend
// as a confidential client to sign users in with a password or an emailed
// code, sign them up and reset passwords, so only external identity
// providers need the redirect flow. Signing in is a grant at the token
// endpoint; the steps that only send mail are JSON endpoints under the
// issuer.
const (
	passwordGrant  = "urn:terrascale:params:oauth:grant-type:password"
	emailCodeGrant = "urn:terrascale:params:oauth:grant-type:email-code"

	headlessCodeStart     = "/headless/v1/email-code/start"
	headlessSignUp        = "/headless/v1/sign-up"
	headlessResetStart    = "/headless/v1/password-reset/start"
	headlessResetComplete = "/headless/v1/password-reset/complete"
)

// errInvalidCode is an emailed code ts-auth refused: wrong, expired or used
// up, or one for an address without an account, which reads the same.
var errInvalidCode = errors.New("invalid or expired code")

// tsAuthRefusal is a 4xx answer from ts-auth other than a rate limit.
type tsAuthRefusal struct {
	Status        int
	Code, Message string
}

func (e tsAuthRefusal) Error() string {
	return fmt.Sprintf("ts-auth refused the request: %d %s", e.Status, e.Code)
}

// grant signs in at ts-auth's token endpoint with one of its headless grants
// for the browser that sent r, from the end user's address client.
func (t *TSAuth) grant(r *http.Request, client, grantType string, fields url.Values) (*oauth2.Token, error) {
	provider, err := t.discover(r.Context())
	if err != nil {
		return nil, fmt.Errorf("%w: %w", core.ErrUnavailable, err)
	}
	var tokens *tsauth.Tokens
	switch grantType {
	case passwordGrant:
		tokens, err = t.headlessClient.SignInWithPassword(r.Context(), fields.Get("email"), fields.Get("password"), client, provider.oauth.Scopes)
	case emailCodeGrant:
		tokens, err = t.headlessClient.SignInWithEmailCode(r.Context(), fields.Get("email"), fields.Get("code"), client, fields.Get("pending_token"), provider.oauth.Scopes)
	default:
		return nil, errors.New("unsupported headless grant")
	}
	if err != nil {
		return nil, headlessError(err)
	}
	if tokens.IDToken == "" {
		return nil, fmt.Errorf("%w: ts-auth answered without an ID token", errSignInRejected)
	}
	return oauthTokens(tokens), nil
}

// headless calls the SDK's JSON steps, retaining the pending-token answer
// the native sign-up form consumes.
func (t *TSAuth) headless(r *http.Request, client, path string, fields map[string]string) ([]byte, error) {
	var err error
	switch path {
	case headlessCodeStart:
		err = t.headlessClient.StartEmailCode(r.Context(), fields["email"], client)
	case headlessSignUp:
		var pending string
		pending, err = t.headlessClient.SignUp(r.Context(), fields["email"], fields["password"], client)
		if err == nil {
			return json.Marshal(map[string]string{"pending_token": pending})
		}
	case headlessResetStart:
		err = t.headlessClient.StartPasswordReset(r.Context(), fields["email"], client)
	case headlessResetComplete:
		err = t.headlessClient.CompletePasswordReset(r.Context(), fields["email"], fields["code"], fields["new_password"], client)
	default:
		return nil, errors.New("unsupported headless step")
	}
	return nil, headlessError(err)
}

// revoke ends the ts-auth sign-in a session's refresh token belongs to.
// A legacy issuer advertising no revocation endpoint stays a no-op.
func (t *TSAuth) revoke(ctx context.Context, refreshToken string) error {
	provider, err := t.discover(ctx)
	if err != nil || provider.revocation == "" {
		return err
	}
	return headlessError(t.headlessClient.Revoke(ctx, refreshToken))
}

// headlessError maps SDK errors to the native forms' existing contract.
func headlessError(err error) error {
	if err == nil {
		return nil
	}
	var status int
	var code, message string
	var wait time.Duration
	if refusal, ok := errors.AsType[*tsauth.Error](err); ok {
		status, code, message, wait = refusal.Status, refusal.Code, refusal.Description, refusal.RetryAfter
	} else if refusal, ok := errors.AsType[*tsauth.APIError](err); ok {
		status, code, message, wait = refusal.Status, string(refusal.Code), refusal.Message, refusal.RetryAfter
	}
	if status == http.StatusTooManyRequests {
		return tooManyAttempts{wait: min(max(wait, time.Second), time.Hour)}
	}
	if status >= 400 && status < 500 && status != http.StatusUnauthorized {
		return tsAuthRefusal{Status: status, Code: code, Message: message}
	}
	// 401 is invalid_client: the dashboard's registration, not the visitor.
	return fmt.Errorf("ts-auth: %w: %w", core.ErrUnavailable, err)
}

// headlessTransport caps numeric Retry-After before the pinned SDK's 32-bit
// parser, preserving the dashboard's one-hour cap even for larger values.
type headlessTransport struct{ http.RoundTripper }

func (t headlessTransport) RoundTrip(r *http.Request) (*http.Response, error) {
	response, err := t.RoundTripper.RoundTrip(r)
	if err == nil && response.StatusCode == http.StatusTooManyRequests {
		// ParseUint returns the maximum value on positive overflow too.
		if seconds, _ := strconv.ParseUint(strings.TrimSpace(response.Header.Get("Retry-After")), 10, 64); seconds > 3600 {
			response.Header.Set("Retry-After", "3600")
		}
	}
	return response, err
}

// refusedAs maps ts-auth's refusal code to err, so callers can tell a wrong
// secret from a broken request.
func refusedAs(err error, code string, as error) error {
	if refusal, ok := errors.AsType[tsAuthRefusal](err); ok && refusal.Code == code {
		return as
	}
	return err
}

// passwordRefused is the InputError for a password ts-auth's policy refused,
// or err unchanged.
func passwordRefused(err error, field string) error {
	refusal, ok := errors.AsType[tsAuthRefusal](err)
	if !ok || refusal.Code != "invalid_password" {
		return err
	}
	message := refusal.Message
	if message == "" || len(message) > 200 {
		message = "Choose a different password."
	}
	return InputError{field, message}
}

// oauthTokens adapts the SDK answer for the dashboard's strict ID-token
// verification and session state. The SDK already enforces Bearer tokens.
func oauthTokens(tokens *tsauth.Tokens) *oauth2.Token {
	return (&oauth2.Token{
		AccessToken: tokens.AccessToken, TokenType: "Bearer", RefreshToken: tokens.RefreshToken, Expiry: tokens.Expiry,
	}).WithExtra(map[string]any{"id_token": tokens.IDToken})
}
