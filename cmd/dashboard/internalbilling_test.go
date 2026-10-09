package main

import (
	"strings"
	"testing"

	"terrascale.tech/dashboard/internal/features/subscriptions"
)

func internalBillingArgs() []string {
	return []string{
		"--org", "01K71E8P000000000000000000", "--admin-email", "admin@example.test", "--base-plan", "business",
		"--spend-cap-cents", "12345", "--write-cents-per-million", "0", "--storage-cents-per-gib-month", "0",
	}
}

func TestParseInternalBillingConfigRequiresExplicitFlags(t *testing.T) {
	args := internalBillingArgs()
	want := subscriptions.InternalBillingConfig{OrganizationID: "01K71E8P000000000000000000", BasePlanKey: "business", SpendCapCents: 12345}
	config, email, err := parseInternalBillingConfig(args)
	if err != nil || config != want || email != "admin@example.test" {
		t.Fatalf("parsed explicit zero rates = %+v, %q, %v", config, email, err)
	}
	nonzero := append([]string{}, args...)
	nonzero[9], nonzero[11] = "321", "45"
	want.WriteCentsPerMillion, want.StorageCentsPerGiBMonth = 321, 45
	config, _, err = parseInternalBillingConfig(nonzero)
	if err != nil || config != want {
		t.Fatalf("parsed nonzero rates = %+v, %v, want %+v", config, err, want)
	}
	for i := 0; i < len(args); i += 2 {
		t.Run(args[i], func(t *testing.T) {
			missing := append(append([]string{}, args[:i]...), args[i+2:]...)
			if _, _, err := parseInternalBillingConfig(missing); err == nil || !strings.Contains(err.Error(), args[i]+" is required") {
				t.Fatalf("missing %s = %v, want required flag error", args[i], err)
			}
		})
	}
}

func TestParseInternalBillingConfigRejectsInvalidValues(t *testing.T) {
	for _, test := range []struct{ flag, value string }{
		{"--org", ""}, {"--org", " 01K71E8P000000000000000000"}, {"--admin-email", " "}, {"--base-plan", ""},
		{"--spend-cap-cents", "0"}, {"--spend-cap-cents", "-1"},
		{"--write-cents-per-million", "-1"}, {"--storage-cents-per-gib-month", "-1"},
		{"--write-cents-per-million", "not-a-number"}, {"--storage-cents-per-gib-month", "9223372036854775808"},
	} {
		t.Run(test.flag+"="+test.value, func(t *testing.T) {
			args := internalBillingArgs()
			for i := 0; i < len(args); i += 2 {
				if args[i] == test.flag {
					args[i+1] = test.value
				}
			}
			if _, _, err := parseInternalBillingConfig(args); err == nil {
				t.Fatal("invalid flag value accepted")
			}
		})
	}
	if _, _, err := parseInternalBillingConfig(append(internalBillingArgs(), "unexpected")); err == nil {
		t.Fatal("positional argument accepted")
	}
}
