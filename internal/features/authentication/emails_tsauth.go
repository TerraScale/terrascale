package authentication

import (
	"bytes"
	"cmp"
	"context"
	"encoding/json/v2"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// tsAuthEmails is ts-auth's email API, called as the dashboard's own
// confidential client.
type tsAuthEmails struct {
	origin       string
	tenant       string
	clientID     string
	clientSecret string
	client       *http.Client
}

// NewEmailBackend returns ts-auth's email API for the tenant of the
// dashboard's issuer. Without ts-auth, the in-memory demo (demo) keeps each
// project's emails in memory like the rest of its state; a dashboard backed
// by TerraBase keeps none, since all data lives in TerraBase. nil means no
// project can edit emails, as with an issuer that names no tenant.
// ponytail: thin client for ts-auth's /v1/{slug}/email API; move to ts-go's tsauth package once it covers it (TS-597).
func NewEmailBackend(issuer, clientID, clientSecret string, demo bool) EmailBackend {
	if issuer == "" {
		if demo {
			return newMemoryEmails()
		}
		return nil
	}
	u, err := url.Parse(issuer)
	tenant := ""
	if err == nil {
		tenant = strings.Trim(u.Path, "/")
	}
	if err != nil || u.Host == "" || tenant == "" || strings.Contains(tenant, "/") {
		return nil
	}
	return &tsAuthEmails{
		origin: u.Scheme + "://" + u.Host, tenant: tenant, clientID: clientID, clientSecret: clientSecret,
		client: &http.Client{Timeout: 15 * time.Second},
	}
}

// Tenant gives platform administrators the dashboard's own tenant, whose
// emails sign people in to TerraScale. Project slugs are only unique per
// organization, so a project never names a tenant itself.
// ponytail: ts-auth has one tenant today; bind each project to its own once ts-auth provisions them.
func (t *tsAuthEmails) Tenant(_ core.Project, platformAdmin bool) string {
	if platformAdmin {
		return t.tenant
	}
	return ""
}

func (t *tsAuthEmails) Branding(ctx context.Context, tenant string) (brandingDocument, error) {
	var document brandingDocument
	err := t.call(ctx, http.MethodGet, tenant, "/branding", nil, &document)
	return document, err
}

func (t *tsAuthEmails) SaveBranding(ctx context.Context, tenant string, update brandingUpdate) (brandingDocument, error) {
	var document brandingDocument
	err := t.call(ctx, http.MethodPut, tenant, "/branding", update, &document)
	return document, err
}

func (t *tsAuthEmails) Templates(ctx context.Context, tenant string) (templateList, error) {
	var list templateList
	err := t.call(ctx, http.MethodGet, tenant, "/templates", nil, &list)
	return list, err
}

func (t *tsAuthEmails) Template(ctx context.Context, tenant, kind, locale string) (emailTemplate, error) {
	var template emailTemplate
	err := t.call(ctx, http.MethodGet, tenant, templatePath(kind, locale), nil, &template)
	return template, err
}

func (t *tsAuthEmails) SaveTemplate(ctx context.Context, tenant, kind, locale string, update templateUpdate) (emailTemplate, error) {
	var template emailTemplate
	err := t.call(ctx, http.MethodPut, tenant, templatePath(kind, locale), update, &template)
	return template, err
}

func (t *tsAuthEmails) Preview(ctx context.Context, tenant string, preview emailPreview) (renderedEmail, error) {
	var rendered renderedEmail
	err := t.call(ctx, http.MethodPost, tenant, "/preview", preview, &rendered)
	return rendered, err
}

func (t *tsAuthEmails) SendTest(ctx context.Context, tenant string, test emailTest) error {
	return t.call(ctx, http.MethodPost, tenant, "/test", test, nil)
}

func templatePath(kind, locale string) string {
	return "/templates/" + url.PathEscape(kind) + "/" + url.PathEscape(locale)
}

// call sends one request and decodes a 2xx answer into out. ts-auth's
// validation errors become form errors; any other failure is unavailable.
func (t *tsAuthEmails) call(ctx context.Context, method, tenant, path string, in, out any) error {
	var body io.Reader
	if in != nil {
		data, err := json.Marshal(in)
		if err != nil {
			return err
		}
		body = bytes.NewReader(data)
	}
	req, err := http.NewRequestWithContext(ctx, method, t.origin+"/v1/"+url.PathEscape(tenant)+"/email"+path, body)
	if err != nil {
		return err
	}
	// Client credentials are form-encoded inside Basic (RFC 6749, 2.3.1).
	req.SetBasicAuth(url.QueryEscape(t.clientID), url.QueryEscape(t.clientSecret))
	req.Header.Set("Accept", "application/json")
	if in != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := t.client.Do(req)
	if err != nil {
		return fmt.Errorf("%w: ts-auth email API: %w", core.ErrUnavailable, err)
	}
	defer resp.Body.Close()
	if resp.StatusCode >= 200 && resp.StatusCode <= 299 {
		if out == nil {
			return nil
		}
		if err := json.UnmarshalRead(io.LimitReader(resp.Body, 4<<20), out); err != nil {
			return fmt.Errorf("%w: ts-auth email API: %w", core.ErrUnavailable, err)
		}
		return nil
	}
	var problem struct {
		Error struct {
			Code    string `json:"code"`
			Message string `json:"message"`
		} `json:"error"`
	}
	_ = json.UnmarshalRead(io.LimitReader(resp.Body, 64<<10), &problem)
	switch resp.StatusCode {
	case http.StatusBadRequest, http.StatusUnprocessableEntity:
		return formError(cmp.Or(problem.Error.Message, "ts-auth rejected the change."))
	case http.StatusConflict:
		return errEmailConflict
	case http.StatusTooManyRequests:
		return formError("ts-auth is limiting test emails. Try again in a minute.")
	case http.StatusNotFound:
		return fmt.Errorf("%w: ts-auth email %s", core.ErrNotFound, path)
	}
	return fmt.Errorf("%w: ts-auth email API answered %d", core.ErrUnavailable, resp.StatusCode)
}
