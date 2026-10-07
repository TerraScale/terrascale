package authentication

import (
	"context"
	"encoding/json/v2"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"slices"
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/memstore"
	"terrascale.tech/dashboard/internal/platform/csrf"
	"terrascale.tech/dashboard/internal/platform/session"
)

func newEmailService(t *testing.T) (Service, *memoryEmails, core.Principal) {
	t.Helper()
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "emails-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	emails := newMemoryEmails()
	return Service{deps: core.Deps{Store: store, Demo: true}, emails: emails}, emails, account.Principal()
}

func TestEmailServiceSavesOnlyWhatDiffersFromDefaults(t *testing.T) {
	service, emails, owner := newEmailService(t)
	ctx := t.Context()
	view, err := service.Emails(ctx, owner, "sign_in_code", "en")
	if err != nil || view.Version != 1 || !view.CanManage || view.Template.Overridden {
		t.Fatalf("Emails = %+v, %v; want version 1 with the defaults", view, err)
	}
	save := func(version int, branding *emailBranding, fields *emailFields) (int, error) {
		return service.SaveEmails(ctx, owner, emailChange{Version: version, Branding: branding, Kind: "sign_in_code", Locale: "en", Fields: fields})
	}

	branding, fields := view.Defaults, view.Template.Defaults
	branding.BrandName, branding.PrimaryColor = " Acme ", "#FF0000"
	fields.Subject = "{{ password }}"
	if _, err := save(1, &branding, &fields); err == nil || !strings.Contains(err.Error(), "password") {
		t.Errorf("unknown variable = %v, want it rejected", err)
	}
	if saved, _ := emails.Branding(ctx, owner.ProjectID); saved.Version != 1 || saved.Branding != (emailBranding{}) {
		t.Errorf("after a rejected template the branding = %+v, want nothing saved", saved)
	}

	fields.Subject = "Your Acme code: {{ code }}"
	version, err := save(1, &branding, &fields)
	if err != nil || version != 3 {
		t.Fatalf("SaveEmails = %d, %v; want versions for the branding and the template", version, err)
	}
	saved, _ := emails.Branding(ctx, owner.ProjectID)
	if want := (emailBranding{BrandName: "Acme", PrimaryColor: "#ff0000"}); saved.Branding != want {
		t.Errorf("stored branding = %+v, want only the fields that differ from the defaults: %+v", saved.Branding, want)
	}
	template, _ := emails.Template(ctx, owner.ProjectID, "sign_in_code", "en")
	if !template.Overridden || template.Fields != (emailFields{Subject: "Your Acme code: {{ code }}"}) {
		t.Errorf("stored template = %+v, want only the subject overridden", template)
	}
	if version, err := save(3, &branding, &fields); err != nil || version != 3 {
		t.Errorf("saving unchanged drafts = %d, %v; want no new version", version, err)
	}
	if version, err := save(2, &branding, &fields); !errors.Is(err, errEmailConflict) {
		t.Errorf("saving unchanged drafts over version 2 at version 3 = %d, %v; want a conflict, not a stale version", version, err)
	}
	for _, logo := range []emailBranding{{LogoURL: "https://acme.test/logo.png"}, {LogoURL: "https://acme.test/logo.png", LogoWidth: 120, LogoHeight: 121}, {LogoWidth: 120}} {
		if _, err := save(3, &logo, nil); !errors.Is(err, core.ErrInvalid) {
			t.Errorf("logo %+v = %v, want ts-auth's logo size rule", logo, err)
		}
	}
	fields.Button = "Go"
	if _, err := save(3, nil, &fields); err == nil || !strings.Contains(err.Error(), "button") {
		t.Errorf("button on a code email = %v, want it rejected", err)
	}

	fields.Button, fields.Subject = "", "Stale"
	branding.BrandName = "Acme 2"
	if _, err := save(2, &branding, &fields); !errors.Is(err, core.ErrInvalid) || !strings.Contains(err.Error(), "changed since") {
		t.Errorf("saving over version 2 at version 3 = %v, want a conflict for the form", err)
	}
	if _, err := save(3, nil, &view.Template.Defaults); err != nil {
		t.Fatal(err)
	}
	if template, _ := emails.Template(ctx, owner.ProjectID, "sign_in_code", "en"); template.Overridden {
		t.Error("saving the defaults must drop the override")
	}

	to, err := service.SendTestEmail(ctx, owner, "magic_link", "pt-BR", nil, &emailFields{Button: "Entrar já"})
	if err != nil || to != "emails-owner@example.test" || len(emails.sent) != 1 || emails.sent[0].To != to || emails.sent[0].Fields.Button != "Entrar já" {
		t.Errorf("SendTestEmail = %q, %v, sent %+v; want the draft sent to the owner only", to, err, emails.sent)
	}
	if _, err := service.SendTestEmail(ctx, owner, "magic_link", "fr", nil, nil); !errors.Is(err, core.ErrInvalid) {
		t.Errorf("unknown locale = %v, want invalid", err)
	}
	invalid := &emailFields{Subject: "{{ password }}"}
	if _, err := service.SendTestEmail(ctx, owner, "sign_in_code", "en", nil, invalid); err == nil || !strings.Contains(err.Error(), "password") || len(emails.sent) != 1 {
		t.Errorf("test with an unknown variable = %v, sent %d; want it rejected like a save", err, len(emails.sent))
	}
	if _, err := service.PreviewEmail(ctx, owner, emailPreview{Kind: "sign_in_code", Locale: "en", Fields: invalid}); err == nil || !strings.Contains(err.Error(), "password") {
		t.Errorf("preview with an unknown variable = %v, want it rejected like a save", err)
	}
	rendered, err := service.PreviewEmail(ctx, owner, emailPreview{Kind: "magic_link", Locale: "en", Fields: &emailFields{Preheader: "No expiry here"}})
	if err != nil || !strings.Contains(rendered.Text, "Sign in: https://example.com/continue") || !strings.Contains(rendered.Text, "It expires in 10 minutes.") ||
		!strings.Contains(rendered.HTML, "It expires in 10 minutes.") {
		t.Errorf("plain text = %q, %v; want the button's label and link and the expiry, whatever the wording", rendered.Text, err)
	}
	if rendered, _ := service.PreviewEmail(ctx, owner, emailPreview{Kind: "new_device", Locale: "pt-BR"}); strings.Contains(rendered.Text, "Expira") {
		t.Error("an email without an expiry must not state one")
	}

	audit, err := service.AuditEvents(ctx, owner)
	if err != nil {
		t.Fatal(err)
	}
	var actions []string
	for _, event := range audit.Events {
		actions = append(actions, event.Action)
	}
	for _, want := range []string{"auth.emails.branding_saved", "auth.emails.template_saved", "auth.emails.test_sent"} {
		if !slices.Contains(actions, want) {
			t.Errorf("audit log %v lacks %s", actions, want)
		}
	}
}

