package tsauth

import (
	"context"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

const StepUpProofHeader = "X-TerraScale-Step-Up-Proof"

type StepUpRequest struct {
	Purpose        string `json:"purpose"`
	Audience       string `json:"audience"`
	ReturnURI      string `json:"return_uri"`
	OrganizationID string `json:"organization_id,omitempty"`
}

type StepUpBody struct {
	Proof     string    `json:"proof"`
	StepUpURL string    `json:"step_up_url"`
	ExpiresAt time.Time `json:"expires_at"`
}

const (
	StepUpCreateAPIKey           = "create_api_key"
	StepUpRotateAPIKey           = "rotate_api_key"
	StepUpDeleteOrganization     = "delete_organization"
	StepUpStartOwnershipTransfer = "start_ownership_transfer"
)

const DefaultDashboardOrigin = "https://dash.terrascale.tech"

func (u *User) RequestStepUp(ctx context.Context, purpose string) (*StepUpBody, error) {
	if purpose != StepUpCreateAPIKey && purpose != StepUpRotateAPIKey {
		return nil, fmt.Errorf("tsauth: unsupported step-up purpose %q", purpose)
	}
	return u.requestStepUp(ctx, purpose, "")
}

func (u *User) RequestOrganizationStepUp(ctx context.Context, purpose, organization string) (*StepUpBody, error) {
	if purpose != StepUpDeleteOrganization && purpose != StepUpStartOwnershipTransfer {
		return nil, fmt.Errorf("tsauth: unsupported organization step-up purpose %q", purpose)
	}
	if err := entityid.Check(organization); err != nil {
		return nil, err
	}
	return u.requestStepUp(ctx, purpose, organization)
}

func (u *User) requestStepUp(ctx context.Context, purpose, organization string) (*StepUpBody, error) {
	var out StepUpBody
	body := StepUpRequest{Purpose: purpose, Audience: "ts-auth", ReturnURI: DefaultDashboardOrigin + "/auth/step-up/callback", OrganizationID: organization}
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
