package tsauth

import (
	"bytes"
	"encoding/json"
	"reflect"
	"strings"
)

// ResponseContractError reports a malformed or oversized auth response without
// retaining response values, which can include credentials. It is never retried.
type ResponseContractError struct{}

func (*ResponseContractError) Error() string {
	return "tsauth: response does not match the public contract"
}

func (*Page[T]) requiredAuthMembers() string { return "items" }

// Required members follow PlatformEndpoints, InvitationEndpoints and
// ApiKeyEndpoints. Nullable and optional properties remain compatible.
func requiredAuthMembers(t reflect.Type) string {
	model := reflect.New(t).Interface()
	if page, ok := model.(interface{ requiredAuthMembers() string }); ok {
		return page.requiredAuthMembers()
	}
	switch model.(type) {
	case *Organization:
		return "id name created_by created_at"
	case *Member:
		return "user_id email role joined_at"
	case *Project:
		return "id organization_id name created_by created_at role"
	case *ProjectMember:
		return "user_id email role granted_at"
	case *Invitation:
		return "id email role invited_by created_at expires_at status"
	case *CreatedInvitation:
		return "invitation token"
	case *IDName:
		return "id name"
	case *InvitationLookup:
		return "organization email role expires_at"
	case *AcceptedInvitation:
		return "organization_id role"
	case *OwnershipTransfer:
		return "from_user_id to_user_id created_at expires_at"
	case *APIKey:
		return "id name organization_id scopes created_by created_at"
	case *CreatedAPIKey:
		return "api_key key"
	case *AuditEvent:
		return "id actor action occurred_at"
	}
	return ""
}

func validateAuthResponse(data []byte, t reflect.Type) error {
	for t.Kind() == reflect.Pointer {
		t = t.Elem()
	}
	if t.Kind() == reflect.Slice {
		var items []json.RawMessage
		if json.Unmarshal(data, &items) != nil {
			return &ResponseContractError{}
		}
		for _, item := range items {
			if err := validateAuthResponse(item, t.Elem()); err != nil {
				return err
			}
		}
		return nil
	}
	if t.Kind() != reflect.Struct {
		return nil
	}
	required := requiredAuthMembers(t)
	if required == "" {
		return nil
	}
	var object map[string]json.RawMessage
	if json.Unmarshal(data, &object) != nil || object == nil {
		return &ResponseContractError{}
	}
	for _, name := range strings.Fields(required) {
		if raw := object[name]; len(raw) == 0 || bytes.Equal(bytes.TrimSpace(raw), []byte("null")) {
			return &ResponseContractError{}
		}
	}
	for i := 0; i < t.NumField(); i++ {
		field := t.Field(i)
		name, _, _ := strings.Cut(field.Tag.Get("json"), ",")
		raw := object[name]
		if len(raw) == 0 || bytes.Equal(bytes.TrimSpace(raw), []byte("null")) {
			continue
		}
		if err := validateAuthResponse(raw, field.Type); err != nil {
			return err
		}
	}
	return nil
}