// shiftingEmails reports a newer template list than its branding, as when
// another administrator saves between the reads.
type shiftingEmails struct{ *memoryEmails }

func (s shiftingEmails) Templates(ctx context.Context, tenant string) (templateList, error) {
	list, err := s.memoryEmails.Templates(ctx, tenant)
	list.Version++
	return list, err
}

func TestEmailsNeverMixesConfigurationVersions(t *testing.T) {
	service, _, owner := newEmailService(t)
	service.emails = shiftingEmails{newMemoryEmails()}
	if _, err := service.Emails(t.Context(), owner, "sign_in_code", "en"); !errors.Is(err, core.ErrUnavailable) {
		t.Errorf("Emails over moving versions = %v, want unavailable rather than a mixed view", err)
	}
}

func TestEmailTenantNeverComesFromAProjectSlug(t *testing.T) {
	backend := NewEmailBackend("https://gru.auth.terrascale.tech/terrascale", "ts-dashboard", "secret", false)
	project := core.Project{ID: "prj_1", Slug: "terrascale"}
	if got := backend.Tenant(project, false); got != "" {
		t.Errorf("customer project named terrascale edits instance %q, want none", got)
	}
	if got := backend.Tenant(project, true); got != "terrascale" {
		t.Errorf("platform admin edits instance %q, want terrascale", got)
	}
	for _, issuer := range []string{"https://gru.auth.terrascale.tech", "https://gru.auth.terrascale.tech/a/b", "::"} {
		if backend := NewEmailBackend(issuer, "id", "secret", true); backend != nil {
			t.Errorf("issuer %q names no single instance, want no email backend", issuer)
		}
	}
}

