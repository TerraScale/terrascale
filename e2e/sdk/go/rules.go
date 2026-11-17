package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"

	"terrascale.tech/sdk/tsauth"

	"terrascale.tech/sdk/v1"
)

func (r *runner) rules() error {
	ctx := r.ctx
	var current *string
	before, err := r.db.GetRules(ctx)
	if err == nil {
		current = &before.Version
	} else if code(err) != "no_rules" {
		return err
	}
	read := "row.id = auth.uid AND auth.claims.plan = 'pro'"
	rules := v1.RulesSet{Tables: map[string]v1.RulesTable{r.table: {Read: &read, Columns: []string{"id", "label"}}}}
	deployed, err := r.db.DeployRules(ctx, rules, current)
	if err != nil {
		return err
	}
	got, err := r.db.GetRules(ctx)
	if err != nil {
		return err
	}
	if got.Version != deployed.Version || got.Tables[r.table].Read == nil || *got.Tables[r.table].Read != read {
		return errors.New("rules read differs")
	}
	if err := r.customTokenRead(); err != nil {
		return err
	}
	input := v1.RulesTestRequest{Operation: "read", Table: r.table, Auth: v1.RulesTestAuth{UID: "owner", Claims: map[string]any{"plan": "pro"}}, Row: map[string]any{"id": "owner"}}
	allowed, err := r.db.TestRules(ctx, input)
	if err != nil {
		return err
	}
	if !allowed.Allowed || len(allowed.Columns) != 2 || len(allowed.Problems) != 0 {
		return errors.New("rules simulator did not allow owner")
	}
	input.Auth.UID = "other"
	denied, err := r.db.TestRules(ctx, input)
	if err != nil {
		return err
	}
	if denied.Allowed || denied.DecidedBy != "read" {
		return errors.New("rules simulator did not deny other user")
	}
	invalid := v1.RulesSet{Tables: map[string]v1.RulesTable{r.table: {Read: &read, Columns: []string{"missing_column"}}}}
	_, err = r.db.DeployRules(ctx, invalid, &deployed.Version)
	var api *v1.APIError
	if !errors.Is(err, v1.ErrInvalidRules) || !errors.As(err, &api) || api.Status != 400 || len(api.Problems) == 0 {
		return fmt.Errorf("invalid_rules diagnostics missing: %v", err)
	}
	_, err = r.db.DeployRules(ctx, rules, nil)
	if !errors.Is(err, v1.ErrVersionMismatch) {
		return fmt.Errorf("stale rules accepted: %v", err)
	}
	empty := v1.RulesSet{Tables: map[string]v1.RulesTable{}}
	updated, err := r.db.DeployRules(ctx, empty, &deployed.Version)
	if err != nil {
		return err
	}
	page, err := r.db.ListRulesVersions(ctx, v1.PageOptions{Limit: 1})
	if err != nil {
		return err
	}
	if len(page.Items) != 1 || page.Items[0].Version != updated.Version || page.Next == "" {
		return errors.New("rules first page differs")
	}
	next, err := r.db.ListRulesVersions(ctx, v1.PageOptions{Limit: 1, After: page.Next})
	if err != nil {
		return err
	}
	if len(next.Items) != 1 || next.Items[0].Version != deployed.Version {
		return errors.New("rules second page differs")
	}
	version, err := r.db.GetRulesVersion(ctx, deployed.Version)
	if err != nil {
		return err
	}
	if version.Version != deployed.Version || version.Tables[r.table].Read == nil || *version.Tables[r.table].Read != read {
		return errors.New("rules version differs")
	}
	restored, err := r.db.RestoreRules(ctx, deployed.Version, &updated.Version)
	if err != nil {
		return err
	}
	if restored.Version == deployed.Version || restored.Tables[r.table].Read == nil || *restored.Tables[r.table].Read != read {
		return errors.New("rules restore differs")
	}
	if before != nil && current != nil {
		_, err = r.db.RestoreRules(ctx, before.Version, &restored.Version)
	} else {
		_, err = r.db.DeployRules(ctx, empty, &restored.Version)
	}
	return err
}

func (r *runner) customTokenRead() error {
	auth, err := tsauth.New(tsauth.Config{})
	if err != nil {
		return err
	}
	user := os.Getenv("E2E_AUTH_USER")
	custom, err := auth.WithToken(os.Getenv("E2E_AUTH_MINT_KEY")).MintCustomToken(r.ctx, r.project,
		tsauth.CustomTokenRequest{UserID: user, Database: os.Getenv("E2E_DATABASE_ID"), Claims: map[string]any{"plan": "pro"}})
	if err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(os.Getenv("E2E_SECRETS"), "custom-token-go"), []byte(custom.AccessToken), 0o600); err != nil {
		return err
	}
	if custom.TokenType != "Bearer" || custom.ExpiresIn != 300 {
		return errors.New("custom token lifetime differs")
	}
	if _, err := r.db.Execute(r.ctx, fmt.Sprintf("INSERT INTO %s(rowid,id,label) VALUES (700001,?,'custom-token'),(700002,'other','hidden')", r.table), user); err != nil {
		return err
	}
	for _, key := range []string{"700001", "700002"} {
		if err := r.customTokenRow(r.ctx, custom.AccessToken, user, key); err != nil {
			return err
		}
	}
	_, err = r.db.Execute(r.ctx, fmt.Sprintf("DELETE FROM %s WHERE rowid IN (700001,700002)", r.table))
	return err
}

func (r *runner) customTokenRow(ctx context.Context, token, user, key string) error {
	path := fmt.Sprintf("https://api.terrascale.tech/v1/projects/%s/databases/%s/tables/%s/rows/%s",
		r.project, os.Getenv("E2E_DATABASE_ID"), r.table, key)
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, path, nil)
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", "Bearer "+token)
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	var body struct {
		Row   map[string]any
		Error struct{ Code string }
	}
	if err := json.NewDecoder(response.Body).Decode(&body); err != nil {
		return err
	}
	if key == "700001" {
		if response.StatusCode != 200 || body.Row["id"] != user || body.Row["label"] != "custom-token" {
			return errors.New("custom token Rules read differs")
		}
	} else if response.StatusCode != 404 || body.Error.Code != "row_not_found" {
		return errors.New("custom token revealed another user's row")
	}
	return nil
}
