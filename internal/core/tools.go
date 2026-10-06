package core

import (
	"cmp"
	"context"
	"encoding/json/v2"
	"errors"
	"fmt"
	"slices"

	"github.com/google/jsonschema-go/jsonschema"
	"github.com/modelcontextprotocol/go-sdk/auth"
	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// principalKey stores the authenticated principal in an MCP token's extras.
const principalKey = "terrascale.principal"

// invalidError is ErrInvalid with the reason the input was rejected.
type invalidError struct{ reason string }

func (e invalidError) Error() string { return ErrInvalid.Error() + ": " + e.reason }

func (e invalidError) Unwrap() error { return ErrInvalid }

// Invalid returns an ErrInvalid that explains which input was rejected, so
// HTTP responses and MCP tool errors can show the reason.
func Invalid(reason string) error {
	return invalidError{reason: reason}
}

// InvalidReason returns the reason of an error made by Invalid, or of the
// one it wraps, for pages that show only the reason.
func InvalidReason(err error) (string, bool) {
	invalid, ok := errors.AsType[invalidError](err)
	return invalid.reason, ok
}

// Scope selects the organization and project an MCP tool acts on. Tools are
// stateless, so every call names its scope; empty fields fall back to the
// caller's current dashboard selection.
type Scope struct {
	OrganizationID string `json:"organizationId,omitempty" jsonschema:"Organization ID to act on. Defaults to the caller's current dashboard organization."`
	ProjectID      string `json:"projectId,omitempty" jsonschema:"Project ID to act on. Defaults to the current project, or the organization's default project when organizationId is given."`
}

func (s Scope) toolScope() Scope { return s }

// scoped is implemented by tool inputs that embed Scope.
type scoped interface{ toolScope() Scope }

// ToolSpec describes one MCP tool. Read-only tools never change state;
// Destructive marks mutations that delete or irreversibly replace data.
type ToolSpec struct {
	Name        string
	Title       string
	Description string
	ReadOnly    bool
	Destructive bool
	Idempotent  bool
	// OpenWorld marks tools that reach external systems such as Stripe.
	OpenWorld bool
	// Admin lists the tool only for platform administrators.
	Admin bool
	// Enums restricts top-level string input properties, by JSON name, to
	// the listed values.
	Enums map[string][]string
	// Route also serves the tool on the platform REST API, as a ServeMux
	// pattern such as "GET /v1/organizations/{organizationId}/billing".
	// Its wildcards name input properties.
	Route string
	// RouteScope is the ts-auth scope, such as billing:read, a token needs
	// for Route.
	RouteScope string
}

// Route is a tool the platform REST API serves.
type Route struct {
	Spec          ToolSpec
	Input, Output *jsonschema.Schema
	// Call validates args against Input, narrows p to the scope they name,
	// and runs the tool. It returns the access and ErrInvalid errors
	// unphrased, for the API to map to statuses.
	Call func(ctx context.Context, p Principal, args map[string]any) (any, error)
}

// ToolHandler runs one MCP tool call as the authenticated principal, already
// narrowed to the scope the input selects.
type ToolHandler[In, Out any] func(ctx context.Context, p Principal, input In) (Out, error)

// Tools collects the MCP tools that feature slices expose next to their HTTP
// routes. The MCP platform registers them on one server per audience.
type Tools struct {
	store Store
	tools []registeredTool
}

type registeredTool struct {
	name     string
	admin    bool
	register func(*mcp.Server)
	route    *Route
}

func NewTools(store Store) *Tools {
	return &Tools{store: store}
}

// AddTool registers a typed tool. Input and output schemas are inferred from
// In and Out, and the SDK validates arguments before handler runs.
func AddTool[In, Out any](t *Tools, spec ToolSpec, handler ToolHandler[In, Out]) {
	tool := &mcp.Tool{
		Name: spec.Name, Title: spec.Title, Description: spec.Description,
		Annotations: &mcp.ToolAnnotations{
			Title: spec.Title, ReadOnlyHint: spec.ReadOnly, IdempotentHint: spec.ReadOnly || spec.Idempotent,
			DestructiveHint: new(!spec.ReadOnly && spec.Destructive), OpenWorldHint: new(spec.OpenWorld),
		},
	}
	if slices.ContainsFunc(t.tools, func(registered registeredTool) bool { return registered.name == spec.Name }) {
		panic("duplicate MCP tool " + spec.Name)
	}
	if len(spec.Enums) > 0 {
		tool.InputSchema = enumSchema[In](spec)
	}
	var route *Route
	if spec.Route != "" {
		route = newRoute(t, spec, handler)
	}
	t.tools = append(t.tools, registeredTool{name: spec.Name, admin: spec.Admin, route: route, register: func(server *mcp.Server) {
		mcp.AddTool(server, tool, func(ctx context.Context, req *mcp.CallToolRequest, input In) (*mcp.CallToolResult, Out, error) {
			var zero Out
			p, err := t.principal(ctx, req, input)
			if err != nil {
				return nil, zero, err
			}
			out, err := handler(ctx, p, input)
			if err != nil {
				return nil, zero, toolError(err)
			}
			return nil, out, nil
		})
	}})
}

// newRoute serves handler on the platform REST API with the schemas the MCP
// tool has, validating arguments as the SDK does.
func newRoute[In, Out any](t *Tools, spec ToolSpec, handler ToolHandler[In, Out]) *Route {
	if spec.Admin || spec.RouteScope == "" {
		panic("tool " + spec.Name + ": a platform API route needs a scope and a tool every user has")
	}
	input := enumSchema[In](spec)
	output, err := jsonschema.For[Out](nil)
	if err != nil {
		panic(fmt.Sprintf("tool %s: output schema: %v", spec.Name, err))
	}
	resolved, err := input.Resolve(nil)
	if err != nil {
		panic(fmt.Sprintf("tool %s: input schema: %v", spec.Name, err))
	}
	return &Route{Spec: spec, Input: input, Output: output, Call: func(ctx context.Context, p Principal, args map[string]any) (any, error) {
		if err := resolved.Validate(args); err != nil {
			return nil, Invalid(err.Error())
		}
		encoded, err := json.Marshal(args)
		if err != nil {
			return nil, err
		}
		var in In
		if err := json.Unmarshal(encoded, &in); err != nil {
			return nil, Invalid(err.Error())
		}
		var scope Scope
		if s, ok := any(in).(scoped); ok {
			scope = s.toolScope()
		}
		if p, err = t.store.ScopedPrincipal(ctx, p, scope); err != nil {
			return nil, err
		}
		return handler(ctx, p, in)
	}}
}

// Routes returns the tools the platform REST API serves, in name order.
func (t *Tools) Routes() []Route {
	var routes []Route
	for _, tool := range slices.SortedFunc(slices.Values(t.tools), func(a, b registeredTool) int { return cmp.Compare(a.name, b.name) }) {
		if tool.route != nil {
			routes = append(routes, *tool.route)
		}
	}
	return routes
}

// enumSchema infers In's input schema and applies spec.Enums to it. The SDK
// validates arguments against the returned schema.
func enumSchema[In any](spec ToolSpec) *jsonschema.Schema {
	schema, err := jsonschema.For[In](nil)
	if err != nil {
		panic(fmt.Sprintf("tool %s: input schema: %v", spec.Name, err))
	}
	for name, values := range spec.Enums {
		property, ok := schema.Properties[name]
		if !ok {
			panic(fmt.Sprintf("tool %s: enum for unknown input property %q", spec.Name, name))
		}
		property.Enum = make([]any, len(values))
		for i, value := range values {
			property.Enum[i] = value
		}
	}
	return schema
}

// Register adds every tool the audience may call to server in name order.
func (t *Tools) Register(server *mcp.Server, admin bool) {
	sorted := slices.SortedFunc(slices.Values(t.tools), func(a, b registeredTool) int { return cmp.Compare(a.name, b.name) })
	for _, tool := range sorted {
		if admin || !tool.admin {
			tool.register(server)
		}
	}
}

// TokenInfo wraps an authenticated principal for the MCP transport.
func TokenInfo(p Principal) *auth.TokenInfo {
	return &auth.TokenInfo{UserID: p.UserID, Extra: map[string]any{principalKey: p}}
}

// ToolPrincipal returns the principal a TokenInfo carries.
func ToolPrincipal(info *auth.TokenInfo) (Principal, bool) {
	if info == nil {
		return Principal{}, false
	}
	p, ok := info.Extra[principalKey].(Principal)
	return p, ok
}

func (t *Tools) principal(ctx context.Context, req *mcp.CallToolRequest, input any) (Principal, error) {
	p, ok := ToolPrincipal(req.Extra.TokenInfo)
	if !ok {
		return Principal{}, toolError(ErrUnauthorized)
	}
	var scope Scope
	if s, ok := input.(scoped); ok {
		scope = s.toolScope()
	}
	p, err := t.store.ScopedPrincipal(ctx, p, scope)
	if err != nil {
		return Principal{}, toolError(err)
	}
	return p, nil
}

// toolError phrases the shared access errors for an assistant. Errors that
// wrap them already carry their own reason and are kept.
func toolError(err error) error {
	switch err {
	case ErrUnauthorized:
		return errors.New("authentication required: the access token is no longer valid")
	case ErrForbidden:
		return errors.New("forbidden: your role does not allow this action in the selected scope")
	case ErrNotFound:
		return errors.New("not found in the selected organization and project")
	}
	return err
}

// ScopePrincipal narrows p to the organization and project scope names,
// checking membership against view, the principal's account with its live
// organizations. It never changes the account's saved dashboard selection.
func ScopePrincipal(view Account, p Principal, scope Scope) (Principal, error) {
	if err := AuthorizeAccount(view, p); err != nil {
		return Principal{}, err
	}
	organizationID, projectID := p.OrganizationID, p.ProjectID
	if scope.ProjectID != "" && scope.OrganizationID == "" {
		organizationID = ""
		for id, org := range view.Organizations {
			if _, ok := org.Projects[scope.ProjectID]; ok {
				organizationID = id
			}
		}
		if organizationID == "" {
			return Principal{}, fmt.Errorf("%w: project %q", ErrNotFound, scope.ProjectID)
		}
	}
	if scope.OrganizationID != "" && scope.OrganizationID != organizationID {
		org, err := view.OrganizationFor(p, scope.OrganizationID)
		if err != nil {
			return Principal{}, fmt.Errorf("%w: organization %q", err, scope.OrganizationID)
		}
		organizationID, projectID = org.ID, org.DefaultProjectID()
	}
	if scope.ProjectID != "" {
		projectID = scope.ProjectID
	}
	next := view.scopedPrincipal(organizationID, projectID)
	if err := AuthorizeAccount(view, next); err != nil {
		return Principal{}, fmt.Errorf("%w: project %q", err, projectID)
	}
	return next, nil
}
