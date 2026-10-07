package authentication

import (
	"bytes"
	"cmp"
	"context"
	"html/template"
	"slices"
	"strings"
	"sync"

	"terrascale.tech/dashboard/internal/core"
)

// memoryEmails stands in for ts-auth's email API in the demo: every project
// is its own instance, and test emails are recorded instead of sent.
type memoryEmails struct {
	mu       sync.Mutex
	versions map[string]int
	branding map[string]emailBranding
	fields   map[string]emailFields // instance/kind/locale overrides
	sent     []emailTest
}

func newMemoryEmails() *memoryEmails {
	return &memoryEmails{versions: map[string]int{}, branding: map[string]emailBranding{}, fields: map[string]emailFields{}}
}

var memoryBrandingDefaults = emailBranding{BrandName: "TerraScale", PrimaryColor: "#2563eb", AccentColor: "#3b82f6", Theme: "auto", ButtonStyle: "rounded"}

// emailVariables are the variables each email offers.
var emailVariables = map[string][]string{
	"sign_in_code":   {"app_name", "user.email", "code", "expires_in", "support_email"},
	"magic_link":     {"app_name", "user.email", "link", "expires_in", "support_email"},
	"verify_email":   {"app_name", "user.email", "code", "expires_in", "support_email"},
	"password_reset": {"app_name", "user.email", "code", "expires_in", "support_email"},
	"invitation":     {"app_name", "user.email", "link", "expires_in", "inviter", "organization", "support_email"},
	"mfa_code":       {"app_name", "user.email", "code", "expires_in", "support_email"},
	"new_device":     {"app_name", "user.email", "device", "location", "time", "support_email"},
}

// defaultEmails are the demo's default fields by email and locale.
var defaultEmails = map[string]map[string]emailFields{
	"sign_in_code": {
		"en":    {"Your {{ app_name }} sign-in code", "Your code expires in {{ expires_in }}.", "Sign in to {{ app_name }}", "Enter this code to sign in as {{ user.email }}.", "", "Didn't try to sign in? You can ignore this email; nobody can sign in without the code."},
		"pt-BR": {"Seu código de acesso ao {{ app_name }}", "O código expira em {{ expires_in }}.", "Entre no {{ app_name }}", "Digite este código para entrar como {{ user.email }}.", "", "Não foi você? Ignore este email; ninguém entra sem o código."},
	},
	"magic_link": {
		"en":    {"Sign in to {{ app_name }}", "The link expires in {{ expires_in }}.", "Sign in to {{ app_name }}", "Use the button below to sign in as {{ user.email }}.", "Sign in", "Didn't try to sign in? You can ignore this email."},
		"pt-BR": {"Entre no {{ app_name }}", "O link expira em {{ expires_in }}.", "Entre no {{ app_name }}", "Use o botão abaixo para entrar como {{ user.email }}.", "Entrar", "Não foi você? Ignore este email."},
	},
	"verify_email": {
		"en":    {"Verify your email for {{ app_name }}", "The code expires in {{ expires_in }}.", "Verify your email", "Enter this code to verify {{ user.email }}.", "", "Didn't create an account? You can ignore this email."},
		"pt-BR": {"Confirme seu email no {{ app_name }}", "O código expira em {{ expires_in }}.", "Confirme seu email", "Digite este código para confirmar {{ user.email }}.", "", "Não criou uma conta? Ignore este email."},
	},
	"password_reset": {
		"en":    {"Reset your {{ app_name }} password", "The code expires in {{ expires_in }}.", "Reset your password", "Enter this code to reset the password of {{ user.email }}.", "", "Didn't ask for this? Your password stays the same."},
		"pt-BR": {"Redefina sua senha do {{ app_name }}", "O código expira em {{ expires_in }}.", "Redefina sua senha", "Digite este código para redefinir a senha de {{ user.email }}.", "", "Não foi você? Sua senha continua a mesma."},
	},
	"invitation": {
		"en":    {"{{ inviter }} invited you to {{ organization }}", "Join {{ organization }} on {{ app_name }}.", "Join {{ organization }}", "{{ inviter }} invited {{ user.email }} to join {{ organization }} on {{ app_name }}.", "Accept invitation", "Not expecting this? You can ignore this email."},
		"pt-BR": {"{{ inviter }} convidou você para {{ organization }}", "Entre em {{ organization }} no {{ app_name }}.", "Entre em {{ organization }}", "{{ inviter }} convidou {{ user.email }} para entrar em {{ organization }} no {{ app_name }}.", "Aceitar convite", "Não esperava este convite? Ignore este email."},
	},
	"mfa_code": {
		"en":    {"Your {{ app_name }} verification code", "The code expires in {{ expires_in }}.", "Finish signing in", "Enter this code to finish signing in to {{ app_name }}.", "", "Didn't try to sign in? Change your password now."},
		"pt-BR": {"Seu código de verificação do {{ app_name }}", "O código expira em {{ expires_in }}.", "Conclua o acesso", "Digite este código para concluir o acesso ao {{ app_name }}.", "", "Não foi você? Troque sua senha agora."},
	},
	"new_device": {
		"en":    {"New sign-in to {{ app_name }}", "{{ device }} near {{ location }}.", "New sign-in", "{{ user.email }} signed in from {{ device }} near {{ location }} at {{ time }}.", "", "Wasn't you? Reset your password now."},
		"pt-BR": {"Novo acesso ao {{ app_name }}", "{{ device }} perto de {{ location }}.", "Novo acesso", "{{ user.email }} entrou pelo {{ device }} perto de {{ location }} em {{ time }}.", "", "Não foi você? Redefina sua senha agora."},
	},
}

