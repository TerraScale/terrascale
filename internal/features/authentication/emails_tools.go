package authentication

import (
	"cmp"
	"context"
	"fmt"
	"slices"

	"terrascale.tech/dashboard/internal/core"
)

type emailInput struct {
	core.Scope
	Template string `json:"template,omitempty" jsonschema:"Email; sign_in_code when omitted."`
	Locale   string `json:"locale,omitempty" jsonschema:"Language; en when omitted."`
}

type emailPreviewInput struct {
	core.Scope
	Template    string `json:"template,omitempty" jsonschema:"Email; sign_in_code when omitted."`
	Locale      string `json:"locale,omitempty" jsonschema:"Language; en when omitted."`
	ColorScheme string `json:"colorScheme,omitempty" jsonschema:"light, dark, or auto (the branding's theme)."`
}

type emailBrandingInput struct {
	core.Scope
	Branding emailBranding `json:"branding,omitempty" jsonschema:"Branding fields to change; omitted fields keep their value."`
	Clear    []string      `json:"clear,omitempty" jsonschema:"Branding field names, such as logo_url or reply_to, to return to ts-auth's default. Clearing logo_url clears the whole logo."`
}

type emailTemplateInput struct {
	core.Scope
	Template string      `json:"template,omitempty" jsonschema:"Email; sign_in_code when omitted."`
	Locale   string      `json:"locale,omitempty" jsonschema:"Language; en when omitted."`
	Fields   emailFields `json:"fields,omitempty" jsonschema:"Fields to change; omitted fields keep their wording."`
	Clear    []string    `json:"clear,omitempty" jsonschema:"Field names (subject, preheader, heading, body, button, footnote) to return to the default wording."`
}

type savedEmails struct {
	Version int `json:"version" jsonschema:"Version of the whole email configuration after the change."`
}

type sentEmail struct {
	To string `json:"to"`
}

func mountEmailTools(tools *core.Tools, service Service) {
	enums := map[string][]string{"template": optionValues(emailKinds), "locale": optionValues(emailLocales)}
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_get_emails", Title: "Get authentication emails", ReadOnly: true, Enums: enums,
		Description: "Show the branding of the emails ts-auth sends for the project, which emails are customized, and one email's fields and defaults.",
	}, func(ctx context.Context, p core.Principal, in emailInput) (emailsView, error) {
		return service.Emails(ctx, p, cmp.Or(in.Template, "sign_in_code"), cmp.Or(in.Locale, "en"))
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_update_email_branding", Title: "Update email branding", Idempotent: true,
		Description: "Change the logo, colors, theme, sender, and footer of every email ts-auth sends. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in emailBrandingInput) (savedEmails, error) {
		document, err := service.Emails(ctx, p, "sign_in_code", "en")
		if err != nil {
			return savedEmails{}, err
		}
		branding := document.Branding
		overlay(brandingValues(&branding), brandingValues(&in.Branding))
		branding.LogoWidth, branding.LogoHeight = cmp.Or(in.Branding.LogoWidth, branding.LogoWidth), cmp.Or(in.Branding.LogoHeight, branding.LogoHeight)
		if err := resetFields(brandingNames, brandingValues(&branding), in.Clear); err != nil {
			return savedEmails{}, err
		}
		if slices.Contains(in.Clear, "logo_url") {
			branding.setLogo(emailLogo{})
		}
		version, err := service.SaveEmails(ctx, p, emailChange{Version: document.Version, Branding: &branding})
		return savedEmails{Version: version}, err
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_update_email_template", Title: "Update email template", Idempotent: true, Enums: enums,
		Description: "Change the subject, preheader, heading, body, button, or footnote of one email in one language. Fields may use the email's {{ variables }}. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in emailTemplateInput) (emailsView, error) {
		kind, locale := cmp.Or(in.Template, "sign_in_code"), cmp.Or(in.Locale, "en")
		current, err := service.Emails(ctx, p, kind, locale)
		if err != nil {
			return emailsView{}, err
		}
		fields := current.Template.Defaults
		overlay(fieldValues(&fields), fieldValues(&current.Template.Fields))
		overlay(fieldValues(&fields), fieldValues(&in.Fields))
		if err := resetFields(fieldNames, fieldValues(&fields), in.Clear); err != nil {
			return emailsView{}, err
		}
		if _, err := service.SaveEmails(ctx, p, emailChange{Version: current.Version, Kind: kind, Locale: locale, Fields: &fields}); err != nil {
			return emailsView{}, err
		}
		return service.Emails(ctx, p, kind, locale)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_reset_email_template", Title: "Reset email template", Destructive: true, Enums: enums,
		Description: "Drop the customized wording of one email in one language, back to ts-auth's default.",
	}, func(ctx context.Context, p core.Principal, in emailInput) (emailsView, error) {
		kind, locale := cmp.Or(in.Template, "sign_in_code"), cmp.Or(in.Locale, "en")
		current, err := service.Emails(ctx, p, kind, locale)
		if err != nil {
			return emailsView{}, err
		}
		if err := service.ResetEmailTemplate(ctx, p, current.Version, kind, locale); err != nil {
			return emailsView{}, err
		}
		return service.Emails(ctx, p, kind, locale)
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_preview_email", Title: "Preview authentication email", ReadOnly: true, Enums: enums,
		Description: "Render one saved email with sample values: its subject, HTML, and plain text.",
	}, func(ctx context.Context, p core.Principal, in emailPreviewInput) (renderedEmail, error) {
		return service.PreviewEmail(ctx, p, emailPreview{Kind: cmp.Or(in.Template, "sign_in_code"), Locale: cmp.Or(in.Locale, "en"), ColorScheme: in.ColorScheme})
	})
	core.AddTool(tools, core.ToolSpec{
		Name: "auth_send_test_email", Title: "Send test authentication email", Enums: enums,
		Description: "Send one saved email to the caller's own address. Requires an owner or admin role.",
	}, func(ctx context.Context, p core.Principal, in emailInput) (sentEmail, error) {
		to, err := service.SendTestEmail(ctx, p, cmp.Or(in.Template, "sign_in_code"), cmp.Or(in.Locale, "en"), nil, nil)
		return sentEmail{To: to}, err
	})
}

// fieldNames names fieldValues' fields as ts-auth does.
var fieldNames = []string{"subject", "preheader", "heading", "body", "button", "footnote"}

// resetFields empties the values with the given names, which then fall back to
// ts-auth's defaults.
func resetFields(names []string, values []*string, cleared []string) error {
	for _, name := range cleared {
		index := slices.Index(names, name)
		if index < 0 {
			return core.Invalid(fmt.Sprintf("%q is not a field that can be cleared", name))
		}
		*values[index] = ""
	}
	return nil
}

// overlay copies every non-empty value of changes over values.
func overlay(values, changes []*string) {
	for i, change := range changes {
		if *change != "" {
			*values[i] = *change
		}
	}
}

func optionValues(options []option) []string {
	values := make([]string, 0, len(options))
	for _, item := range options {
		values = append(values, item.Value)
	}
	return values
}
