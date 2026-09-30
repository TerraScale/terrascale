// The reference customer uses only exported TerraScale SDK APIs.
package main

import (
	"context"
	"errors"
	"fmt"
	"log"
	"net"
	"net/http"
	"net/url"
	"os"
	"os/signal"
	"strconv"
	"strings"
	"syscall"
	"time"

	sdk "terrascale.tech/sdk"
)

type configuration struct {
	origin, issuer, clientID, clientSecret, dataToken, dataOrigin, listen string
	development                                                           bool
	scope                                                                 sdk.DocumentScope
	collection                                                            sdk.UUID
	schemaGeneration                                                      sdk.DecimalU64
	schemaDigest                                                          sdk.Hex32
	ownerField, bodyField                                                 uint32
}

func loadConfiguration(getenv func(string) string) (configuration, error) {
	var c configuration
	c.development = getenv("REFERENCE_DEVELOPMENT") == "true"
	c.origin = strings.TrimSuffix(getenv("REFERENCE_ORIGIN"), "/")
	c.issuer = strings.TrimSuffix(getenv("TERRASCALE_AUTH_ISSUER"), "/")
	c.clientID, c.clientSecret = getenv("TERRASCALE_CLIENT_ID"), getenv("TERRASCALE_CLIENT_SECRET")
	c.dataToken, c.dataOrigin = getenv("TERRASCALE_DATA_KEY"), getenv("REFERENCE_DEVELOPMENT_DATA_URL")
	c.listen = getenv("REFERENCE_LISTEN")
	if c.listen == "" {
		c.listen = "127.0.0.1:8080"
	}
	origin, err := url.Parse(c.origin)
	if err != nil || origin.Host == "" || origin.User != nil || origin.RawQuery != "" || origin.Fragment != "" || origin.Path != "" ||
		(origin.Scheme != "https" && !(c.development && origin.Scheme == "http" && loopbackHost(origin.Hostname()))) {
		return c, errors.New("REFERENCE_ORIGIN requires an HTTPS origin, or loopback HTTP in explicit development")
	}
	if c.dataOrigin != "" && !c.development {
		return c, errors.New("explicit data origin requires REFERENCE_DEVELOPMENT=true")
	}
	issuer, err := url.Parse(c.issuer)
	if err != nil || issuer.Host == "" || issuer.User != nil || issuer.RawQuery != "" || issuer.Fragment != "" ||
		strings.Count(issuer.Path, "/") != 1 || len(issuer.Path) < 2 ||
		(issuer.Scheme != "https" && !(c.development && issuer.Scheme == "http" && loopbackHost(issuer.Hostname()))) {
		return c, errors.New("TERRASCALE_AUTH_ISSUER requires the registered instance issuer")
	}
	if c.clientID == "" || c.clientSecret == "" || c.dataToken == "" {
		return c, errors.New("OAuth client and server-held scoped data key are required")
	}
	c.scope.Tenant, err = sdk.ParseUUID(getenv("TERRASCALE_TENANT_ID"))
	if err != nil {
		return c, errors.New("invalid tenant UUID")
	}
	c.scope.Database, err = sdk.ParseUUID(getenv("TERRASCALE_DATABASE_ID"))
	if err != nil {
		return c, errors.New("invalid database UUID")
	}
	c.collection, err = sdk.ParseUUID(getenv("TERRASCALE_COLLECTION_ID"))
	if err != nil {
		return c, errors.New("invalid collection UUID")
	}
	shard, err := strconv.ParseUint(getenv("TERRASCALE_SHARD"), 10, 64)
	if err != nil {
		return c, errors.New("invalid shard decimal")
	}
	c.scope.Shard = sdk.DecimalU64(shard)
	generation, err := strconv.ParseUint(getenv("TERRASCALE_SCHEMA_GENERATION"), 10, 64)
	if err != nil || generation == 0 {
		return c, errors.New("positive schema generation is required")
	}
	c.schemaGeneration = sdk.DecimalU64(generation)
	c.schemaDigest, err = sdk.ParseHex32(getenv("TERRASCALE_SCHEMA_DIGEST"))
	if err != nil {
		return c, errors.New("invalid schema digest")
	}
	owner, err := strconv.ParseUint(getenv("TERRASCALE_OWNER_FIELD_ID"), 10, 32)
	if err != nil || owner == 0 {
		return c, errors.New("positive owner field ID is required")
	}
	body, err := strconv.ParseUint(getenv("TERRASCALE_BODY_FIELD_ID"), 10, 32)
	if err != nil || body == 0 || body == owner {
		return c, errors.New("distinct positive body field ID is required")
	}
	c.ownerField, c.bodyField = uint32(owner), uint32(body)
	return c, nil
}

func loopbackHost(host string) bool {
	ip := net.ParseIP(host)
	return host == "localhost" || ip != nil && ip.IsLoopback()
}

func run(ctx context.Context) error {
	config, err := loadConfiguration(os.Getenv)
	if err != nil {
		return err
	}
	issuer, _ := url.Parse(config.issuer)
	client, err := sdk.NewClient(sdk.Config{
		TSAuthBaseURL:   issuer.Scheme + "://" + issuer.Host,
		DataAccessToken: config.dataToken, DataBaseURL: config.dataOrigin,
		RequestTimeout: 10 * time.Second,
	})
	if err != nil {
		return errors.New("SDK configuration rejected; check configured origins and scoped credentials")
	}
	instance, err := client.TSAuth.Instance(strings.TrimPrefix(issuer.Path, "/"))
	if err != nil {
		return errors.New("invalid auth instance configuration")
	}
	oidc, err := instance.OIDC.NewApplication(sdk.TSAuthOIDCApplicationConfig{
		Issuer:         config.issuer,
		Authentication: sdk.TSAuthOAuthClientAuthentication{ClientID: config.clientID, ClientSecret: config.clientSecret, Method: sdk.TSAuthClientSecretBasic},
		RedirectURI:    config.origin + "/callback", PostLogoutRedirectURIs: []string{config.origin + "/signed-out"}, RequireSession: true,
	})
	if err != nil {
		return errors.New("OIDC application registration is invalid")
	}
	app := newApplication(config, oidc, client.Documents)
	server := &http.Server{Addr: config.listen, Handler: app.handler(), ReadHeaderTimeout: 5 * time.Second, ReadTimeout: 15 * time.Second, WriteTimeout: 30 * time.Second, IdleTimeout: time.Minute, MaxHeaderBytes: 16 << 10}
	go func() {
		<-ctx.Done()
		stop, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = server.Shutdown(stop)
	}()
	log.Print("reference application listening; connection capabilities remain subject to SDK admission")
	err = server.ListenAndServe()
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return errors.New("reference application listener failed")
}

func main() {
	ctx, cancel := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer cancel()
	if err := run(ctx); err != nil {
		fmt.Fprintln(os.Stderr, err.Error())
		os.Exit(1)
	}
}
