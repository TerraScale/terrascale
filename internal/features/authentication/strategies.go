package authentication

import (
	"github.com/a-h/templ"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/ui/icons"
)

// strategy is one sign-in or multi-factor toggle backed by a ts-auth hosted
// flow (email passcodes, magic links, passwords, usernames, WebAuthn
// passkeys, TOTP, and recovery codes).
type strategy struct {
	Key         string
	Label       string
	Description string
	// FirstFactor strategies let a user sign in on their own; at least one
	// must stay enabled.
	FirstFactor bool
	// Requires names a strategy that must be enabled first.
	Requires string
	get      func(core.AuthSettings) bool
	set      func(*core.AuthSettings, bool)
}

// strategyGroup is one card of related strategies on a settings page.
type strategyGroup struct {
	ID          string
	Page        string
	Title       string
	Description string
	Icon        func(int) templ.Component
	Tone        string
	Strategies  []strategy
}

var strategyGroups = []strategyGroup{
	{
		ID: "email", Page: "sign-in", Title: "Email address", Icon: icons.Mail, Tone: "blue",
		Description: "Every user has an email address; ts-auth sends verification, reset, and sign-in messages to it.",
		Strategies: []strategy{
			{
				Key: "email_verification", Label: "Verify at sign-up",
				Description: "New users confirm their address from an emailed link before their identity is established.",
				get:         func(s core.AuthSettings) bool { return s.EmailVerification },
				set:         func(s *core.AuthSettings, on bool) { s.EmailVerification = on },
			},
			{
				Key: "email_passcode", Label: "Email verification code", FirstFactor: true,
				Description: "Sign in with a one-time code sent to the user's email address.",
				get:         func(s core.AuthSettings) bool { return s.EmailPasscode },
				set:         func(s *core.AuthSettings, on bool) { s.EmailPasscode = on },
			},
			{
				Key: "magic_link", Label: "Email magic link", FirstFactor: true,
				Description: "Sign in by opening a single-use link sent to the user's email address.",
				get:         func(s core.AuthSettings) bool { return s.MagicLink },
				set:         func(s *core.AuthSettings, on bool) { s.MagicLink = on },
			},
		},
	},
	{
		ID: "password", Page: "sign-in", Title: "Password", Icon: icons.LockKeyhole, Tone: "amber",
		Description: "Passwords are hashed with Argon2id; a change or reset invalidates sessions issued before it.",
		Strategies: []strategy{{
			Key: "password", Label: "Sign in with a password", FirstFactor: true,
			Description: "Users set a password at sign-up and can reset it from an emailed link.",
			get:         func(s core.AuthSettings) bool { return s.Password },
			set:         func(s *core.AuthSettings, on bool) { s.Password = on },
		}},
	},
	{
		ID: "username", Page: "sign-in", Title: "Username", Icon: icons.UserRound, Tone: "pink",
		Description: "A unique handle users can type instead of their email address.",
		Strategies: []strategy{{
			Key: "username", Label: "Allow usernames",
			Description: "Users pick a username at sign-up and can use it as their identifier.",
			get:         func(s core.AuthSettings) bool { return s.Username },
			set:         func(s *core.AuthSettings, on bool) { s.Username = on },
		}},
	},
	{
		ID: "passkeys", Page: "sign-in", Title: "Passkeys", Icon: icons.Fingerprint, Tone: "emerald",
		Description: "Phishing-resistant WebAuthn credentials stored on the user's devices.",
		Strategies: []strategy{{
			Key: "passkeys", Label: "Sign in with a passkey", FirstFactor: true,
			Description: "Discoverable credentials with sign-count clone detection; users manage them from their account page.",
			get:         func(s core.AuthSettings) bool { return s.Passkeys },
			set:         func(s *core.AuthSettings, on bool) { s.Passkeys = on },
		}},
	},
	{
		ID: "totp", Page: "mfa", Title: "Authenticator application", Icon: icons.Smartphone, Tone: "lime",
		Description: "Time-based one-time passwords (RFC 6238) from apps such as 1Password or Google Authenticator.",
		Strategies: []strategy{{
			Key: "totp", Label: "Authenticator app (TOTP)",
			Description: "Users enroll from their account page; enrolled users complete a step-up challenge at every sign-in.",
			get:         func(s core.AuthSettings) bool { return s.TOTP },
			set:         func(s *core.AuthSettings, on bool) { s.TOTP = on },
		}},
	},
	{
		ID: "recovery", Page: "mfa", Title: "Recovery codes", Icon: icons.KeyRound, Tone: "orange",
		Description: "One-time codes that stand in for a lost authenticator. Only their digests are stored.",
		Strategies: []strategy{{
			Key: "recovery_codes", Label: "Recovery codes", Requires: "totp",
			Description: "Issued when a user enrolls an authenticator app; each code works once.",
			get:         func(s core.AuthSettings) bool { return s.RecoveryCodes },
			set:         func(s *core.AuthSettings, on bool) { s.RecoveryCodes = on },
		}},
	},
}

// defaultSettings are the strategies a new ts-auth instance offers.
var defaultSettings = core.AuthSettings{
	EmailVerification: true, EmailPasscode: true, MagicLink: true, Password: true,
	Passkeys: true, TOTP: true, RecoveryCodes: true,
}

// effectiveSettings applies the defaults until an administrator saves.
func effectiveSettings(instance core.AuthInstance) core.AuthSettings {
	if instance.Settings.UpdatedAt.IsZero() {
		return defaultSettings
	}
	return instance.Settings
}

func groupsFor(page string) []strategyGroup {
	groups := make([]strategyGroup, 0, len(strategyGroups))
	for _, group := range strategyGroups {
		if group.Page == page {
			groups = append(groups, group)
		}
	}
	return groups
}

func findStrategy(key string) (strategy, strategyGroup, bool) {
	for _, group := range strategyGroups {
		for _, current := range group.Strategies {
			if current.Key == key {
				return current, group, true
			}
		}
	}
	return strategy{}, strategyGroup{}, false
}

// applyStrategy switches one strategy and keeps the settings coherent: at
// least one first factor stays enabled, and a dependent strategy follows the
// one it requires.
func applyStrategy(settings core.AuthSettings, key string, on bool) (core.AuthSettings, string) {
	target, _, ok := findStrategy(key)
	if !ok {
		return settings, "Unknown sign-in setting."
	}
	if on && target.Requires != "" {
		required, _, _ := findStrategy(target.Requires)
		if !required.get(settings) {
			return settings, target.Label + " requires " + required.Label + "."
		}
	}
	target.set(&settings, on)
	firstFactors := 0
	for _, group := range strategyGroups {
		for _, current := range group.Strategies {
			if !on && current.Requires == key {
				current.set(&settings, false)
			}
			if current.FirstFactor && current.get(settings) {
				firstFactors++
			}
		}
	}
	if firstFactors == 0 {
		return settings, "Keep at least one way to sign in: a password, an email code, a magic link, or passkeys."
	}
	return settings, ""
}

// enabledStrategies lists the enabled strategy labels on a page.
func enabledStrategies(settings core.AuthSettings, page string) []string {
	labels := make([]string, 0)
	for _, group := range groupsFor(page) {
		for _, current := range group.Strategies {
			if current.get(settings) {
				labels = append(labels, current.Label)
			}
		}
	}
	return labels
}
