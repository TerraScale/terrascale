package authentication

import (
	"cmp"
	"context"
	"fmt"
	"log/slog"
	"net/mail"
	"slices"
	"strings"
	"time"
	"unicode/utf8"

	"terrascale.tech/dashboard/internal/core"
)

// The Emails section edits the branding and the per-locale templates of the
// transactional emails ts-auth sends: sign-in codes, magic links, and the
// rest of emailKinds. ts-auth stores, versions, and renders them, so the
// preview is the email users receive; the console reaches them through an
// EmailBackend. Field names follow ts-auth's email API (TS-583).

// emailBranding is the look and sender of every email an instance sends.
// Empty fields use ts-auth's defaults.
type emailBranding struct {
	BrandName    string `json:"brand_name,omitempty" jsonschema:"Shown as {{ app_name }}, up to 80 characters."`
	LogoURL      string `json:"logo_url,omitempty" jsonschema:"HTTPS PNG, JPEG, or GIF logo; email clients do not show SVG."`
	LogoDarkURL  string `json:"logo_dark_url,omitempty" jsonschema:"HTTPS logo for dark mode."`
	LogoWidth    int    `json:"logo_width,omitzero" jsonschema:"Logo width in CSS pixels, 16 to 400; required with a logo."`
	LogoHeight   int    `json:"logo_height,omitzero" jsonschema:"Logo height in CSS pixels, 16 to 120; required with a logo."`
	IconURL      string `json:"icon_url,omitempty" jsonschema:"Square HTTPS image used when there is no logo."`
	PrimaryColor string `json:"primary_color,omitempty" jsonschema:"#rrggbb button fill."`
	AccentColor  string `json:"accent_color,omitempty" jsonschema:"#rrggbb links and code box."`
	Theme        string `json:"theme,omitempty" jsonschema:"auto follows the reader's light or dark mode; light or dark forces one."`
	ButtonStyle  string `json:"button_style,omitempty" jsonschema:"rounded, pill, or square."`
	FooterText   string `json:"footer_text,omitempty" jsonschema:"Up to 500 characters, such as the company and postal address."`
	SupportEmail string `json:"support_email,omitempty"`
	SupportURL   string `json:"support_url,omitempty"`
	WebsiteURL   string `json:"website_url,omitempty"`
	SenderName   string `json:"sender_name,omitempty" jsonschema:"Display name of the From header, up to 80 characters without @, <, >, or quotes."`
	ReplyTo      string `json:"reply_to,omitempty"`
}

// emailFields are the editable parts of one email. Body is plain
// paragraphs separated by blank lines; the code box, the button, and the
// expiry line come from ts-auth's layout.
type emailFields struct {
	Subject   string `json:"subject,omitempty"`
	Preheader string `json:"preheader,omitempty" jsonschema:"Inbox preview text after the subject."`
	Heading   string `json:"heading,omitempty"`
	Body      string `json:"body,omitempty"`
	Button    string `json:"button,omitempty" jsonschema:"Label of the action button, in magic_link and invitation only."`
	Footnote  string `json:"footnote,omitempty" jsonschema:"Security note, such as what to do if the reader did not ask for the email."`
}

type emailActor struct {
	ID    string `json:"id"`
	Email string `json:"email"`
}

// brandingDocument is the saved branding, the defaults it falls back to,
// and the version of the instance's whole email configuration.
type brandingDocument struct {
	Version  int           `json:"version"`
	Branding emailBranding `json:"branding"`
	Defaults emailBranding `json:"defaults"`
}

type brandingUpdate struct {
	ExpectedVersion int           `json:"expected_version"`
	Actor           emailActor    `json:"actor"`
	Branding        emailBranding `json:"branding"`
}

type templateList struct {
	Version   int               `json:"version"`
	Templates []templateSummary `json:"templates"`
}

type templateSummary struct {
	Kind       string   `json:"kind"`
	Locale     string   `json:"locale"`
	Overridden bool     `json:"overridden"`
	Variables  []string `json:"variables"`
}

