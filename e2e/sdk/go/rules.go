package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"strings"

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
	create := "auth.claims.plan = 'pro'"
	rules := v1.RulesSet{Tables: map[string]v1.RulesTable{r.table: {Read: &read, Create: &create, Columns: []string{"id", "label"}, Writable: []string{"label"}, Set: &v1.RulesValues{Create: map[string]string{"id": "auth.uid"}}}}}
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
	if err := r.customTokenWriteRead(); err != nil {
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

func (r *runner) customTokenWriteRead() error {
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
	if _, err := r.db.Execute(r.ctx, fmt.Sprintf("INSERT INTO %s(rowid,id,label) VALUES (700002,'other','hidden')", r.table)); err != nil {
		return err
	}
	path := fmt.Sprintf("https://api.terrascale.tech/v1/projects/%s/databases/%s/tables/%s/rows",
		r.project, os.Getenv("E2E_DATABASE_ID"), r.table)
	request, err := http.NewRequestWithContext(r.ctx, http.MethodPost, path, strings.NewReader(`{"row":{"label":"custom-token"}}`))
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", "Bearer "+custom.AccessToken)
	request.Header.Set("Content-Type", "application/json")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	var written struct {
		Key string
		Row map[string]any
	}
	if err := json.NewDecoder(response.Body).Decode(&written); err != nil {
		return err
	}
	if response.StatusCode != 201 || written.Row["id"] != user || written.Row["label"] != "custom-token" || written.Key == "" {
		return errors.New("custom token Rules write differs")
	}
	if err := r.customTokenRow(r.ctx, custom.AccessToken, user, written.Key, false); err != nil {
		return err
	}
	if err := r.customTokenRow(r.ctx, custom.AccessToken, user, "700002", true); err != nil {
		return err
	}
	if err := r.customTokenDeniedUpdate(r.ctx, custom.AccessToken, written.Key); err != nil {
		return err
	}
	_, err = r.db.Execute(r.ctx, fmt.Sprintf("DELETE FROM %s WHERE rowid = 700002 OR label = 'custom-token'", r.table))
	return err
}

func (r *runner) customTokenRow(ctx context.Context, token, user, key string, hidden bool) error {
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
	if !hidden {
		if response.StatusCode != 200 || body.Row["id"] != user || body.Row["label"] != "custom-token" {
			return errors.New("custom token Rules read differs")
		}
	} else if response.StatusCode != 404 || body.Error.Code != "row_not_found" {
		return errors.New("custom token revealed another user's row")
	}
	return nil
}

func (r *runner) customTokenDeniedUpdate(ctx context.Context, token, key string) error {
	path := fmt.Sprintf("https://api.terrascale.tech/v1/projects/%s/databases/%s/tables/%s/rows/%s",
		r.project, os.Getenv("E2E_DATABASE_ID"), r.table, key)
	request, err := http.NewRequestWithContext(ctx, http.MethodPatch, path, strings.NewReader(`{"row":{"label":"changed"}}`))
	if err != nil {
		return err
	}
	request.Header.Set("Authorization", "Bearer "+token)
	request.Header.Set("Content-Type", "application/json")
	response, err := http.DefaultClient.Do(request)
	if err != nil {
		return err
	}
	defer response.Body.Close()
	var body struct{ Error struct{ Code string } }
	if err := json.NewDecoder(response.Body).Decode(&body); err != nil {
		return err
	}
	if response.StatusCode != 403 || body.Error.Code != "rule_denied" {
		return fmt.Errorf("custom token update was not denied: %d %s", response.StatusCode, body.Error.Code)
	}
	return nil
}

func (r *runner) ts(dir string, args ...string) (int, []byte, error) {
	self, err := os.Executable()
	if err != nil {
		return 0, nil, err
	}
	command := exec.CommandContext(r.ctx, filepath.Join(filepath.Dir(self), "ts"), append([]string{"-project", r.project}, args...)...)
	command.Dir = dir
	var stderr strings.Builder
	command.Stderr = &stderr
	out, err := command.Output()
	var exit *exec.ExitError
	if errors.As(err, &exit) {
		return exit.ExitCode(), out, nil
	}
	if err != nil {
		return 0, nil, fmt.Errorf("ts %s: %w: %s", strings.Join(args, " "), err, stderr.String())
	}
	return 0, out, nil
}

func (r *runner) rulesCLI() error {
	database := os.Getenv("E2E_DATABASE_ID")
	dir, err := os.MkdirTemp("", "ts-rules-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(dir)
	exit, out, err := r.ts(dir, "rules", "pull", database, "--json")
	if err != nil {
		return err
	}
	var pulled struct {
		Path    string      `json:"path"`
		Version *string     `json:"version"`
		Rules   v1.RulesSet `json:"rules"`
	}
	if err := json.Unmarshal(out, &pulled); err != nil || exit != 0 || pulled.Path != "terrascale.rules.json" {
		return fmt.Errorf("ts rules pull: exit %d: %s", exit, out)
	}
	live, err := r.db.GetRules(r.ctx)
	if err != nil && code(err) != "no_rules" {
		return err
	}
	if err == nil && (pulled.Version == nil || *pulled.Version != live.Version) || err != nil && pulled.Version != nil {
		return fmt.Errorf("ts rules pull version %v differs from the SDK", pulled.Version)
	}
	read := "row.id = auth.uid"
	rules := v1.RulesSet{Tables: map[string]v1.RulesTable{r.table: {Read: &read, Columns: []string{"id", "label"}}}}
	body, err := json.Marshal(rules)
	if err != nil {
		return err
	}
	if err := os.WriteFile(filepath.Join(dir, "terrascale.rules.json"), body, 0o600); err != nil {
		return err
	}
	exit, out, err = r.ts(dir, "rules", "deploy", database, "--yes", "--json")
	if err != nil {
		return err
	}
	var deployed v1.Rules
	if err := json.Unmarshal(out, &deployed); err != nil || exit != 0 {
		return fmt.Errorf("ts rules deploy: exit %d: %s", exit, out)
	}
	current, err := r.db.GetRules(r.ctx)
	if err != nil {
		return err
	}
	if current.Version != deployed.Version || pulled.Version != nil && deployed.Version == *pulled.Version ||
		current.Tables[r.table].Read == nil || *current.Tables[r.table].Read != read {
		return errors.New("ts rules deploy differs from the SDK read")
	}
	for _, uid := range []string{"owner", "other"} {
		exit, out, err := r.ts(dir, "rules", "test", database, "--operation", "read", "--table", r.table,
			"--auth", `{"uid":"`+uid+`"}`, "--row", `{"id":"owner","label":"a"}`, "--json")
		if err != nil {
			return err
		}
		var cli v1.RulesTestResult
		if err := json.Unmarshal(out, &cli); err != nil {
			return fmt.Errorf("ts rules test %s: exit %d: %s", uid, exit, out)
		}
		sdk, err := r.db.TestRules(r.ctx, v1.RulesTestRequest{Operation: "read", Table: r.table, Auth: v1.RulesTestAuth{UID: uid},
			Row: map[string]any{"id": "owner", "label": "a"}})
		if err != nil {
			return err
		}
		allowed := uid == "owner"
		if cli.Allowed != allowed || sdk.Allowed != allowed || (exit == 0) != allowed || cli.DecidedBy != sdk.DecidedBy ||
			strings.Join(cli.Columns, ",") != strings.Join(sdk.Columns, ",") {
			return fmt.Errorf("ts rules test %s: exit %d %+v, SDK %+v", uid, exit, cli, *sdk)
		}
	}
	if pulled.Version != nil {
		_, err = r.db.RestoreRules(r.ctx, *pulled.Version, &deployed.Version)
	} else {
		_, err = r.db.DeployRules(r.ctx, v1.RulesSet{Tables: map[string]v1.RulesTable{}}, &deployed.Version)
	}
	return err
}
