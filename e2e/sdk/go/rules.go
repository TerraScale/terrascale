package main

import (
	"errors"
	"fmt"

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
	read := "row.id = auth.uid"
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
	input := v1.RulesTestRequest{Operation: "read", Table: r.table, Auth: v1.RulesTestAuth{UID: "owner"}, Row: map[string]any{"id": "owner"}}
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
