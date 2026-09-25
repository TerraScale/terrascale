package authentication

import (
	"encoding/json/v2"
	"net"
	"net/mail"
	"net/url"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"unicode/utf8"
)

var (
	themeColorPattern   = regexp.MustCompile(`^#[0-9a-fA-F]{6}$`)
	slugPattern         = regexp.MustCompile(`^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$`)
	templateNamePattern = regexp.MustCompile(`^[A-Za-z0-9_-]{1,48}$`)
	tenantPattern       = regexp.MustCompile(`^[A-Za-z0-9.-]{1,128}$`)
	placeholderPattern  = regexp.MustCompile(`{{\s*([^{}]*?)\s*}}`)
)

// reservedClaims and templatePlaceholders mirror ts-auth's JWT template rules.
var (
	reservedClaims       = []string{"token_use", "iss", "exp", "iat", "nbf"}
	templatePlaceholders = []string{"user.id", "user.email", "user.username", "user.name", "user.email_verified", "org.id", "org.role"}
)

// validate checks the submitted values of a section form and returns the
// first problem as a message for the user.
func validate(current section, fields []field, values map[string]string) string {
	for _, item := range fields {
		if message := validateField(item, values[item.Key]); message != "" {
			return message
		}
	}
	if current.Key == "templates" && !templateNamePattern.MatchString(values["name"]) {
		return "Template names are up to 48 letters, numbers, dashes, and underscores."
	}
	return ""
}

// validateField checks one submitted value against its field rules.
func validateField(item field, value string) string {
	if item.Required && value == "" {
		return item.Label + " is required."
	}
	if len(value) > 16384 {
		return item.Label + " is too long."
	}
	if value == "" {
		return ""
	}
	switch item.Type {
	case typeEmail:
		address, err := mail.ParseAddress(value)
		if err != nil || address.Address != value || len(value) > 320 {
			return "Enter a valid email address."
		}
	case typeURL:
		if !validHTTPSURL(value) {
			return item.Label + " must be an absolute HTTPS URL."
		}
	case typeNumber:
		number, err := strconv.Atoi(value)
		if err != nil || number < 1 || number > 86400 {
			return item.Label + " must be between 1 and 86400."
		}
	case typeRole:
		if utf8.RuneCountInString(value) > 64 {
			return "Role must be at most 64 characters."
		}
	case typeSelect, typeChoices:
		for choice := range strings.SplitSeq(value, ",") {
			if !slices.ContainsFunc(item.Options, func(o option) bool { return o.Value == choice }) {
				return "Choose " + strings.ToLower(item.Label) + " from the list."
			}
		}
		return ""
	case typeText:
		if utf8.RuneCountInString(value) > 256 {
			return item.Label + " must be at most 256 characters."
		}
	}
	return validateKey(item.Key, value)
}

// validateKey applies the ts-auth rules attached to specific values.
func validateKey(key, value string) string {
	switch key {
	case "slug":
		if !slugPattern.MatchString(value) {
			return "Slug must be lowercase letters, numbers, and dashes."
		}
	case "domain":
		if _, ok := normalizeDomain(value); !ok {
			return "Enter a domain such as acme.com."
		}
	case "tenant":
		if !tenantPattern.MatchString(value) {
			return "Enter the directory (tenant) ID from Microsoft Entra ID."
		}
	case "scopes":
		scopes := strings.Fields(value)
		if !slices.Contains(scopes, "openid") || !slices.Contains(scopes, "email") {
			return "Scopes must include openid and email."
		}
	case "x509_certificate_pem":
		if !strings.HasPrefix(value, "-----BEGIN CERTIFICATE-----") || !strings.Contains(value, "-----END CERTIFICATE-----") {
			return "Paste the PEM certificate, including its BEGIN and END lines."
		}
	case "redirect_uris":
		for _, uri := range strings.Fields(value) {
			if !validRedirectURI(uri) {
				return "Redirect URIs must use HTTPS, or HTTP on localhost or a loopback address, without a fragment."
			}
		}
	case "claims":
		return validateClaims(value)
	}
	return ""
}

// validateClaims accepts a non-empty object of string claims that avoids
// reserved registered claims and uses only known placeholders.
func validateClaims(value string) string {
	var claims map[string]any
	if json.Unmarshal([]byte(value), &claims) != nil || len(claims) == 0 {
		return "Claims must be a non-empty JSON object."
	}
	for name, claim := range claims {
		if slices.Contains(reservedClaims, name) {
			return "The " + name + " claim is set by ts-auth and cannot be templated."
		}
		text, ok := claim.(string)
		if !ok {
			return "Claim values must be strings; use placeholders such as {{user.id}}."
		}
		for _, match := range placeholderPattern.FindAllStringSubmatch(text, -1) {
			if !slices.Contains(templatePlaceholders, match[1]) {
				return "Unknown placeholder {{" + match[1] + "}}."
			}
		}
	}
	return ""
}

func validHTTPSURL(value string) bool {
	parsed, err := url.Parse(value)
	return err == nil && parsed.IsAbs() && parsed.Scheme == "https" && parsed.Host != "" && parsed.User == nil
}

// validRedirectURI follows ts-auth: HTTPS, or HTTP on localhost or a
// loopback IP, with no fragment or credentials.
func validRedirectURI(value string) bool {
	parsed, err := url.Parse(value)
	if err != nil || !parsed.IsAbs() || parsed.Host == "" || parsed.User != nil || parsed.Fragment != "" || strings.Contains(value, "#") {
		return false
	}
	switch parsed.Scheme {
	case "https":
		return true
	case "http":
		host := parsed.Hostname()
		ip := net.ParseIP(host)
		return host == "localhost" || (ip != nil && ip.IsLoopback())
	}
	return false
}

func normalizeDomain(value string) (string, bool) {
	domain := strings.ToLower(strings.TrimSpace(value))
	if domain == "" || len(domain) > 253 || strings.HasPrefix(domain, ".") || strings.Contains(domain, "..") || !strings.Contains(domain, ".") {
		return "", false
	}
	for label := range strings.SplitSeq(domain, ".") {
		if label == "" || len(label) > 63 || strings.HasPrefix(label, "-") || strings.HasSuffix(label, "-") {
			return "", false
		}
		for _, r := range label {
			if (r < 'a' || r > 'z') && (r < '0' || r > '9') && r != '-' {
				return "", false
			}
		}
	}
	return domain, true
}