// emailTemplate is one email in one locale: its overrides and defaults.
type emailTemplate struct {
	Kind       string      `json:"kind"`
	Locale     string      `json:"locale"`
	Version    int         `json:"version"`
	Overridden bool        `json:"overridden"`
	Variables  []string    `json:"variables" jsonschema:"Variables the fields may use as {{ name }}."`
	Fields     emailFields `json:"fields" jsonschema:"Overrides; empty fields use the defaults."`
	Defaults   emailFields `json:"defaults"`
}

// templateUpdate saves a template's overrides; empty Fields reset it.
type templateUpdate struct {
	ExpectedVersion int         `json:"expected_version"`
	Actor           emailActor  `json:"actor"`
	Fields          emailFields `json:"fields"`
}

// emailPreview renders an email with sample values. Branding and Fields are
// unsaved drafts; nil uses what is saved.
type emailPreview struct {
	Kind        string         `json:"kind"`
	Locale      string         `json:"locale"`
	ColorScheme string         `json:"color_scheme"`
	Branding    *emailBranding `json:"branding,omitempty"`
	Fields      *emailFields   `json:"fields,omitempty"`
}

type renderedEmail struct {
	Subject string `json:"subject"`
	HTML    string `json:"html"`
	Text    string `json:"text"`
}

// emailTest sends an email, drafts included, to To.
type emailTest struct {
	Kind     string         `json:"kind"`
	Locale   string         `json:"locale"`
	To       string         `json:"to"`
	Actor    emailActor     `json:"actor"`
	Branding *emailBranding `json:"branding,omitempty"`
	Fields   *emailFields   `json:"fields,omitempty"`
}

// EmailBackend stores, renders, and sends an instance's emails: ts-auth in
// production, memory in the demo.
type EmailBackend interface {
	// Tenant names the ts-auth instance whose emails project edits, or ""
	// when it has none the principal may edit.
	Tenant(project core.Project, platformAdmin bool) string
	Branding(ctx context.Context, tenant string) (brandingDocument, error)
	SaveBranding(ctx context.Context, tenant string, update brandingUpdate) (brandingDocument, error)
	Templates(ctx context.Context, tenant string) (templateList, error)
	Template(ctx context.Context, tenant, kind, locale string) (emailTemplate, error)
	SaveTemplate(ctx context.Context, tenant, kind, locale string, update templateUpdate) (emailTemplate, error)
	Preview(ctx context.Context, tenant string, preview emailPreview) (renderedEmail, error)
	SendTest(ctx context.Context, tenant string, test emailTest) error
}

var (
	// emailKinds lists ts-auth's transactional emails in console order.
	emailKinds = []option{
		{"sign_in_code", "Sign-in code"}, {"magic_link", "Magic link"}, {"verify_email", "Email verification"},
		{"password_reset", "Password reset"}, {"invitation", "Invitation"}, {"mfa_code", "MFA code"},
		{"new_device", "New device alert"},
	}
	emailLocales = []option{{"en", "English"}, {"pt-BR", "Português (Brasil)"}}
	emailThemes  = []option{{"auto", "Follow the reader's setting"}, {"light", "Always light"}, {"dark", "Always dark"}}
	buttonStyles = []option{{"rounded", "Rounded"}, {"pill", "Pill"}, {"square", "Square"}}
)

// errEmailConflict is a change based on a configuration version that is no
// longer current.
var errEmailConflict = formError("These emails changed since you opened the page. Reload it and try again.")

// errNoEmailTenant is a project whose emails the principal cannot edit yet.
var errNoEmailTenant = fmt.Errorf("%w: this project's ts-auth instance does not send emails yet", core.ErrNotFound)

// emailsView is the Emails section: the branding, which templates are
// overridden, and one template in full.
type emailsView struct {
	Tenant    string            `json:"tenant" jsonschema:"ts-auth instance whose emails these are."`
	CanManage bool              `json:"canManage"`
	Version   int               `json:"version" jsonschema:"Version of the whole email configuration."`
	Branding  emailBranding     `json:"branding"`
	Defaults  emailBranding     `json:"brandingDefaults"`
	Templates []templateSummary `json:"templates"`
	Template  emailTemplate     `json:"template"`
}