func (m *memoryEmails) Tenant(project core.Project, _ bool) string { return project.ID }

func (m *memoryEmails) Branding(_ context.Context, tenant string) (brandingDocument, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	return brandingDocument{Version: m.version(tenant), Branding: m.branding[tenant], Defaults: memoryBrandingDefaults}, nil
}

func (m *memoryEmails) SaveBranding(_ context.Context, tenant string, update brandingUpdate) (brandingDocument, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.advance(tenant, update.ExpectedVersion); err != nil {
		return brandingDocument{}, err
	}
	m.branding[tenant] = update.Branding
	return brandingDocument{Version: m.version(tenant), Branding: update.Branding, Defaults: memoryBrandingDefaults}, nil
}

func (m *memoryEmails) Templates(_ context.Context, tenant string) (templateList, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	list := templateList{Version: m.version(tenant)}
	for _, kind := range emailKinds {
		for _, locale := range emailLocales {
			_, overridden := m.fields[tenant+"/"+kind.Value+"/"+locale.Value]
			list.Templates = append(list.Templates, templateSummary{Kind: kind.Value, Locale: locale.Value, Overridden: overridden, Variables: emailVariables[kind.Value]})
		}
	}
	return list, nil
}

func (m *memoryEmails) Template(_ context.Context, tenant, kind, locale string) (emailTemplate, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.template(tenant, kind, locale), nil
}

// template reads one email; the caller holds mu.
func (m *memoryEmails) template(tenant, kind, locale string) emailTemplate {
	fields, overridden := m.fields[tenant+"/"+kind+"/"+locale]
	return emailTemplate{
		Kind: kind, Locale: locale, Version: m.version(tenant), Overridden: overridden, Variables: emailVariables[kind],
		Fields: fields, Defaults: defaultEmails[kind][locale],
	}
}

func (m *memoryEmails) SaveTemplate(_ context.Context, tenant, kind, locale string, update templateUpdate) (emailTemplate, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if err := m.advance(tenant, update.ExpectedVersion); err != nil {
		return emailTemplate{}, err
	}
	if update.Fields == (emailFields{}) {
		delete(m.fields, tenant+"/"+kind+"/"+locale)
	} else {
		m.fields[tenant+"/"+kind+"/"+locale] = update.Fields
	}
	return m.template(tenant, kind, locale), nil
}

