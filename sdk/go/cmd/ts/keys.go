package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/sdk/internal/entityid"
	"terrascale.tech/sdk/tsauth"
)

// Validate before opening credentials or making any authenticated request.
func parseKeyRotation(args []string) (string, time.Duration, error) {
	if len(args) != 2 {
		return "", 0, fmt.Errorf("expected keys rotate <id> <grace>: %w", errUsage)
	}
	if entityid.Check(args[0]) != nil {
		return "", 0, fmt.Errorf("key id must be a canonical ULID: %w", errUsage)
	}
	grace, err := time.ParseDuration(args[1])
	if err != nil || grace < 0 || grace > 24*time.Hour || grace%time.Second != 0 {
		return "", 0, fmt.Errorf("grace must be a whole-second duration from 0s to 24h, for example 30m: %w", errUsage)
	}
	return args[0], grace, nil
}

func rotateKey(ctx context.Context, s *session, id string, grace time.Duration, out, diagnostics io.Writer) error {
	key, err := s.user.RotateAPIKey(ctx, s.org, s.project, id, grace)
	if err != nil {
		return keyOperationError(err)
	}
	// A mismatched or malformed success can contain sensitive remote content.
	if key == nil || key.APIKey.ID != id || key.APIKey.OrganizationID != s.org || key.APIKey.ProjectID != s.project ||
		(grace > 0) != (key.APIKey.PreviousSecretExpiresAt != nil) ||
		len(key.Key) != 79 || !strings.HasPrefix(key.Key, id+"_") || strings.Trim(key.Key[27:], "0123456789abcdefghjkmnpqrstvwxyz") != "" {
		return errors.New("Rotation response did not match the key contract; inspect key state before retrying.")
	}
	if jsonMode(out) {
		return keyOutputError(emitJSON(out, key)) // The explicit command's single secret-bearing result.
	}
	previous := "immediately"
	if key.APIKey.PreviousSecretExpiresAt != nil {
		previous = key.APIKey.PreviousSecretExpiresAt.UTC().Format(time.RFC3339)
	}
	if _, err := fmt.Fprintf(diagnostics, "Rotated key %s. Previous secret ends: %s. Store stdout securely; the new secret cannot be retrieved again.\n", id, previous); err != nil {
		return keyOutputError(err)
	}
	_, err = fmt.Fprintln(out, key.Key)
	return keyOutputError(err)
}

// Preserve only source-confirmed codes/status, never remote messages or causes.
func keyOperationError(err error) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, context.Canceled) {
		return context.Canceled
	}
	if errors.Is(err, context.DeadlineExceeded) {
		return context.DeadlineExceeded
	}
	var api *tsauth.APIError
	if !errors.As(err, &api) {
		return errors.New("API key request failed; its outcome may be unknown. Inspect key state before retrying.")
	}
	code := tsauth.ErrorCode("http_" + strconv.Itoa(api.Status))
	message := "API key operation failed; verify the selected project and permissions."
	switch string(api.Code) {
	case "rotation_pending":
		code, message = api.Code, "Previous rotation grace is active; wait until previous_secret_expires_at before rotating again."
	case "authentication_conflict":
		code, message = api.Code, "Sign in again before issuing another API key secret."
	case "unauthorized", "forbidden_role", "not_found", "invalid_request", "rate_limited", "temporarily_unavailable":
		code = api.Code
		if text := http.StatusText(api.Status); text != "" {
			message = text
		}
	}
	return &tsauth.APIError{Status: api.Status, Code: code, Message: message, RetryAfter: api.RetryAfter}
}

func keyOutputError(err error) error {
	if err != nil {
		// Some writers include the bytes they rejected in their error text.
		return errors.New("API key output failed; if a secret was returned, it may be lost. Inspect key state before retrying.")
	}
	return nil
}