// emailScope returns the instance whose emails p edits from the selected
// project, and the actor ts-auth records for p. manage requires owner or
// admin rights.
func (s Service) emailScope(ctx context.Context, p core.Principal, manage bool) (string, emailActor, bool, error) {
	if p.ProjectID == "" {
		return "", emailActor{}, false, core.ErrNotFound
	}
	account, err := s.deps.Store.SnapshotAs(ctx, p)
	if err != nil {
		return "", emailActor{}, false, err
	}
	org, err := account.OrganizationFor(p, p.OrganizationID)
	if err != nil {
		return "", emailActor{}, false, err
	}
	project, err := account.ProjectFor(p, org.ID, p.ProjectID)
	if err != nil {
		return "", emailActor{}, false, err
	}
	canManage := org.CanManage(p.UserID)
	if manage && !canManage {
		return "", emailActor{}, false, core.ErrForbidden
	}
	tenant := ""
	if s.emails != nil {
		tenant = s.emails.Tenant(project, account.User.PlatformAdmin)
	}
	if tenant == "" {
		return "", emailActor{}, false, errNoEmailTenant
	}
	return tenant, emailActor{ID: account.User.ID, Email: account.User.Email}, canManage, nil
}

// Emails returns the branding, the template list, and one template, all
// read at the same configuration version.
func (s Service) Emails(ctx context.Context, p core.Principal, kind, locale string) (emailsView, error) {
	if !knownEmail(kind, locale) {
		return emailsView{}, formError("Choose an email and a language.")
	}
	tenant, _, canManage, err := s.emailScope(ctx, p, false)
	if err != nil {
		return emailsView{}, err
	}
	for range 3 {
		branding, err := s.emails.Branding(ctx, tenant)
		if err != nil {
			return emailsView{}, err
		}
		list, err := s.emails.Templates(ctx, tenant)
		if err != nil {
			return emailsView{}, err
		}
		template, err := s.emails.Template(ctx, tenant, kind, locale)
		if err != nil {
			return emailsView{}, err
		}
		if branding.Version == list.Version && list.Version == template.Version {
			return emailsView{
				Tenant: tenant, CanManage: canManage, Version: template.Version, Branding: branding.Branding, Defaults: branding.Defaults,
				Templates: list.Templates, Template: template,
			}, nil
		}
	}
	return emailsView{}, fmt.Errorf("%w: the email configuration kept changing while it was read", core.ErrUnavailable)
}

// emailChange is one save of the Emails form: new branding, one email's
// fields, or both, based on the configuration version the editor read.
type emailChange struct {
	Version  int
	Branding *emailBranding
	Kind     string
	Locale   string
	Fields   *emailFields
}