func (m *memoryEmails) Preview(_ context.Context, tenant string, preview emailPreview) (renderedEmail, error) {
	m.mu.Lock()
	branding, fields := m.branding[tenant], m.fields[tenant+"/"+preview.Kind+"/"+preview.Locale]
	m.mu.Unlock()
	if preview.Branding != nil {
		branding = *preview.Branding
	}
	if preview.Fields != nil {
		if message := validateFields(*preview.Fields, emailVariables[preview.Kind]); message != "" {
			return renderedEmail{}, formError(message)
		}
		fields = *preview.Fields
	}
	return renderMemoryEmail(preview.Kind, preview.Locale, preview.ColorScheme, branding, fields)
}

// SendTest checks drafts as ts-auth does, then records the email.
func (m *memoryEmails) SendTest(_ context.Context, _ string, test emailTest) error {
	if test.Fields != nil {
		if message := validateFields(*test.Fields, emailVariables[test.Kind]); message != "" {
			return formError(message)
		}
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	m.sent = append(m.sent, test)
	return nil
}

// version is an instance's configuration version; it starts at 1.
func (m *memoryEmails) version(tenant string) int { return m.versions[tenant] + 1 }

// advance moves to the next version if expected is the current one.
func (m *memoryEmails) advance(tenant string, expected int) error {
	if expected != m.version(tenant) {
		return errEmailConflict
	}
	m.versions[tenant]++
	return nil
}

// memoryEmailPage is the demo's email layout. The colors are validated
// hex values, and the theme class picks light, dark, or the reader's mode.
var memoryEmailPage = template.Must(template.New("email").Parse(`<!doctype html>
<html lang="{{.Locale}}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="color-scheme" content="light dark"><title>{{.Subject}}</title><style>
body{margin:0;padding:32px 12px;background:#f4f4f5;color:#18181b;font:16px/1.6 -apple-system,"Segoe UI",Roboto,Helvetica,Arial,sans-serif}
.card{max-width:520px;margin:0 auto;padding:32px;border-radius:12px;background:#fff}
.brand{display:flex;align-items:center;gap:10px;margin:0 0 24px;font-weight:700;font-size:18px}
h1{margin:0 0 12px;font-size:22px;line-height:1.3}
.code{margin:16px 0;padding:16px;border-radius:8px;background:#f4f4f5;font:700 32px/1.2 ui-monospace,Menlo,Consolas,monospace;letter-spacing:6px;text-align:center}
.button{display:inline-block;margin:8px 0;padding:12px 22px;color:#fff;font-weight:600;text-decoration:none}
.rounded{border-radius:8px}.pill{border-radius:999px}.square{border-radius:0}
.note{color:#71717a;font-size:14px}
.footer{max-width:520px;margin:16px auto 0;color:#71717a;font-size:13px;text-align:center}
.footer a{color:inherit}
.dark{background:#09090b;color:#f4f4f5}.dark .card{background:#18181b}.dark .code{background:#27272a}
@media (prefers-color-scheme:dark){.auto{background:#09090b;color:#f4f4f5}.auto .card{background:#18181b}.auto .code{background:#27272a}}
</style></head><body class="{{.Scheme}}"><span hidden>{{.Preheader}}</span><div class="card">
<p class="brand">{{with .Branding.LogoURL}}<picture>{{if $.DarkLogo}}<source srcset="{{$.DarkLogo}}" media="{{$.DarkMedia}}">{{end}}<img src="{{.}}" width="{{$.Branding.LogoWidth}}" height="{{$.Branding.LogoHeight}}" alt="{{$.Branding.BrandName}}"></picture>{{else}}{{with .Branding.IconURL}}<img src="{{.}}" width="36" height="36" alt="">{{end}}<span>{{.Branding.BrandName}}</span>{{end}}</p>
<h1>{{.Heading}}</h1>
{{range .Body}}<p>{{.}}</p>{{end}}
{{if .Code}}<p class="code" style="color:{{.Branding.AccentColor}}">{{.Code}}</p>{{end}}
{{if .Button}}<p><a class="button {{.Branding.ButtonStyle}}" style="background:{{.Branding.PrimaryColor}}" href="https://example.com/continue">{{.Button}}</a></p>{{end}}
{{with .Expiry}}<p class="note">{{.}}</p>{{end}}
<p class="note">{{.Footnote}}</p>
</div><div class="footer">{{with .Branding.FooterText}}<p>{{.}}</p>{{end}}{{with .Branding.SupportEmail}}<p><a href="mailto:{{.}}">{{.}}</a></p>{{end}}{{with .Branding.SupportURL}}<p><a href="{{.}}">{{.}}</a></p>{{end}}</div></body></html>`))

func renderMemoryEmail(kind, locale, scheme string, branding emailBranding, fields emailFields) (renderedEmail, error) {
	defaultBranding, defaultFields := memoryBrandingDefaults, defaultEmails[kind][locale]
	for i, value := range brandingValues(&branding) {
		*value = cmp.Or(*value, *brandingValues(&defaultBranding)[i])
	}
	for i, value := range fieldValues(&fields) {
		*value = cmp.Or(*value, *fieldValues(&defaultFields)[i])
	}
	values := map[string]string{
		"app_name": branding.BrandName, "user.email": "ada@example.com", "code": "123456", "expires_in": "10 minutes",
		"link": "https://example.com/continue", "inviter": "Grace Hopper", "organization": "Acme Inc.",
		"device": "Firefox on Linux", "location": "São Paulo, Brazil", "time": "6 Oct 2026, 14:03 UTC", "support_email": branding.SupportEmail,
	}
	if locale == "pt-BR" {
		values["expires_in"] = "10 minutos"
	}
	fill := func(text string) string {
		return placeholderPattern.ReplaceAllStringFunc(text, func(match string) string {
			return values[placeholderPattern.FindStringSubmatch(match)[1]]
		})
	}
	var body []string
	for paragraph := range strings.SplitSeq(fill(fields.Body), "\n\n") {
		if paragraph = strings.TrimSpace(paragraph); paragraph != "" {
			body = append(body, paragraph)
		}
	}
	if scheme == "light" || scheme == "dark" {
		branding.Theme = scheme
	}
	// The dark logo replaces the logo in dark mode, or when the reader's
	// mode is dark under the auto theme.
	darkMedia := map[string]string{"dark": "all", "auto": "(prefers-color-scheme: dark)"}[branding.Theme]
	if darkMedia == "" {
		branding.LogoDarkURL = ""
	}
	data := map[string]any{
		"Locale": locale, "Scheme": branding.Theme, "Branding": branding, "DarkLogo": branding.LogoDarkURL, "DarkMedia": darkMedia,
		"Subject": fill(fields.Subject), "Preheader": fill(fields.Preheader), "Heading": fill(fields.Heading), "Body": body,
		"Footnote": fill(fields.Footnote), "Button": "", "Code": "", "Expiry": "",
	}
	// The layout states the expiry itself, so customized wording cannot drop it.
	if slices.Contains(emailVariables[kind], "expires_in") {
		data["Expiry"] = map[string]string{"en": "It expires in ", "pt-BR": "Expira em "}[locale] + values["expires_in"] + "."
	}
	if slices.Contains(emailVariables[kind], "code") {
		data["Code"] = values["code"]
	}
	if slices.Contains(emailVariables[kind], "link") {
		data["Button"] = fill(fields.Button)
	}
	var html bytes.Buffer
	if err := memoryEmailPage.Execute(&html, data); err != nil {
		return renderedEmail{}, err
	}
	action := ""
	if data["Button"] != "" {
		action = data["Button"].(string) + ": " + values["link"]
	}
	text := strings.Join(slices.DeleteFunc(append(append([]string{fill(fields.Heading)}, body...), data["Code"].(string), action, data["Expiry"].(string), fill(fields.Footnote)), func(s string) bool { return s == "" }), "\n\n")
	return renderedEmail{Subject: fill(fields.Subject), HTML: html.String(), Text: text}, nil
}
