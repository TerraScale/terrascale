package core

import (
	"context"
	"errors"
	"fmt"
	"testing"
)

func TestInvalidReason(t *testing.T) {
	rejected := Invalid("Choose a database in this project.")
	if !errors.Is(rejected, ErrInvalid) || rejected.Error() != "invalid input: Choose a database in this project." {
		t.Fatalf("Invalid = %q, want an ErrInvalid naming the reason", rejected)
	}
	for _, tc := range []struct {
		err    error
		reason string
		ok     bool
	}{
		{err: rejected, reason: "Choose a database in this project.", ok: true},
		{err: fmt.Errorf("attach: %w", rejected), reason: "Choose a database in this project.", ok: true},
		{err: ErrInvalid},
		{err: ErrConflict},
	} {
		if reason, ok := InvalidReason(tc.err); reason != tc.reason || ok != tc.ok {
			t.Errorf("InvalidReason(%q) = %q, %v; want %q, %v", tc.err, reason, ok, tc.reason, tc.ok)
		}
	}
}

// TestRouteGuards refuses, at registration, platform API routes that could
// act outside the organization their path names or never match a request.
func TestRouteGuards(t *testing.T) {
	type unscoped struct {
		Name string `json:"name"`
	}
	add := func(spec ToolSpec, scopedInput bool) (panicked bool) {
		defer func() { panicked = recover() != nil }()
		spec.Name, spec.Title, spec.Description = "routed", "Routed", "A routed tool."
		if scopedInput {
			AddTool(NewTools(nil), spec, func(context.Context, Principal, Scope) (struct{}, error) { return struct{}{}, nil })
		} else {
			AddTool(NewTools(nil), spec, func(context.Context, Principal, unscoped) (struct{}, error) { return struct{}{}, nil })
		}
		return false
	}
	const route = "GET /v1/organizations/{organizationId}/billing"
	for _, tc := range []struct {
		name   string
		spec   ToolSpec
		scoped bool
		panics bool
	}{
		{"valid", ToolSpec{Route: route, RouteScope: "billing:read"}, true, false},
		{"project wildcard", ToolSpec{Route: "GET /v1/organizations/{organizationId}/projects/{projectId}/usage", RouteScope: "usage:read"}, true, false},
		{"no scope", ToolSpec{Route: route}, true, true},
		{"administration tool", ToolSpec{Route: route, RouteScope: "billing:read", Admin: true}, true, true},
		{"input without Scope", ToolSpec{Route: route, RouteScope: "billing:read"}, false, true},
		{"no organization wildcard", ToolSpec{Route: "GET /v1/plans", RouteScope: "billing:read"}, true, true},
		{"misspelled organization wildcard", ToolSpec{Route: "GET /v1/organizations/{organizationID}/billing", RouteScope: "billing:read"}, true, true},
		{"wildcard naming no input", ToolSpec{Route: "GET /v1/organizations/{organizationId}/databases/{databaseId}", RouteScope: "billing:read"}, true, true},
	} {
		if panicked := add(tc.spec, tc.scoped); panicked != tc.panics {
			t.Errorf("%s: panicked = %v, want %v", tc.name, panicked, tc.panics)
		}
	}
}