// SaveEmails checks every part of change before writing any, including
// that change.Version is still current, then saves the parts that differ
// from what is stored. Fields equal to ts-auth's
// defaults are left to them, so an email whose fields all match drops its
// override. It returns the new configuration version.
func (s Service) SaveEmails(ctx context.Context, p core.Principal, change emailChange) (int, error) {
	tenant, actor, _, err := s.emailScope(ctx, p, true)
	if err != nil {
		return 0, err
	}
	var branding *emailBranding
	if change.Branding != nil {
		current, err := s.emails.Branding(ctx, tenant)
		if err != nil {
			return 0, err
		}
		if current.Version != change.Version {
			return 0, errEmailConflict
		}
		next, stored := normalizeBranding(*change.Branding), normalizeBranding(current.Branding)
		clearBrandingDefaults(&next, current.Defaults)
		clearBrandingDefaults(&stored, current.Defaults)
		if message := validateBranding(next); message != "" {
			return 0, formError(message)
		}
		if next != stored {
			branding = &next
		}
	}
	var fields *emailFields
	if change.Fields != nil {
		if !knownEmail(change.Kind, change.Locale) {
			return 0, formError("Choose an email and a language.")
		}
		current, err := s.emails.Template(ctx, tenant, change.Kind, change.Locale)
		if err != nil {
			return 0, err
		}
		if current.Version != change.Version {
			return 0, errEmailConflict
		}
		next, stored := trimFields(*change.Fields), trimFields(current.Fields)
		clearDefaults(fieldValues(&next), fieldValues(&current.Defaults))
		clearDefaults(fieldValues(&stored), fieldValues(&current.Defaults))
		if message := validateFields(next, current.Variables); message != "" {
			return 0, formError(message)
		}
		if next != stored {
			fields = &next
		}
	}
	version := change.Version
	if branding != nil {
		saved, err := s.emails.SaveBranding(ctx, tenant, brandingUpdate{ExpectedVersion: version, Actor: actor, Branding: *branding})
		if err != nil {
			return 0, err
		}
		version = saved.Version
		s.recordEmailEvent(ctx, p, "auth.emails.branding_saved", tenant)
	}
	if fields != nil {
		saved, err := s.emails.SaveTemplate(ctx, tenant, change.Kind, change.Locale, templateUpdate{ExpectedVersion: version, Actor: actor, Fields: *fields})
		if err != nil && branding != nil {
			message, _ := emailFailure(err)
			return version, formError("The branding was saved, but the template was not. " + cmp.Or(message, "Try again."))
		}
		if err != nil {
			return 0, err
		}
		version = saved.Version
		s.recordEmailEvent(ctx, p, "auth.emails.template_saved", change.Kind+"/"+change.Locale)
	}
	return version, nil
}

// ResetEmailTemplate drops the overrides of one email in one locale.
func (s Service) ResetEmailTemplate(ctx context.Context, p core.Principal, version int, kind, locale string) error {
	if !knownEmail(kind, locale) {
		return formError("Choose an email and a language.")
	}
	tenant, actor, _, err := s.emailScope(ctx, p, true)
	if err != nil {
		return err
	}
	if _, err := s.emails.SaveTemplate(ctx, tenant, kind, locale, templateUpdate{ExpectedVersion: version, Actor: actor}); err != nil {
		return err
	}
	s.recordEmailEvent(ctx, p, "auth.emails.template_reset", kind+"/"+locale)
	return nil
}

// PreviewEmail renders one email, with optional unsaved drafts.
func (s Service) PreviewEmail(ctx context.Context, p core.Principal, preview emailPreview) (renderedEmail, error) {
	if message := validatePreview(&preview.Kind, &preview.Locale, &preview.ColorScheme, preview.Branding, preview.Fields); message != "" {
		return renderedEmail{}, formError(message)
	}
	tenant, _, _, err := s.emailScope(ctx, p, false)
	if err != nil {
		return renderedEmail{}, err
	}
	return s.emails.Preview(ctx, tenant, preview)
}

// SendTestEmail sends one email, with optional drafts, to the principal's
// own address, so a test never reaches anyone else.
func (s Service) SendTestEmail(ctx context.Context, p core.Principal, kind, locale string, branding *emailBranding, fields *emailFields) (string, error) {
	scheme := "auto"
	if message := validatePreview(&kind, &locale, &scheme, branding, fields); message != "" {
		return "", formError(message)
	}
	tenant, actor, _, err := s.emailScope(ctx, p, true)
	if err != nil {
		return "", err
	}
	if err := s.emails.SendTest(ctx, tenant, emailTest{Kind: kind, Locale: locale, To: actor.Email, Actor: actor, Branding: branding, Fields: fields}); err != nil {
		return "", err
	}
	s.recordEmailEvent(ctx, p, "auth.emails.test_sent", kind+"/"+locale)
	return actor.Email, nil
}

// recordEmailEvent adds a change already made in ts-auth to the project's
// audit log. A failure is logged rather than returned: reporting a saved
// change or a sent email as failed would invite a duplicate, and ts-auth
// keeps its own record of the actor for every saved version.
func (s Service) recordEmailEvent(ctx context.Context, p core.Principal, action, target string) {
	if _, err := s.update(ctx, p, time.Now().UTC(), func(*core.AuthInstance) (string, string, error) {
		return action, target, nil
	}); err != nil {
		slog.ErrorContext(ctx, "could not record an email audit event", "action", action, "target", target, "error", err)
	}
}

