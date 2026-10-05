// Package mcpserver serves the dashboard's MCP tools over stateless
// Streamable HTTP (MCP 2026-07-28). Every request authenticates with a
// personal access token and carries its own scope, so no MCP session or
// per-connection state exists on the server.
package mcpserver

import (
	"context"
	"errors"
	"net/http"
	"strings"

	"github.com/modelcontextprotocol/go-sdk/auth"
	"github.com/modelcontextprotocol/go-sdk/mcp"
	"terrascale.tech/dashboard/internal/core"
)

const serverVersion = "1.0.0"

const instructions = `TerraScale dashboard tools manage organizations, projects, members, databases, data, backups, credentials, authentication, billing, and settings.
Calls are stateless: pass organizationId and projectId to choose the scope, or omit them to use your current dashboard selection. Call get_context first to discover the IDs you can use.`

// New returns the MCP endpoint handler. Platform administrators see the
// administration tools in addition to the tools every user sees.
func New(store core.Store, tools *core.Tools) http.Handler {
	userServer := newServer(tools, false)
	adminServer := newServer(tools, true)
	handler := mcp.NewStreamableHTTPHandler(func(r *http.Request) *mcp.Server {
		if p, ok := core.ToolPrincipal(auth.TokenInfoFromContext(r.Context())); ok && p.PlatformAdmin {
			return adminServer
		}
		return userServer
	}, &mcp.StreamableHTTPOptions{
		Stateless: true, JSONResponse: true,
		CrossOriginProtection: http.NewCrossOriginProtection(),
	})
	verify := func(ctx context.Context, token string, _ *http.Request) (*auth.TokenInfo, error) {
		p, err := store.AccessTokenPrincipal(ctx, strings.TrimSpace(token))
		if err != nil {
			return nil, errors.Join(auth.ErrInvalidToken, err)
		}
		return core.TokenInfo(p), nil
	}
	protect := auth.RequireBearerToken(verify, &auth.RequireBearerTokenOptions{AllowMissingExpiration: true})
	return challenge(protect(handler))
}

// challenge adds the RFC 6750 Bearer challenge to 401 responses, which the
// SDK only sends when OAuth resource metadata is configured.
func challenge(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		next.ServeHTTP(challengeWriter{ResponseWriter: w}, r)
	})
}

type challengeWriter struct {
	http.ResponseWriter
}

func (w challengeWriter) WriteHeader(status int) {
	if status == http.StatusUnauthorized && w.Header().Get("WWW-Authenticate") == "" {
		w.Header().Set("WWW-Authenticate", `Bearer realm="TerraScale MCP", error="invalid_token"`)
	}
	w.ResponseWriter.WriteHeader(status)
}

func (w challengeWriter) Unwrap() http.ResponseWriter { return w.ResponseWriter }

func newServer(tools *core.Tools, admin bool) *mcp.Server {
	server := mcp.NewServer(&mcp.Implementation{Name: "terrascale-dashboard", Title: "TerraScale Dashboard", Version: serverVersion},
		&mcp.ServerOptions{
			Instructions: instructions,
			Capabilities: &mcp.ServerCapabilities{Tools: &mcp.ToolCapabilities{}},
			// The tool list depends on the caller's role, so shared caches
			// must not serve it to another user.
			SetCacheable: func(_ context.Context, _ mcp.Request, c *mcp.Cacheable) {
				c.CacheScope = "private"
			},
		})
	tools.Register(server, admin)
	return server
}
