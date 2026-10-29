package v1_test

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"reflect"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const collectionRulesPath = parityDatabase + "/collections/people/rules"

func TestCollectionRulesNullDenyAndExactPolicy(t *testing.T) {
	c := parityClient(t, http.MethodGet, collectionRulesPath, "", "", `{"rules":null}`, 200)
	out, err := c.Database(parityProject, "main").Collection("people").Rules(t.Context())
	if err != nil || out.Rules != nil {
		t.Fatalf("rules=%+v error=%v", out, err)
	}
	ownerPointer := "/userId"
	for _, owner := range []*string{nil, &ownerPointer} {
		policy := tb.CollectionAccessRules{Owner: owner, Read: &tb.CollectionAccessPolicy{Public: true, Roles: []string{"Admin", "admin"}}, Write: &tb.CollectionAccessPolicy{Owner: owner != nil, Roles: []string{}}}
		body := `{"read":{"owner":false,"public":true,"roles":["Admin","admin"]},"write":{"owner":false,"public":false,"roles":[]}}`
		if owner != nil {
			body = `{"owner":"/userId","read":{"owner":false,"public":true,"roles":["Admin","admin"]},"write":{"owner":true,"public":false,"roles":[]}}`
		}
		c := parityClient(t, http.MethodPut, collectionRulesPath, "", body, `{"rules":`+body+`}`, 200)
		out, err := c.Database(parityProject, "main").Collection("people").SetRules(t.Context(), policy)
		if err != nil || !reflect.DeepEqual(out.Rules, &policy) {
			t.Fatalf("rules=%+v error=%v", out, err)
		}
	}
}

func TestCollectionRulesErrorsAndMalformedResultsNeverReplay(t *testing.T) {
	policy := tb.CollectionAccessRules{Read: &tb.CollectionAccessPolicy{Roles: []string{}}, Write: &tb.CollectionAccessPolicy{Roles: []string{}}}
	body := `{"read":{"owner":false,"public":false,"roles":[]},"write":{"owner":false,"public":false,"roles":[]}}`
	for _, item := range []struct {
		status   int
		response string
	}{
		{400, `{"error":{"code":"invalid_rules"}}`}, {403, `{"error":{"code":"forbidden","message":"project credentials are required"}}`},
		{404, `{"error":{"code":"not_found"}}`}, {413, `{"error":{"code":"payload_too_large"}}`}, {503, `{"error":{"code":"catalog_stale"}}`},
		{200, `{}`}, {200, `null`}, {200, `{"rules":{}}`}, {200, `{"rules":{"read":null,"write":{}}}`},
	} {
		c := parityClient(t, http.MethodPut, collectionRulesPath, "", body, item.response, item.status)
		_, err := c.Database(parityProject, "main").Collection("people").SetRules(t.Context(), policy)
		if err == nil {
			t.Fatal("accepted failure")
		}
		var api *tb.APIError
		if item.status != 200 && (!errors.As(err, &api) || api.Status != item.status) {
			t.Fatal(err)
		}
	}
}

func TestCollectionRulesCancellation(t *testing.T) {
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	hc := &http.Client{Transport: collectionRulesTransport(func(*http.Request) (*http.Response, error) {
		t.Error("canceled request dispatched")
		return nil, fmt.Errorf("unexpected")
	})}
	c, err := tb.New(tb.Config{HTTPClient: hc, Token: "fixture.jwt.signature"})
	if err != nil {
		t.Fatal(err)
	}
	col := c.Database(parityProject, "main").Collection("people")
	if _, err := col.Rules(ctx); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if _, err := col.SetRules(t.Context(), tb.CollectionAccessRules{}); err == nil {
		t.Fatal("missing policies reached transport")
	}

}

type collectionRulesTransport func(*http.Request) (*http.Response, error)

func (f collectionRulesTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