// brandingValues and fieldValues list a value's fields, so normalizing and
// comparing them is one loop.
func brandingValues(b *emailBranding) []*string {
	return []*string{
		&b.BrandName, &b.LogoURL, &b.LogoDarkURL, &b.IconURL, &b.PrimaryColor, &b.AccentColor, &b.Theme, &b.ButtonStyle,
		&b.FooterText, &b.SupportEmail, &b.SupportURL, &b.WebsiteURL, &b.SenderName, &b.ReplyTo,
	}
}

func fieldValues(f *emailFields) []*string {
	return []*string{&f.Subject, &f.Preheader, &f.Heading, &f.Body, &f.Button, &f.Footnote}
}

// brandingNames names brandingValues' fields as ts-auth does.
var brandingNames = []string{
	"brand_name", "logo_url", "logo_dark_url", "icon_url", "primary_color", "accent_color", "theme", "button_style",
	"footer_text", "support_email", "support_url", "website_url", "sender_name", "reply_to",
}

// emailLogo is the logo's URLs and size, which ts-auth takes together.
type emailLogo struct {
	url, dark     string
	width, height int
}

func logoOf(b emailBranding) emailLogo {
	return emailLogo{b.LogoURL, b.LogoDarkURL, b.LogoWidth, b.LogoHeight}
}

func (b *emailBranding) setLogo(logo emailLogo) {
	b.LogoURL, b.LogoDarkURL, b.LogoWidth, b.LogoHeight = logo.url, logo.dark, logo.width, logo.height
}

// clearBrandingDefaults is clearDefaults for branding. The logo is cleared
// only as a whole, so a default logo URL never leaves its size behind.
func clearBrandingDefaults(b *emailBranding, defaults emailBranding) {
	logo := logoOf(*b)
	clearDefaults(brandingValues(b), brandingValues(&defaults))
	if logo == logoOf(defaults) {
		logo = emailLogo{}
	}
	b.setLogo(logo)
}

// clearDefaults empties every value equal to its default, so later changes
// to ts-auth's defaults still reach it.
func clearDefaults(values, defaults []*string) {
	for i, value := range values {
		if *value == *defaults[i] {
			*value = ""
		}
	}
}

func normalizeBranding(b emailBranding) emailBranding {
	for _, value := range brandingValues(&b) {
		*value = strings.TrimSpace(*value)
	}
	b.PrimaryColor, b.AccentColor = strings.ToLower(b.PrimaryColor), strings.ToLower(b.AccentColor)
	return b
}

func trimFields(f emailFields) emailFields {
	for _, value := range fieldValues(&f) {
		*value = strings.TrimSpace(strings.ReplaceAll(*value, "\r\n", "\n"))
	}
	return f
}

// validateBranding mirrors ts-auth's branding rules so the form reports a
// problem before the call. Header values must not break a line.
func validateBranding(b emailBranding) string {
	switch {
	case utf8.RuneCountInString(b.BrandName) > 80 || strings.ContainsAny(b.BrandName, "\r\n"):
		return "Keep the brand name to 80 characters on one line."
	case utf8.RuneCountInString(b.SenderName) > 80 || strings.ContainsAny(b.SenderName, "\r\n@<>\""):
		return "Keep the sender name to 80 characters on one line, without @, <, >, or quotes."
	case !optionalHTTPS(b.LogoURL) || !optionalHTTPS(b.LogoDarkURL) || !optionalHTTPS(b.IconURL) || !optionalHTTPS(b.SupportURL) || !optionalHTTPS(b.WebsiteURL):
		return "Logo, icon, support, and website links must be HTTPS URLs."
	case !optionalColor(b.PrimaryColor) || !optionalColor(b.AccentColor):
		return "Colors are six-digit hex values such as #2563eb."
	case b.Theme != "" && !hasOption(emailThemes, b.Theme):
		return "Choose a theme."
	case b.ButtonStyle != "" && !hasOption(buttonStyles, b.ButtonStyle):
		return "Choose a button style."
	case !optionalAddress(b.SupportEmail) || !optionalAddress(b.ReplyTo):
		return "Enter valid support and reply-to email addresses."
	case utf8.RuneCountInString(b.FooterText) > 500:
		return "Keep the footer to 500 characters."
	case b.LogoURL == "" && (b.LogoDarkURL != "" || b.LogoWidth != 0 || b.LogoHeight != 0):
		return "Add a logo URL before a dark mode logo or logo size."
	case b.LogoURL != "" && (b.LogoWidth < 16 || b.LogoWidth > 400 || b.LogoHeight < 16 || b.LogoHeight > 120):
		return "A logo needs its width (16 to 400) and height (16 to 120) in pixels."
	}
	return ""
}

