package tsauth

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"
)

const StepUpProofHeader = "X-TerraScale-Step-Up-Proof"

type StepUpRequest struct {
	Purpose   string `json:"purpose"`
	Audience  string `json:"audience"`
	ReturnURI string `json:"return_uri"`
}

type StepUpBody struct {
	Proof     string    `json:"proof"`
	StepUpURL string    `json:"step_up_url"`
	ExpiresAt time.Time `json:"expires_at"`
}

const (
	StepUpCreateAPIKey = "create_api_key"
	StepUpRotateAPIKey = "rotate_api_key"
)

const DefaultDashboardOrigin = "https://dash.terrascale.tech"

func (u *User) RequestStepUp(ctx context.Context, purpose string) (*StepUpBody, error) {
	if purpose != StepUpCreateAPIKey && purpose != StepUpRotateAPIKey {
		return nil, fmt.Errorf("tsauth: unsupported step-up purpose %q", purpose)
	}
	var out StepUpBody
	body := StepUpRequest{Purpose: purpose, Audience: "ts-auth", ReturnURI: DefaultDashboardOrigin + "/auth/step-up/callback"}
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

func (u *User) WithStepUpProof(proof string) *User {
	return &User{c: u.c, token: u.token, proof: proof}
}
