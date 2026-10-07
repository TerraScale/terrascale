package authentication

import (
	"cmp"
	"context"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"net/url"
	"slices"
	"strconv"
	"strings"

	"terrascale.tech/dashboard/internal/core"
)

// emailsPage is the Emails section: the branding form, the selected email
// in the selected locale, and its preview.
type emailsPage struct {
	// Unavailable explains why there is nothing to edit.
	Unavailable string
	View        emailsView
	Kind        option
	Locale      option
	// Fields are the selected email's overrides over its defaults.
	Fields emailFields
	// Customized holds the emails overridden in any locale.
	Customized map[string]bool
	TestTo     string
	// Drafted shows a failed save's drafts, which the preview loads.
	Drafted bool
}

// previewPolicy keeps a rendered email inert: no scripts, forms, or
// navigation of the console, only its own styles and HTTPS images.
const previewPolicy = "sandbox; default-src 'none'; img-src https: data:; style-src 'unsafe-inline'; font-src https: data:; frame-ancestors 'self'"

func (s Service) emailsPage(ctx context.Context, p core.Principal, account core.Account, query, drafts url.Values) (emailsPage, error) {
	page := emailsPage{Kind: emailKinds[0], Locale: emailLocales[0], Customized: map[string]bool{}, TestTo: account.User.Email}
	if index := slices.IndexFunc(emailKinds, func(o option) bool { return o.Value == query.Get("template") }); index >= 0 {
		page.Kind = emailKinds[index]
	}
	if index := slices.IndexFunc(emailLocales, func(o option) bool { return o.Value == query.Get("locale") }); index >= 0 {
		page.Locale = emailLocales[index]
	}
	view, err := s.Emails(ctx, p, page.Kind.Value, page.Locale.Value)
	switch {
	case errors.Is(err, errNoEmailTenant):
		page.Unavailable = "This project's ts-auth instance does not send emails yet, so there is nothing to customize here."
		return page, nil
	case errors.Is(err, core.ErrUnavailable) || errors.Is(err, core.ErrNotFound):
		slog.WarnContext(ctx, "ts-auth email settings unavailable", "error", err)
		page.Unavailable = "ts-auth could not load the email settings. Try again in a moment."
		return page, nil
	case err != nil:
		return emailsPage{}, err
	}
	page.View, page.Fields = view, view.Template.Fields
	defaults := view.Template.Defaults
	for i, value := range fieldValues(&page.Fields) {
		*value = cmp.Or(*value, *fieldValues(&defaults)[i])
	}
	for _, template := range view.Templates {
		page.Customized[template.Kind] = page.Customized[template.Kind] || template.Overridden
	}
	if drafts != nil {
		// The drafts keep the version they were based on, so saving them
		// again cannot overwrite a change they never saw.
		page.View.Branding, page.Fields, page.Drafted = brandingFromForm(drafts), fieldsFromForm(drafts), true
		page.View.Version, _ = strconv.Atoi(drafts.Get("version"))
	}
	return page, nil
}

// saveEmails saves the Emails form, or resets the selected email. A save
// that fails shows the form again with its drafts, so nothing typed is lost.
func saveEmails(w http.ResponseWriter, r *http.Request, service Service) {
	p, ok := authorize(service.deps, w, r)
	if !ok {
		return
	}
	form, ctx := r.Form, r.Context()
	kind, locale := form.Get("template"), form.Get("locale")
	version, _ := strconv.Atoi(form.Get("version"))
	var err error
	if form.Get("intent") == "reset" {
		err = service.ResetEmailTemplate(ctx, p, version, kind, locale)
	} else {
		branding, fields := brandingFromForm(form), fieldsFromForm(form)
		var saved int
		saved, err = service.SaveEmails(ctx, p, emailChange{Version: version, Branding: &branding, Kind: kind, Locale: locale, Fields: &fields})
		if err != nil && saved != 0 {
			// The branding saved before the template failed: the drafts now
			// build on its version.
			form.Set("version", strconv.Itoa(saved))
		}
	}
	query := url.Values{"template": {kind}, "locale": {locale}}
	if message, failed := emailFailure(err); failed {
		if message == "" {
			writeError(w, err)
			return
		}
		query.Set("error", message)
		renderSection(w, r, service, "emails", query, form, http.StatusUnprocessableEntity)
		return
	}
	query.Set("saved", "1")
	http.Redirect(w, r, "/auth/emails?"+query.Encode(), http.StatusSeeOther)
}

