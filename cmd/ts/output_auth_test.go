package main

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

const jsonAuthID = "01M5N9000000000000000000AA"

func jsonAuthUser(t *testing.T, handler http.HandlerFunc) *tsauth.User {
	t.Helper()
	server := httptest.NewTLSServer(handler)
	t.Cleanup(server.Close)
	client, err := tsauth.New(tsauth.Config{Issuer: server.URL + "/terrascale", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	return client.WithToken("fixture-sign-in-token")
}

func TestJSONAccountAndProjectResults(t *testing.T) {
	t.Parallel()
	for _, command := range []string{"projects", "projects create", "whoami"} {
		t.Run(command, func(t *testing.T) {
			t.Parallel()
			u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				if strings.HasSuffix(r.URL.Path, "/organizations") {
					json.NewEncoder(w).Encode(tsauth.Page[tsauth.Organization]{Items: []tsauth.Organization{{ID: jsonAuthID, CreatedBy: jsonAuthID, Name: "own"}}})
				} else if r.Method == http.MethodPost {
					json.NewEncoder(w).Encode(tsauth.Project{ID: jsonAuthID, OrganizationID: jsonAuthID, CreatedBy: jsonAuthID, Name: "app"})
				} else {
					io.WriteString(w, `{"items":[],"next":null}`)
				}
			})
			var buf bytes.Buffer
			out := &jsonOutput{Writer: &buf}
			var err error
			switch command {
			case "projects":
				err = listProjects(t.Context(), u, out)
			case "projects create":
				err = createProject(t.Context(), u, jsonAuthID, "app", out)
			default:
				token := "header." + base64.RawURLEncoding.EncodeToString([]byte(`{"sub":"`+jsonAuthID+`","email":"fixture@example.test","access_token":"hidden"}`)) + ".signature"
				err = whoami(t.Context(), u, token, out)
			}
			if err != nil || !json.Valid(buf.Bytes()) || strings.Contains(buf.String(), "hidden") {
				t.Fatalf("JSON command failed: %v", err)
			}
			if command == "projects" && buf.String() != "[]\n" {
				t.Fatal("empty list is not []")
			}
			if command == "projects create" && !strings.Contains(buf.String(), `"name":"app"`) {
				t.Fatal("project metadata missing")
			}
			if command == "whoami" && !strings.Contains(buf.String(), `"email":"fixture@example.test"`) {
				t.Fatal("account metadata missing")
			}
		})
	}
}

func TestJSONKeySecretOnlyOnCreate(t *testing.T) {
	t.Parallel()
	for _, command := range []string{"keys create", "keys list", "keys revoke"} {
		t.Run(command, func(t *testing.T) {
			t.Parallel()
			u := jsonAuthUser(t, func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				key := tsauth.APIKey{ID: jsonAuthID, OrganizationID: jsonAuthID, ProjectID: jsonAuthID, CreatedBy: jsonAuthID, Name: "ci"}
				switch r.Method {
				case http.MethodPost:
					json.NewEncoder(w).Encode(tsauth.CreatedAPIKey{APIKey: key, Key: "one-time-fixture-secret"})
				case http.MethodDelete:
					w.WriteHeader(http.StatusNoContent)
				default:
					json.NewEncoder(w).Encode(tsauth.Page[tsauth.APIKey]{Items: []tsauth.APIKey{key}})
				}
			})
			var buf bytes.Buffer
			out := &jsonOutput{Writer: &buf}
			client, err := v1.New(v1.Config{BaseURL: "http://127.0.0.1", Token: "fixture.jwt.signature"})
			if err != nil {
				t.Fatal(err)
			}
			s := &session{tb: client, user: u, org: jsonAuthID, project: jsonAuthID}
			if command == "keys create" {
				err = createKey(t.Context(), s, tsauth.APIKeyRequest{Name: "ci"}, out)
			} else {
				err = s.run(t.Context(), command, []string{jsonAuthID}, out)
			}
			if err != nil || !json.Valid(buf.Bytes()) {
				t.Fatalf("key result failed: %v", err)
			}
			if strings.Contains(buf.String(), "one-time-fixture-secret") != (command == "keys create") {
				t.Fatal("secret exposure contract violated")
			}
		})
	}
}

func TestJSONLoginProgressFailureStopsBeforePolling(t *testing.T) {
	t.Parallel()
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(r.URL.Path, "/oauth/device") {
			t.Error("unexpected token request")
		}
		json.NewEncoder(w).Encode(tsauth.DeviceAuthorization{DeviceCode: "not-for-output", UserCode: "VISIBLE", VerificationURI: "https://" + r.Host + "/approve", ExpiresIn: 60})
	}))
	defer server.Close()
	d, err := tsauth.NewDevice(tsauth.DeviceConfig{Issuer: server.URL + "/terrascale", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	var buf bytes.Buffer
	err = login(t.Context(), d, &jsonOutput{Writer: &buf}, brokenPrompt{})
	if !errors.Is(err, io.ErrClosedPipe) || buf.Len() != 0 {
		t.Fatalf("login progress error = %v", err)
	}
}
