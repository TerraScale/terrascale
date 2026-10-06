package auth

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"

	"golang.org/x/oauth2"
	"terrascale.tech/dashboard/internal/core"
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

// maxHeadlessAnswer bounds what the dashboard reads of one ts-auth answer.
const maxHeadlessAnswer = 1 << 20

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
	fields.Set("grant_type", grantType)
	fields.Set("ip_address", client)
	fields.Set("scope", strings.Join(provider.oauth.Scopes, " "))
	answer, err := t.post(r.Context(), provider.oauth.Endpoint.TokenURL, "application/x-www-form-urlencoded", []byte(fields.Encode()))
	if err != nil {
		return nil, err
	}
	return decodeTokens(answer)
}

// headless posts fields as JSON to ts-auth's headless endpoint path, from
// the end user's address client, and returns the answer's body.
func (t *TSAuth) headless(r *http.Request, client, path string, fields map[string]string) ([]byte, error) {
	fields["ip_address"] = client
	body, err := json.Marshal(fields)
	if err != nil {
		return nil, err
	}
	return t.post(r.Context(), strings.TrimSuffix(t.cfg.Issuer, "/")+path, "application/json", body)
}

// revoke ends the ts-auth sign-in a session's refresh token belongs to, at
// the revocation endpoint ts-auth advertises (RFC 7009).
func (t *TSAuth) revoke(ctx context.Context, refreshToken string) error {
	provider, err := t.discover(ctx)
	if err != nil || provider.revocation == "" {
		return err
	}
	form := url.Values{"token": {refreshToken}, "token_type_hint": {"refresh_token"}}
	_, err = t.post(ctx, provider.revocation, "application/x-www-form-urlencoded", []byte(form.Encode()))
	return err
}

// post sends body to ts-auth as the dashboard's confidential client and
// returns a successful answer's body. A rate limit is tooManyAttempts, any
// other 4xx a tsAuthRefusal, and the rest wraps core.ErrUnavailable.
func (t *TSAuth) post(ctx context.Context, target, contentType string, body []byte) ([]byte, error) {
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, target, bytes.NewReader(body))
	if err != nil {
		return nil, err
	}
	request.Header.Set("Content-Type", contentType)
	request.Header.Set("Accept", "application/json")
	// client_secret_basic, as at the token endpoint (RFC 6749 §2.3.1).
	request.SetBasicAuth(url.QueryEscape(t.cfg.ClientID), url.QueryEscape(t.cfg.ClientSecret))
	response, err := t.client.Do(request)
	if err != nil {
		return nil, fmt.Errorf("ts-auth: %w: %w", core.ErrUnavailable, err)
	}
	defer response.Body.Close()
	answer, err := io.ReadAll(io.LimitReader(response.Body, maxHeadlessAnswer))
	if err != nil {
		return nil, fmt.Errorf("ts-auth: %w: %w", core.ErrUnavailable, err)
	}
	switch status := response.StatusCode; {
	case status >= 200 && status < 300:
		return answer, nil
	case status == http.StatusTooManyRequests:
		// Bounded before it becomes a Duration, which would overflow.
		seconds, _ := strconv.Atoi(response.Header.Get("Retry-After"))
		return nil, tooManyAttempts{wait: time.Duration(min(max(seconds, 1), 3600)) * time.Second}
	case status >= 400 && status < 500 && status != http.StatusUnauthorized:
		return nil, decodeRefusal(status, answer)
	}
	// 401 is invalid_client: the dashboard's registration, not the visitor.
	return nil, fmt.Errorf("ts-auth answered %d: %w", response.StatusCode, core.ErrUnavailable)
}

// decodeRefusal reads ts-auth's two error shapes: the token endpoint's
// {"error", "error_description"} and the JSON API's {"error": {"code",
// "message"}}.
func decodeRefusal(status int, answer []byte) tsAuthRefusal {
	refusal := tsAuthRefusal{Status: status}
	var oauthError struct {
		Code    string `json:"error"`
		Message string `json:"error_description"`
	}
	if json.Unmarshal(answer, &oauthError) == nil {
		refusal.Code, refusal.Message = oauthError.Code, oauthError.Message
		return refusal
	}
	var apiError struct {
		Error struct {
			Code    string `json:"code"`
			Message string `json:"message"`
		} `json:"error"`
	}
	if json.Unmarshal(answer, &apiError) == nil {
		refusal.Code, refusal.Message = apiError.Error.Code, apiError.Error.Message
	}
	return refusal
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

// decodeTokens reads a token endpoint answer.
func decodeTokens(answer []byte) (*oauth2.Token, error) {
	var tokens struct {
		AccessToken  string `json:"access_token"`
		TokenType    string `json:"token_type"`
		RefreshToken string `json:"refresh_token"`
		IDToken      string `json:"id_token"`
		ExpiresIn    int64  `json:"expires_in"`
	}
	if err := json.Unmarshal(answer, &tokens); err != nil || tokens.IDToken == "" {
		return nil, fmt.Errorf("%w: ts-auth answered without an ID token", errSignInRejected)
	}
	token := &oauth2.Token{AccessToken: tokens.AccessToken, TokenType: tokens.TokenType, RefreshToken: tokens.RefreshToken}
	if tokens.ExpiresIn > 0 {
		token.Expiry = time.Now().Add(time.Duration(tokens.ExpiresIn) * time.Second)
	}
	return token.WithExtra(map[string]any{"id_token": tokens.IDToken}), nil
}
