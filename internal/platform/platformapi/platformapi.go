// Package platformapi serves the platform REST API on
// platform.terrascale.tech: the MCP tools that declare a Route, as JSON over
// HTTP, and their OpenAPI document. Identity (organizations, projects,
// members, keys) is ts-auth's API and data is TerraBase's; this API covers
// what the platform owns: plans, subscriptions, billing and spending caps.
//
// It is a resource server under ts-auth's docs/platform-contract.md (§6.4):
// callers present an organization or project token whose aud includes
// Audience, and it checks the token's organization, project and scopes.
package platformapi

import (
	"bytes"
	"context"
	"encoding/json/v2"
	"errors"
	"io"
	"log/slog"
	"maps"
	"net/http"
	"slices"
	"strconv"
	"strings"

	"github.com/google/jsonschema-go/jsonschema"
	"terrascale.tech/dashboard/internal/core"
)

// Host is the platform API's own host.
const Host = "platform.terrascale.tech"

// Audience is the aud the platform API's tokens must include.
const Audience = "https://" + Host

// maxBody bounds a request body.
const maxBody = 64 << 10

// Grant is what a verified organization or project token allows: acting as
// Principal, in OrganizationID and, for a project token, ProjectID, with
// Scopes.
type Grant struct {
	Principal      core.Principal
	OrganizationID string
	ProjectID      string
	Scopes         []string
}

// Authenticate verifies a bearer token. It fails with core.ErrUnauthorized
// for a token that grants nothing; any other error means the token could not
// be checked.
type Authenticate func(ctx context.Context, token string) (Grant, error)

// New serves routes, authenticating every call but GET /openapi.json.
func New(routes []core.Route, authenticate Authenticate) http.Handler {
	mux := http.NewServeMux()
	document := openAPI(routes)
	mux.HandleFunc("GET /openapi.json", func(w http.ResponseWriter, _ *http.Request) {
		core.WriteJSON(w, http.StatusOK, document)
	})
	for _, route := range routes {
		mux.HandleFunc(route.Spec.Route, serve(route, authenticate))
	}
	mux.HandleFunc("/", func(w http.ResponseWriter, _ *http.Request) {
		writeError(w, http.StatusNotFound, "not_found", "no such operation")
	})
	return mux
}

func serve(route core.Route, authenticate Authenticate) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Cache-Control", "no-store")
		scheme, token, _ := strings.Cut(r.Header.Get("Authorization"), " ")
		token = strings.TrimSpace(token)
		if token == "" || !strings.EqualFold(scheme, "Bearer") {
			unauthorized(w, false)
			return
		}
		grant, err := authenticate(r.Context(), token)
		if errors.Is(err, core.ErrUnauthorized) {
			unauthorized(w, true)
			return
		}
		if err != nil {
			slog.Warn("platform API authentication unavailable", "error", err)
			writeError(w, http.StatusServiceUnavailable, "temporarily_unavailable", "authentication is temporarily unavailable")
			return
		}
		// An organization token reaches its organization's routes and a
		// project token its project's too; anything else does not exist
		// for the caller.
		if project := r.PathValue("projectId"); r.PathValue("organizationId") != grant.OrganizationID || (project != "" && project != grant.ProjectID) {
			writeError(w, http.StatusNotFound, "not_found", "not found")
			return
		}
		if !slices.Contains(grant.Scopes, route.Spec.RouteScope) {
			writeError(w, http.StatusForbidden, "insufficient_scope", "the token needs the "+route.Spec.RouteScope+" scope")
			return
		}
		args, err := arguments(w, r, route.Input)
		if err != nil {
			writeError(w, http.StatusBadRequest, "invalid_request", "the body must be a JSON object of at most 64 KiB")
			return
		}
		out, err := route.Call(r.Context(), grant.Principal, args)
		if err != nil {
			writeCallError(w, route.Spec.Name, err)
			return
		}
		core.WriteJSON(w, http.StatusOK, out)
	}
}

// arguments merges the JSON body, the query and the path wildcards, each
// overriding the one before, into the tool's arguments. Query values take
// the type their input property declares.
func arguments(w http.ResponseWriter, r *http.Request, input *jsonschema.Schema) (map[string]any, error) {
	args := map[string]any{}
	body, err := io.ReadAll(http.MaxBytesReader(w, r.Body, maxBody))
	if err != nil {
		return nil, err
	}
	if len(bytes.TrimSpace(body)) > 0 {
		if err := json.Unmarshal(body, &args); err != nil {
			return nil, err
		}
		if args == nil {
			return nil, errors.New("the body is null")
		}
	}
	for name, values := range r.URL.Query() {
		args[name] = queryValue(input.Properties[name], values[len(values)-1])
	}
	// The path alone names the organization and project, which the token
	// was checked against.
	delete(args, "organizationId")
	delete(args, "projectId")
	for name := range input.Properties {
		if value := r.PathValue(name); value != "" {
			args[name] = value
		}
	}
	return args, nil
}

func queryValue(property *jsonschema.Schema, raw string) any {
	if property == nil {
		return raw
	}
	types := append([]string{property.Type}, property.Types...)
	switch {
	case slices.Contains(types, "integer"):
		if n, err := strconv.ParseInt(raw, 10, 64); err == nil {
			return n
		}
	case slices.Contains(types, "number"):
		if n, err := strconv.ParseFloat(raw, 64); err == nil {
			return n
		}
	case slices.Contains(types, "boolean"):
		if b, err := strconv.ParseBool(raw); err == nil {
			return b
		}
	}
	return raw
}

