package tsauth

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// StepUpProofHeader carries an approved purpose-bound proof at the sensitive
// mutation itself (contract: step-up-proof.md).
const StepUpProofHeader = "X-TerraScale-Step-Up-Proof"

// StepUpRequest starts a pending proof bound to one operation.
type StepUpRequest struct {
	Purpose   string `json:"purpose"`
	Audience  string `json:"audience"`
	ReturnURI string `json:"return_uri"`
}

// StepUpBody is the answer of POST /v1/terrascale/step-up: the backend-only
// proof, the hosted verification URL and the five-minute deadline.
type StepUpBody struct {
	Proof     string    `json:"proof"`
	StepUpURL string    `json:"step_up_url"`
	ExpiresAt time.Time `json:"expires_at"`
}

// Step-up purposes of the ts-auth audience the CLI drives.
const (
	StepUpCreateAPIKey = "create_api_key"
	StepUpRotateAPIKey = "rotate_api_key"
)

// stepUpCallback is the registered dashboard origin's fixed callback; the
// hosted confirmation returns there once it approves.
const stepUpCallback = "https://dash.terrascale.tech/auth/step-up/callback"

// RequestStepUp starts a pending purpose-bound proof as the signed-in user.
// The proof stays in this process; only StepUpURL goes to the user.
func (u *User) RequestStepUp(ctx context.Context, purpose string) (*StepUpBody, error) {
	if purpose != StepUpCreateAPIKey && purpose != StepUpRotateAPIKey {
		return nil, fmt.Errorf("tsauth: unsupported step-up purpose %q", purpose)
	}
	var out StepUpBody
	body := StepUpRequest{Purpose: purpose, Audience: "ts-auth", ReturnURI: stepUpCallback}
	if _, err := u.doStatus(ctx, http.MethodPost, []string{"step-up"}, nil, body, &out); err != nil {
		return nil, err
	}
	issuer, _ := url.Parse(u.c.base)
	page, err := url.Parse(out.StepUpURL)
	if out.Proof == "" || err != nil || page.Scheme != issuer.Scheme || page.Host != issuer.Host ||
		page.User != nil || page.RawQuery != "" || page.Fragment != "" || page.Path == "" ||
		page.String() != out.StepUpURL || strings.Contains(out.StepUpURL, out.Proof) ||
		!out.ExpiresAt.After(time.Now()) || time.Until(out.ExpiresAt) > 6*time.Minute {
		return nil, &ResponseContractError{}
	}
	return &out, nil
}

// WithStepUpProof returns the user's requests with one approved proof, so a
// sensitive mutation consumes it in X-TerraScale-Step-Up-Proof.
func (u *User) WithStepUpProof(proof string) *User {
	return &User{c: u.c, token: u.token, proof: proof}
}