// TestEmailsStayOutOfMemoryOutsideTheDemo keeps email settings in ts-auth,
// stored in TerraBase: only the in-memory demo holds them in memory.
func TestEmailsStayOutOfMemoryOutsideTheDemo(t *testing.T) {
	if _, ok := NewEmailBackend("", "", "", true).(*memoryEmails); !ok {
		t.Error("the in-memory demo needs its email stand-in")
	}
	if backend := NewEmailBackend("", "", "", false); backend != nil {
		t.Errorf("a TerraBase-backed dashboard without ts-auth got %T, want no email backend", backend)
	}
	service, _, owner := newEmailService(t)
	service.emails = nil
	if _, err := service.Emails(t.Context(), owner, "sign_in_code", "en"); !errors.Is(err, errNoEmailTenant) {
		t.Errorf("Emails without a backend = %v, want not available", err)
	}
}

func TestTSAuthEmailClientFollowsTheEmailAPI(t *testing.T) {
	type call struct{ method, path, query, body string }
	var calls []call
	status := http.StatusOK
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		user, password, ok := r.BasicAuth()
		if !ok || user != "ts-dashboard" || password != "s%2Bcret%3A" {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		body, _ := io.ReadAll(r.Body)
		calls = append(calls, call{r.Method, r.URL.Path, r.URL.RawQuery, string(body)})
		w.WriteHeader(status)
		switch {
		case status == http.StatusUnprocessableEntity:
			io.WriteString(w, `{"error":{"code":"invalid_request","message":"primary_color must be #rrggbb"}}`)
		case r.URL.Path == "/v1/terrascale/email/branding":
			io.WriteString(w, `{"version":7,"updated_by":{"id":"u"},"branding":{"brand_name":"TerraScale","logo_width":120},"defaults":{"theme":"auto"}}`)
		case strings.HasPrefix(r.URL.Path, "/v1/terrascale/email/templates/"):
			io.WriteString(w, `{"kind":"sign_in_code","locale":"pt-BR","version":9,"overridden":true,"variables":["code"],"fields":{},"defaults":{}}`)
		case r.URL.Path == "/v1/terrascale/email/preview":
			io.WriteString(w, `{"subject":"Code","html":"<p>hi</p>","text":"hi"}`)
		}
	}))
	defer server.Close()
	backend := NewEmailBackend(server.URL+"/terrascale", "ts-dashboard", "s+cret:", false)
	ctx, actor := t.Context(), emailActor{ID: "usr_1", Email: "admin@example.test"}

	document, err := backend.Branding(ctx, "terrascale")
	if err != nil || document.Version != 7 || document.Branding.BrandName != "TerraScale" || document.Defaults.Theme != "auto" {
		t.Fatalf("Branding = %+v, %v", document, err)
	}
	if _, err := backend.SaveBranding(ctx, "terrascale", brandingUpdate{ExpectedVersion: 7, Actor: actor, Branding: emailBranding{BrandName: "Acme", LogoURL: "https://acme.test/logo.png", LogoWidth: 120, LogoHeight: 32}}); err != nil {
		t.Fatal(err)
	}
	if _, err := backend.SaveTemplate(ctx, "terrascale", "sign_in_code", "pt-BR", templateUpdate{ExpectedVersion: 8, Actor: actor, Fields: emailFields{Subject: "Oi"}}); err != nil {
		t.Fatal(err)
	}
	if _, err := backend.SaveTemplate(ctx, "terrascale", "sign_in_code", "pt-BR", templateUpdate{ExpectedVersion: 9, Actor: actor}); err != nil {
		t.Fatal(err)
	}
	if rendered, err := backend.Preview(ctx, "terrascale", emailPreview{Kind: "sign_in_code", Locale: "en", ColorScheme: "dark"}); err != nil || rendered.HTML != "<p>hi</p>" {
		t.Fatalf("Preview = %+v, %v", rendered, err)
	}
	want := []call{
		{"GET", "/v1/terrascale/email/branding", "", ""},
		{"PUT", "/v1/terrascale/email/branding", "", `{"expected_version":7,"actor":{"id":"usr_1","email":"admin@example.test"},"branding":{"brand_name":"Acme","logo_url":"https://acme.test/logo.png","logo_width":120,"logo_height":32}}`},
		{"PUT", "/v1/terrascale/email/templates/sign_in_code/pt-BR", "", `{"expected_version":8,"actor":{"id":"usr_1","email":"admin@example.test"},"fields":{"subject":"Oi"}}`},
		{"PUT", "/v1/terrascale/email/templates/sign_in_code/pt-BR", "", `{"expected_version":9,"actor":{"id":"usr_1","email":"admin@example.test"},"fields":{}}`},
		{"POST", "/v1/terrascale/email/preview", "", `{"kind":"sign_in_code","locale":"en","color_scheme":"dark"}`},
	}
	if !slices.Equal(calls, want) {
		t.Errorf("calls =\n%v\nwant\n%v", calls, want)
	}

	status = http.StatusUnprocessableEntity
	if err := backend.SendTest(ctx, "terrascale", emailTest{}); err == nil || err.Error() != "primary_color must be #rrggbb" {
		t.Errorf("422 = %v, want ts-auth's message for the form", err)
	}
	for code, want := range map[int]error{http.StatusConflict: core.ErrInvalid, http.StatusNotFound: core.ErrNotFound, http.StatusBadGateway: core.ErrUnavailable} {
		status = code
		if err := backend.SendTest(ctx, "terrascale", emailTest{}); !errors.Is(err, want) {
			t.Errorf("%d = %v, want %v", code, err, want)
		}
	}
}

