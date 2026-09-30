// Command resourceaudience drives public SDK resource APIs. It can be installed
// against a local module proxy; successful fixture execution is source HTTP
// interoperability evidence, not native persistence or project authorization.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"os"
	"strings"
	"time"

	sdk "terrascale.tech/sdk"
)

func run() error {
	phase := flag.String("phase", "service", "bootstrap, service or inspect")
	base := flag.String("auth-base", "", "explicit Auth transport origin")
	issuer := flag.String("issuer", "", "canonical configured issuer")
	slug := flag.String("instance", "", "instance slug")
	audience := flag.String("resource", "", "exact registered HTTPS resource URI")
	scope := flag.String("scope", "", "explicit service scope tokens")
	redirect := flag.String("redirect-uri", "", "registered originating-client redirect for bootstrap")
	flag.Parse()
	if *base == "" || *issuer == "" || *slug == "" || *audience == "" || (*phase != "bootstrap" && *phase != "service" && *phase != "inspect") {
		return fmt.Errorf("required qualification configuration missing")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	client, err := sdk.NewClient(sdk.Config{TSAuthBaseURL: *base, AccessToken: os.Getenv("TERRASCALE_RESOURCE_MANAGEMENT_KEY")})
	if err != nil {
		return err
	}
	instance, err := client.TSAuth.Instance(*slug)
	if err != nil {
		return err
	}
	origin := sdk.TSAuthOAuthClientAuthentication{ClientID: os.Getenv("TERRASCALE_RESOURCE_ORIGIN_CLIENT_ID"), ClientSecret: os.Getenv("TERRASCALE_RESOURCE_ORIGIN_CLIENT_SECRET"), Method: sdk.TSAuthClientSecretBasic}
	resourceAuth := sdk.TSAuthResourceAuthentication{ClientID: os.Getenv("TERRASCALE_RESOURCE_INTROSPECTION_ID"), Secret: os.Getenv("TERRASCALE_RESOURCE_INTROSPECTION_SECRET")}
	scopes := strings.Fields(*scope)
	if *phase == "bootstrap" {
		if *redirect == "" || os.Getenv("TERRASCALE_RESOURCE_MANAGEMENT_KEY") == "" || len(scopes) == 0 {
			return fmt.Errorf("bootstrap configuration missing")
		}
		registered, err := instance.OAuthClientPolicies().Create(ctx, sdk.CreateTSAuthOAuthPolicyClientRequest{
			CreateTSAuthOAuthClientRequest: sdk.CreateTSAuthOAuthClientRequest{Name: "Resource qualification", RedirectURIs: []string{*redirect}, TokenEndpointAuthMethod: string(sdk.TSAuthClientSecretBasic)},
			AllowedResourceAudiences:       []string{*audience}, AllowedGrantTypes: []string{"client_credentials"}, AllowedScopes: scopes,
		})
		if err != nil {
			return err
		}
		if registered.ClientSecret == nil {
			return fmt.Errorf("originating secret missing")
		}
		origin.ClientID, origin.ClientSecret = registered.Client.ClientID, *registered.ClientSecret
		registeredResource, err := instance.OAuthResourcePolicies().Create(ctx, sdk.CreateTSAuthOAuthResourceRequest{Audience: *audience, AllowedClientIDs: []string{origin.ClientID}, AllowedGrantTypes: []string{"client_credentials"}, AllowedScopes: scopes})
		if err != nil {
			return err
		}
		if registeredResource.IntrospectionSecret == nil {
			return fmt.Errorf("resource secret missing")
		}
		resourceAuth.ClientID, resourceAuth.Secret = registeredResource.Resource.IntrospectionClientID, *registeredResource.IntrospectionSecret
	}
	introspector, err := instance.NewResourceIntrospector(sdk.TSAuthResourceIntrospectorConfig{Issuer: *issuer, ResourceAudience: *audience, Authentication: resourceAuth})
	if err != nil {
		return err
	}
	token := os.Getenv("TERRASCALE_RESOURCE_ACCESS_TOKEN")
	if *phase != "inspect" {
		originClient, err := instance.NewResourceClient(sdk.TSAuthResourceClientConfig{Issuer: *issuer, ResourceAudience: *audience, Authentication: origin})
		if err != nil {
			return err
		}
		grant, err := originClient.RequestClientCredentials(ctx, scopes)
		if err != nil {
			return err
		}
		token = grant.Tokens.AccessToken
	}
	principal, err := introspector.Introspect(ctx, token)
	if err != nil {
		return err
	}
	if !principal.Active {
		return fmt.Errorf("resource token inactive")
	}
	if *phase != "inspect" && (principal.PrincipalKind != "service" || principal.ClientID != origin.ClientID || principal.Subject != origin.ClientID || principal.SessionID != nil) {
		return fmt.Errorf("service principal mismatch")
	}
	unknown, err := introspector.Introspect(ctx, "unknown-qualification-token")
	if err != nil || unknown.Active {
		return fmt.Errorf("unknown token admitted")
	}
	if *phase != "inspect" {
		wrongRole, err := instance.OIDC.IntrospectToken(ctx, origin, token)
		if err != nil || wrongRole.Active {
			return fmt.Errorf("originating credentials admitted a resource token")
		}
	}
	return json.NewEncoder(os.Stdout).Encode(map[string]any{"active": true, "principal_kind": principal.PrincipalKind, "unknown_inactive": true, "fixture_interoperability_only": true, "durable_project_grants_authorized": false})
}

func main() {
	if run() != nil {
		fmt.Fprintln(os.Stderr, "resource qualification failed")
		os.Exit(1)
	}
}
