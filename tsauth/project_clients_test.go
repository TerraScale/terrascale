package tsauth_test

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"terrascale.tech/sdk/tsauth"
)

const oauthProject = "01ARZ3NDEKTSV4RRFFQ69G5FAV"
const oauthClientID = "01ARZ3NDEKTSV4RRFFQ69G5FAW"
const oauthClientJSON = `{"client_id":"` + oauthClientID + `","name":"Example","type":"confidential","redirect_uris":["https://example.test/callback"],"created_at":"2026-10-26T00:00:00Z"}`

func TestProjectOAuthClientsContract(t *testing.T) {
	var calls []string
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls = append(calls, r.Method+" "+r.URL.Path)
		if r.Header.Get("Authorization") != "Bearer fixture-sign-in" || r.URL.RawQuery != "" || r.Header.Get("Accept") != "application/json" {
			t.Error("wrong credential, media or query")
		}
		w.Header().Set("Content-Type", "application/json")
		switch r.Method {
		case http.MethodPost:
			var body map[string]any
			if json.NewDecoder(r.Body).Decode(&body) != nil || len(body) != 3 || body["type"] != "confidential" || body["name"] != "Example" || r.Header.Get("Content-Type") != "application/json" {
				t.Error("registration body does not match the runtime snake_case contract")
			}
			if redirects, ok := body["redirect_uris"].([]any); !ok || len(redirects) != 1 || redirects[0] != "https://example.test/callback" {
				t.Error("redirect registration changed")
			}
			w.WriteHeader(http.StatusCreated)
			fmt.Fprint(w, strings.TrimSuffix(oauthClientJSON, "}")+`,"client_secret":"one-time-fixture"}`)
		case http.MethodGet:
			if r.URL.Path == "/prefix/v1/terrascale/organizations" {
				fmt.Fprint(w, `{"items":[],"next":null}`)
			} else {
				fmt.Fprint(w, `{"items":[`+oauthClientJSON+`],"next":null}`)
			}
		case http.MethodDelete:
			w.WriteHeader(http.StatusNoContent)
		}
	}))
	defer server.Close()
	client, err := tsauth.New(tsauth.Config{Issuer: server.URL + "/prefix/terrascale"})
	if err != nil {
		t.Fatal(err)
	}
	u := client.WithToken("fixture-sign-in")
	created, err := u.CreateProjectOAuthClient(t.Context(), oauthProject, oauthRegistration())
	if err != nil {
		t.Fatal(err)
	}
	if created.ClientSecret != "one-time-fixture" || created.ClientID != oauthClientID {
		t.Fatal("creation result lost fields")
	}
	for _, format := range []string{"%v", "%+v", "%#v"} {
		if strings.Contains(fmt.Sprintf(format, created), created.ClientSecret) {
			t.Fatal("secret in diagnostics")
		}
	}
	page, err := u.ListProjectOAuthClients(t.Context(), oauthProject)
	if err != nil || len(page.Items) != 1 || page.Items[0].ClientSecret != "" || page.Next != "" {
		t.Fatalf("list contract: %v", err)
	}
	if err := u.DeleteProjectOAuthClient(t.Context(), oauthProject, oauthClientID); err != nil {
		t.Fatal(err)
	}
	if _, err := u.Organizations(t.Context(), tsauth.PageOptions{}); err != nil {
		t.Fatal(err)
	}
	want := []string{"POST /prefix/v1/" + oauthProject + "/clients", "GET /prefix/v1/" + oauthProject + "/clients", "DELETE /prefix/v1/" + oauthProject + "/clients/" + oauthClientID, "GET /prefix/v1/terrascale/organizations"}
	if fmt.Sprint(calls) != fmt.Sprint(want) {
		t.Fatalf("routes: %v", calls)
	}
}

func oauthRegistration() tsauth.ProjectOAuthClientRequest {
	return tsauth.ProjectOAuthClientRequest{Name: "Example", Type: "confidential", RedirectURIs: []string{"https://example.test/callback"}}
}

func TestProjectOAuthClientsRejectInvalidInput(t *testing.T) {
	client, _ := tsauth.New(tsauth.Config{Issuer: "https://auth.example.test/terrascale"})
	u := client.WithToken("fixture")
	for _, change := range []func(*tsauth.ProjectOAuthClientRequest){
		func(r *tsauth.ProjectOAuthClientRequest) { r.Name = "!" },
		func(r *tsauth.ProjectOAuthClientRequest) { r.Name = strings.Repeat("a", 129) },
		func(r *tsauth.ProjectOAuthClientRequest) { r.Type = "service" },
		func(r *tsauth.ProjectOAuthClientRequest) { r.RedirectURIs = nil },
		func(r *tsauth.ProjectOAuthClientRequest) { r.RedirectURIs = []string{"http://example.test/callback"} },
		func(r *tsauth.ProjectOAuthClientRequest) { r.RedirectURIs = []string{"https://example.test/#"} },
		func(r *tsauth.ProjectOAuthClientRequest) { r.RedirectURIs = []string{"/callback"} },
		func(r *tsauth.ProjectOAuthClientRequest) {
			r.RedirectURIs = []string{"https://example.test/" + strings.Repeat("a", 17000)}
		},
	} {
		r := oauthRegistration()
		change(&r)
		if _, err := u.CreateProjectOAuthClient(t.Context(), oauthProject, r); err == nil {
			t.Fatal("invalid registration accepted")
		}
	}
	if _, err := u.ListProjectOAuthClients(t.Context(), "../terrascale"); err == nil {
		t.Fatal("invalid project accepted")
	}
	if err := u.DeleteProjectOAuthClient(t.Context(), oauthProject, "bad"); err == nil {
		t.Fatal("invalid client accepted")
	}
}

func TestProjectOAuthClientsErrorsAndCancellation(t *testing.T) {
	for _, status := range []int{400, 401, 403, 404, 503} {
		t.Run(fmt.Sprint(status), func(t *testing.T) {
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
				calls++
				w.WriteHeader(status)
				fmt.Fprint(w, `{"error":{"code":"forbidden"}}`)
			}))
			defer server.Close()
			client, _ := tsauth.New(tsauth.Config{Issuer: server.URL + "/terrascale"})
			_, err := client.WithToken("fixture").CreateProjectOAuthClient(t.Context(), oauthProject, oauthRegistration())
			var api *tsauth.APIError
			if !errors.As(err, &api) || api.Status != status || calls != 1 {
				t.Fatalf("error/replay: %v, calls %d", err, calls)
			}
		})
	}
	client, _ := tsauth.New(tsauth.Config{Issuer: "https://auth.example.test/terrascale"})
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := client.WithToken("fixture").ListProjectOAuthClients(ctx, oauthProject); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation: %v", err)
	}
}

func TestProjectOAuthClientsMalformedResponse(t *testing.T) {
	for _, response := range []string{`null`, `{}`, `{"items":null}`, `{"items":[{}]}`, `{"items":[` + strings.ReplaceAll(oauthClientJSON, oauthClientID, "not-ulid") + `]}`} {
		server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) { fmt.Fprint(w, response) }))
		client, _ := tsauth.New(tsauth.Config{Issuer: server.URL + "/terrascale"})
		_, err := client.WithToken("fixture").ListProjectOAuthClients(t.Context(), oauthProject)
		server.Close()
		var malformed *tsauth.ResponseContractError
		if !errors.As(err, &malformed) {
			t.Fatalf("malformed contract accepted: %v", err)
		}
	}
}