// sendTestEmail sends the form's drafts to the signed-in user and reports
// the outcome in place, so the drafts stay in the form.
func sendTestEmail(w http.ResponseWriter, r *http.Request, service Service) {
	p, ok := authorize(service.deps, w, r)
	if !ok {
		return
	}
	branding, fields := brandingFromForm(r.Form), fieldsFromForm(r.Form)
	to, err := service.SendTestEmail(r.Context(), p, r.Form.Get("template"), r.Form.Get("locale"), &branding, &fields)
	status := emailTestStatus("success", "Test email sent to "+to+".")
	if message, failed := emailFailure(err); failed {
		if message == "" {
			writeError(w, err)
			return
		}
		status = emailTestStatus("danger", message)
	}
	var html strings.Builder
	if err := status.Render(r.Context(), &html); err != nil {
		http.Error(w, "could not render the test result", http.StatusInternalServerError)
		return
	}
	_ = service.deps.Events.PatchElements(w, r, html.String())
}

// emailFailure turns a failed email change into the message the page
// shows. It reports an empty message for errors the caller writes itself.
func emailFailure(err error) (string, bool) {
	var message formError
	switch {
	case err == nil:
		return "", false
	case errors.As(err, &message):
		return string(message), true
	case errors.Is(err, core.ErrUnavailable):
		return "ts-auth could not be reached. Try again in a moment.", true
	}
	return "", true
}

// previewEmail serves the rendered email into the console's preview frame.
func previewEmail(w http.ResponseWriter, r *http.Request, service Service) {
	p, _, ok := signedIn(service.deps, w, r)
	if !ok {
		return
	}
	query := r.URL.Query()
	preview := emailPreview{Kind: query.Get("template"), Locale: query.Get("locale"), ColorScheme: query.Get("color_scheme")}
	// Drafts render only into the console's own frame, so a crafted link
	// cannot show made-up content on the dashboard's origin.
	if query.Has("brand_name") && r.Header.Get("Sec-Fetch-Dest") == "iframe" && r.Header.Get("Sec-Fetch-Site") == "same-origin" {
		branding, fields := brandingFromForm(query), fieldsFromForm(query)
		preview.Branding, preview.Fields = &branding, &fields
	}
	rendered, err := service.PreviewEmail(r.Context(), p, preview)
	w.Header().Set("Content-Security-Policy", previewPolicy)
	w.Header().Set("Referrer-Policy", "no-referrer")
	w.Header().Set("Cache-Control", "private, no-store")
	if message, failed := emailFailure(err); failed {
		if message == "" {
			writeError(w, err)
			return
		}
		// The frame shows why the draft cannot render while it is typed;
		// the request itself did not fail.
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		_, _ = io.WriteString(w, "Preview unavailable: "+message)
		return
	}
	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_, _ = io.WriteString(w, rendered.HTML)
}

func brandingFromForm(form url.Values) emailBranding {
	return emailBranding{
		BrandName: form.Get("brand_name"), LogoURL: form.Get("logo_url"), LogoDarkURL: form.Get("logo_dark_url"), IconURL: form.Get("icon_url"),
		LogoWidth: pixels(form.Get("logo_width")), LogoHeight: pixels(form.Get("logo_height")),
		PrimaryColor: form.Get("primary_color"), AccentColor: form.Get("accent_color"), Theme: form.Get("theme"), ButtonStyle: form.Get("button_style"),
		FooterText: form.Get("footer_text"), SupportEmail: form.Get("support_email"), SupportURL: form.Get("support_url"),
		WebsiteURL: form.Get("website_url"), SenderName: form.Get("sender_name"), ReplyTo: form.Get("reply_to"),
	}
}

// pixels reads a logo dimension; anything but a whole number fails the
// branding's size check.
func pixels(value string) int {
	if value = strings.TrimSpace(value); value == "" {
		return 0
	}
	n, err := strconv.Atoi(value)
	if err != nil {
		return -1
	}
	return n
}

func fieldsFromForm(form url.Values) emailFields {
	return emailFields{
		Subject: form.Get("subject"), Preheader: form.Get("preheader"), Heading: form.Get("heading"),
		Body: form.Get("body"), Button: form.Get("button"), Footnote: form.Get("footnote"),
	}
}

// previewURL is the preview frame's first page: the saved email in light.
func (e emailsPage) previewURL() string {
	return "/auth/emails/preview?" + url.Values{"template": {e.Kind.Value}, "locale": {e.Locale.Value}, "color_scheme": {"light"}}.Encode()
}

func (e emailsPage) pageURL(kind, locale string) string {
	return "/auth/emails?" + url.Values{"template": {kind}, "locale": {locale}}.Encode()
}

// offers reports whether the selected email has the variable, such as the
// link its button opens.
func (e emailsPage) offers(variable string) bool {
	return slices.Contains(e.View.Template.Variables, variable)
}