func TestEmailPreviewShowsDraftsOnlyInTheConsoleFrame(t *testing.T) {
	store := memstore.New()
	account, err := store.CreateAccount(t.Context(), core.User{Name: "Owner", Email: "preview-owner@example.test", PasswordHash: "unused"})
	if err != nil {
		t.Fatal(err)
	}
	sessions := session.New(store, session.Options{})
	mux := http.NewServeMux()
	Mount(mux, core.Deps{Store: store, Sessions: sessions, CSRF: csrf.New(sessions, csrf.Options{}), Tools: core.NewTools(store), Demo: true}, "https://auth.example.test", newMemoryEmails())
	signIn := httptest.NewRecorder()
	if err := sessions.Start(signIn, httptest.NewRequest(http.MethodGet, "/", nil), account.Principal()); err != nil {
		t.Fatal(err)
	}
	preview := func(frame bool) *httptest.ResponseRecorder {
		query := url.Values{"template": {"sign_in_code"}, "locale": {"en"}, "color_scheme": {"dark"}, "brand_name": {"<b>Phish</b>"}, "body": {"Click <script>x</script>"}}
		request := httptest.NewRequest(http.MethodGet, "/auth/emails/preview?"+query.Encode(), nil)
		for _, cookie := range signIn.Result().Cookies() {
			request.AddCookie(cookie)
		}
		if frame {
			request.Header.Set("Sec-Fetch-Dest", "iframe")
			request.Header.Set("Sec-Fetch-Site", "same-origin")
		}
		response := httptest.NewRecorder()
		mux.ServeHTTP(response, request)
		if response.Code != http.StatusOK || !strings.HasPrefix(response.Header().Get("Content-Security-Policy"), "sandbox;") {
			t.Fatalf("preview = %d with policy %q, want a sandboxed page", response.Code, response.Header().Get("Content-Security-Policy"))
		}
		return response
	}
	if body := preview(false).Body.String(); strings.Contains(body, "Phish") || !strings.Contains(body, "TerraScale") {
		t.Error("a top-level link must show the saved email, not the drafts in its URL")
	}
	body := preview(true).Body.String()
	if !strings.Contains(body, "&lt;b&gt;Phish&lt;/b&gt;") || strings.Contains(body, "<script>") || !strings.Contains(body, `class="dark"`) {
		t.Errorf("framed preview = %s\nwant the escaped drafts in dark", body)
	}
}