// unauthorized answers 401 with a Bearer challenge, whose error names a
// token that was presented and refused (RFC 6750, section 3.1).
func unauthorized(w http.ResponseWriter, refused bool) {
	challenge := `Bearer realm="TerraScale platform"`
	if refused {
		challenge += `, error="invalid_token"`
	}
	w.Header().Set("WWW-Authenticate", challenge)
	writeError(w, http.StatusUnauthorized, "unauthorized", "a ts-auth organization or project token for this API is required")
}

// writeCallError answers a failed call. The access, conflict and invalid
// input errors carry messages written for callers, which the MCP tools show
// as well; anything else is logged and answered generically.
func writeCallError(w http.ResponseWriter, operation string, err error) {
	for _, known := range []struct {
		err    error
		status int
		code   string
	}{
		{core.ErrInvalid, http.StatusBadRequest, "invalid_request"},
		{core.ErrForbidden, http.StatusForbidden, "forbidden_role"},
		{core.ErrNotFound, http.StatusNotFound, "not_found"},
		{core.ErrConflict, http.StatusConflict, "conflict"},
	} {
		if errors.Is(err, known.err) {
			writeError(w, known.status, known.code, err.Error())
			return
		}
	}
	if errors.Is(err, core.ErrUnauthorized) {
		unauthorized(w, true)
		return
	}
	slog.Error("platform API operation failed", "operation", operation, "error", err)
	writeError(w, http.StatusServiceUnavailable, "temporarily_unavailable", "the operation could not be completed now")
}

// apiError is the {"error":{"code","message"}} envelope ts-auth's
// management API answers with too.
type apiError struct {
	Error struct {
		Code    string `json:"code"`
		Message string `json:"message"`
	} `json:"error"`
}

func writeError(w http.ResponseWriter, status int, code, message string) {
	var body apiError
	body.Error.Code, body.Error.Message = code, message
	core.WriteJSON(w, status, body)
}

// openAPI describes routes as an OpenAPI 3.1 document, whose schema dialect
// is the JSON Schema the tools' schemas are inferred in.
func openAPI(routes []core.Route) map[string]any {
	errorResponse := func(description string) map[string]any {
		return map[string]any{"description": description, "content": map[string]any{
			"application/json": map[string]any{"schema": map[string]string{"$ref": "#/components/schemas/Error"}},
		}}
	}
	errorSchema, err := jsonschema.For[apiError](nil)
	if err != nil {
		panic(err)
	}
	paths := map[string]map[string]any{}
	for _, route := range routes {
		method, path, _ := strings.Cut(route.Spec.Route, " ")
		operation := map[string]any{
			"operationId": route.Spec.Name, "summary": route.Spec.Title,
			"description": route.Spec.Description + " Needs the " + route.Spec.RouteScope + " scope.",
			"security":    []map[string][]string{{"tsAuth": {route.Spec.RouteScope}}},
			"responses": map[string]any{
				"200": map[string]any{"description": route.Spec.Title, "content": map[string]any{"application/json": map[string]any{"schema": route.Output}}},
				"400": errorResponse("The request is invalid."),
				"401": errorResponse("The access token is missing or invalid."),
				"403": errorResponse("The token lacks the operation's scope, or the caller's role does not allow it."),
				"404": errorResponse("The organization or project is not the token's, or does not exist."),
				"409": errorResponse("The change conflicts with the organization's current state."),
				"503": errorResponse("The operation could not be completed now."),
			},
		}
		body := *route.Input
		body.Properties = maps.Clone(route.Input.Properties)
		var parameters []map[string]any
		for _, name := range slices.Sorted(maps.Keys(route.Input.Properties)) {
			property := route.Input.Properties[name]
			in := "query"
			switch {
			case strings.Contains(path, "{"+name+"}"):
				in = "path"
			case name == "organizationId" || name == "projectId":
				// Only the path names the scope (arguments).
				delete(body.Properties, name)
				continue
			case method != http.MethodGet && method != http.MethodDelete:
				continue
			}
			delete(body.Properties, name)
			parameters = append(parameters, map[string]any{
				"name": name, "in": in, "required": in == "path" || slices.Contains(route.Input.Required, name),
				"description": property.Description, "schema": property,
			})
		}
		if parameters != nil {
			operation["parameters"] = parameters
		}
		if len(body.Properties) > 0 {
			operation["requestBody"] = map[string]any{"required": len(body.Required) > 0, "content": map[string]any{
				"application/json": map[string]any{"schema": &body},
			}}
		}
		if paths[path] == nil {
			paths[path] = map[string]any{}
		}
		paths[path][strings.ToLower(method)] = operation
	}
	return map[string]any{
		"openapi": "3.1.0",
		"info": map[string]any{
			"title": "TerraScale platform API", "version": "1.0.0",
			"description": "Plans, subscriptions, billing and spending caps of TerraScale organizations. Organizations, projects, members and API keys are ts-auth's API; databases, documents and usage are TerraBase's.",
		},
		"servers": []map[string]string{{"url": "https://" + Host}},
		"paths":   paths,
		"components": map[string]any{
			"schemas": map[string]any{"Error": errorSchema},
			"securitySchemes": map[string]any{"tsAuth": map[string]any{
				"type": "http", "scheme": "bearer", "bearerFormat": "JWT",
				"description": "A token of ts-auth's terrascale tenant (https://gru.auth.terrascale.tech/terrascale): an ES256 at+jwt whose aud includes " + Audience + ". Organization routes take an organization token; project routes take a project token of that project, since an organization token reaches no project route (ts-auth platform contract, section 6.4). Exchange a sign-in token for one with ts-auth's token exchange. Each operation lists the scope it needs.",
			}},
		},
	}
}
