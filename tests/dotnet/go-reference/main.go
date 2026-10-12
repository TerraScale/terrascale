// This executable serves the unchanged Go dashboard exclusively for TUnit's
// migration reference capture. It contains no test runner or substitute UI.
package main

import (
	"context"
	"crypto/rand"
	"crypto/rsa"
	"encoding/base64"
	"encoding/json/v2"
	"errors"
	"fmt"
	"math/big"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/config"
)

const clientID = "ts-dashboard"
const clientSecret = "e2e-dashboard-secret"

type referenceServer struct {
	listener net.Listener
	server   *http.Server
	origin   string
}

func main() {
	if err := run(); err != nil {
		fmt.Fprintln(os.Stderr, "Go reference host:", err)
		os.Exit(1)
	}
}

func run() error {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	var servers []*referenceServer
	defer func() {
		deadline, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		for _, server := range servers {
			_ = server.server.Shutdown(deadline)
			_ = server.listener.Close()
		}
	}()
	create := func(handler http.Handler) (*referenceServer, error) {
		listener, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			return nil, err
		}
		server := &referenceServer{listener: listener, origin: "http://" + listener.Addr().String(),
			server: &http.Server{Handler: handler, ReadHeaderTimeout: 10 * time.Second,
				BaseContext: func(net.Listener) context.Context { return ctx }}}
		servers = append(servers, server)
		return server, nil
	}
	atlas, err := create(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"v":1,"closest":"gru-1","regions":[{"id":"gru-1","name":"São Paulo","country":"BR","api":"https://gru.api.terrascale.tech","health":"ok"},{"id":"sfo-1","name":"San Francisco","country":"US","api":"https://sfo.api.terrascale.tech","health":"ok"}]}`))
	}))
	if err != nil {
		return err
	}
	// Discovery is read while app.New creates the original native-auth host.
	provider, err := create(nil)
	if err != nil {
		return err
	}
	provider.server.Handler, err = nativeAuth(provider.origin)
	if err != nil {
		return err
	}
	serveErrors := make(chan error, 4)
	serve := func(server *referenceServer) {
		go func() { serveErrors <- server.server.Serve(server.listener) }()
	}
	serve(atlas)
	serve(provider)
	demoApplication, err := app.New(config.Config{Demo: true,
		AdminEmail: "e2e-admin-seed@example.test", AdminPassword: "E2e-admin-seed-123!",
		TSAuthOrigin: "https://gru.auth.terrascale.tech", AtlasURL: atlas.origin})
	if err != nil {
		return err
	}
	demo, err := create(demoApplication.Handler)
	if err != nil {
		return err
	}
	native, err := create(nil)
	if err != nil {
		return err
	}
	nativeApplication, err := app.New(config.Config{Demo: true, TSAuth: config.TSAuth{
		Issuer: provider.origin, ClientID: clientID, ClientSecret: clientSecret,
		RedirectURL: native.origin + "/login/callback",
	}})
	if err != nil {
		return err
	}
	native.server.Handler = nativeApplication.Handler
	serve(demo)
	serve(native)
	if err := json.MarshalWrite(os.Stdout, map[string]string{"origin": demo.origin, "nativeOrigin": native.origin}); err != nil {
		return err
	}
	fmt.Fprintln(os.Stdout)
	select {
	case <-ctx.Done():
		return nil
	case err := <-serveErrors:
		if errors.Is(err, http.ErrServerClosed) {
			return nil
		}
		return err
	}
}

// The reference inventory only renders anonymous native authentication,
// starts code/reset ceremonies, and rejects a password. Keep that exact
// HTTP provider boundary; valid user tokens and customer data are unnecessary.
func nativeAuth(origin string) (http.Handler, error) {
	key, err := rsa.GenerateKey(rand.Reader, 2048)
	if err != nil {
		return nil, err
	}
	write := func(w http.ResponseWriter, status int, value any) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(status)
		_ = json.MarshalWrite(w, value)
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /.well-known/openid-configuration", func(w http.ResponseWriter, _ *http.Request) {
		write(w, http.StatusOK, map[string]any{
			"issuer": origin, "authorization_endpoint": origin + "/oauth/authorize", "token_endpoint": origin + "/oauth/token",
			"jwks_uri": origin + "/jwks.json", "end_session_endpoint": origin + "/oauth/logout", "revocation_endpoint": origin + "/oauth/revoke",
			"id_token_signing_alg_values_supported": []string{"RS256"}, "response_types_supported": []string{"code"},
			"subject_types_supported": []string{"public"}, "code_challenge_methods_supported": []string{"S256"},
			"grant_types_supported": []string{"authorization_code", "refresh_token", "urn:terrascale:params:oauth:grant-type:password", "urn:terrascale:params:oauth:grant-type:email-code"},
		})
	})
	mux.HandleFunc("GET /jwks.json", func(w http.ResponseWriter, _ *http.Request) {
		write(w, http.StatusOK, map[string]any{"keys": []map[string]string{{
			"kty": "RSA", "kid": "e2e", "alg": "RS256", "use": "sig",
			"n": base64.RawURLEncoding.EncodeToString(key.N.Bytes()),
			"e": base64.RawURLEncoding.EncodeToString(big.NewInt(int64(key.E)).Bytes()),
		}}})
	})
	mux.HandleFunc("POST /headless/v1/{step...}", func(w http.ResponseWriter, r *http.Request) {
		var fields map[string]string
		id, secret, ok := r.BasicAuth()
		if !ok || id != clientID || secret != clientSecret || json.UnmarshalRead(r.Body, &fields) != nil || fields["ip_address"] == "" {
			write(w, http.StatusBadRequest, map[string]any{"error": map[string]string{"code": "invalid_request", "message": "bad request"}})
			return
		}
		switch r.PathValue("step") {
		case "email-code/start", "password-reset/start":
			w.WriteHeader(http.StatusAccepted)
		default:
			write(w, http.StatusBadRequest, map[string]any{"error": map[string]string{"code": "invalid_request", "message": "not a reference ceremony"}})
		}
	})
	mux.HandleFunc("POST /oauth/token", func(w http.ResponseWriter, r *http.Request) {
		id, secret, ok := r.BasicAuth()
		if !ok || id != clientID || secret != clientSecret {
			write(w, http.StatusUnauthorized, map[string]string{"error": "invalid_client"})
			return
		}
		write(w, http.StatusBadRequest, map[string]string{"error": "invalid_grant"})
	})
	return mux, nil
}