func TestEmailsDecodeIgnoresFieldsTheConsoleDoesNotEdit(t *testing.T) {
	var document brandingDocument
	if err := json.Unmarshal([]byte(`{"version":3,"updated_at":"2026-10-06T10:00:00+00:00","branding":{"brand_name":null,"logo_height":40},"defaults":{}}`), &document); err != nil || document.Version != 3 {
		t.Fatalf("decode = %+v, %v", document, err)
	}
}

func TestEmailLogoIsClearedOnlyAsAWhole(t *testing.T) {
	defaults := emailBranding{BrandName: "TerraScale", LogoURL: "https://auth.test/logo.png", LogoWidth: 140, LogoHeight: 32}
	same := defaults
	clearBrandingDefaults(&same, defaults)
	if same != (emailBranding{}) {
		t.Errorf("the default logo = %+v, want it all left to the default", same)
	}
	resized := defaults
	resized.LogoWidth = 100
	clearBrandingDefaults(&resized, defaults)
	if want := (emailBranding{LogoURL: defaults.LogoURL, LogoWidth: 100, LogoHeight: 32}); resized != want {
		t.Errorf("the default logo at another size = %+v, want the whole logo kept: %+v", resized, want)
	}
	if message := validateBranding(resized); message != "" {
		t.Errorf("kept logo fails validation: %s", message)
	}
}

func TestEmailDraftsKeepTheVersionTheyWereBasedOn(t *testing.T) {
	service, _, owner := newEmailService(t)
	ctx := t.Context()
	branding := emailBranding{BrandName: "Moved on"}
	if _, err := service.SaveEmails(ctx, owner, emailChange{Version: 1, Branding: &branding}); err != nil {
		t.Fatal(err)
	}
	account, err := service.deps.Store.SnapshotAs(ctx, owner)
	if err != nil {
		t.Fatal(err)
	}
	drafts := url.Values{"version": {"1"}, "brand_name": {"Stale draft"}, "subject": {"Hi"}}
	page, err := service.emailsPage(ctx, owner, account, url.Values{}, drafts)
	if err != nil || page.View.Version != 1 || page.View.Branding.BrandName != "Stale draft" || !page.Drafted {
		t.Fatalf("emailsPage = version %d, branding %q, %v; want the drafts at their own version 1", page.View.Version, page.View.Branding.BrandName, err)
	}
	stale := brandingFromForm(drafts)
	if _, err := service.SaveEmails(ctx, owner, emailChange{Version: page.View.Version, Branding: &stale}); err == nil || !strings.Contains(err.Error(), "changed since") {
		t.Errorf("saving the drafts again = %v, want the conflict to stand", err)
	}
}

func TestMemoryEmailRendersTheConfiguredLogo(t *testing.T) {
	branding := emailBranding{BrandName: "Acme", LogoURL: "https://acme.test/logo.png", LogoDarkURL: "https://acme.test/logo-dark.png", LogoWidth: 120, LogoHeight: 32}
	auto, err := renderMemoryEmail("magic_link", "en", "auto", branding, emailFields{})
	if err != nil || !strings.Contains(auto.HTML, `width="120" height="32"`) || !strings.Contains(auto.HTML, `media="(prefers-color-scheme: dark)"`) {
		t.Errorf("auto logo = %s, %v; want its size and the dark logo for dark readers", auto.HTML, err)
	}
	light, _ := renderMemoryEmail("magic_link", "en", "light", branding, emailFields{})
	if strings.Contains(light.HTML, "logo-dark.png") {
		t.Error("a light preview must not show the dark logo")
	}
	icon, _ := renderMemoryEmail("magic_link", "en", "light", emailBranding{BrandName: "Acme", IconURL: "https://acme.test/icon.png"}, emailFields{})
	if !strings.Contains(icon.HTML, `<img src="https://acme.test/icon.png"`) {
		t.Errorf("without a logo = %s, want the icon", icon.HTML)
	}
}