// validateFields applies ts-auth's rules for an email's fields: lengths,
// the email's own {{ variables }} and no other template syntax, and a
// button only where there is a link. Without variables it checks lengths.
func validateFields(f emailFields, variables []string) string {
	for _, value := range fieldValues(&f) {
		if variables == nil {
			break // a draft for preview; ts-auth checks its variables
		}
		for _, match := range placeholderPattern.FindAllStringSubmatch(*value, -1) {
			if !slices.Contains(variables, match[1]) {
				return "{{ " + match[1] + " }} is not a variable of this email. Use: " + strings.Join(variables, ", ") + "."
			}
		}
		if rest := placeholderPattern.ReplaceAllString(*value, ""); strings.Contains(rest, "{{") || strings.Contains(rest, "}}") || strings.Contains(rest, "{%") || strings.Contains(rest, "%}") {
			return "Fields may only use {{ name }} variables."
		}
	}
	switch {
	case f.Button != "" && variables != nil && !slices.Contains(variables, "link"):
		return "Only emails with a link have a button."
	case strings.ContainsAny(f.Subject, "\r\n") || strings.ContainsAny(f.Preheader, "\r\n"):
		return "Keep the subject and preheader on one line."
	case utf8.RuneCountInString(f.Subject) > 200 || utf8.RuneCountInString(f.Preheader) > 200 || utf8.RuneCountInString(f.Heading) > 200:
		return "Keep the subject, preheader, and heading to 200 characters each."
	case utf8.RuneCountInString(f.Button) > 60:
		return "Keep the button label to 60 characters."
	case utf8.RuneCountInString(f.Footnote) > 500 || utf8.RuneCountInString(f.Body) > 2000:
		return "Keep the body to 2,000 characters and the footnote to 500."
	}
	return ""
}

// validatePreview checks a preview or test request, defaulting its color
// scheme, and normalizes its drafts.
func validatePreview(kind, locale, scheme *string, branding *emailBranding, fields *emailFields) string {
	if *scheme == "" {
		*scheme = "auto"
	}
	switch {
	case !knownEmail(*kind, *locale):
		return "Choose an email and a language."
	case !hasOption(emailThemes, *scheme):
		return "Choose a light or dark preview."
	}
	if branding != nil {
		*branding = normalizeBranding(*branding)
		if message := validateBranding(*branding); message != "" {
			return message
		}
	}
	if fields != nil {
		*fields = trimFields(*fields)
		return validateFields(*fields, nil)
	}
	return ""
}

func knownEmail(kind, locale string) bool {
	return hasOption(emailKinds, kind) && hasOption(emailLocales, locale)
}

func hasOption(options []option, value string) bool {
	return slices.ContainsFunc(options, func(o option) bool { return o.Value == value })
}

func optionalHTTPS(value string) bool { return value == "" || validHTTPSURL(value) }

func optionalColor(value string) bool { return value == "" || themeColorPattern.MatchString(value) }

func optionalAddress(value string) bool {
	if value == "" {
		return true
	}
	address, err := mail.ParseAddress(value)
	return err == nil && address.Address == value && len(value) <= 320
}
