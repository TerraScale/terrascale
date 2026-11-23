package v1

import (
	"context"
	"fmt"
	"net/http"
	"time"
)

type RulesSet struct {
	Tables map[string]RulesTable `json:"tables"`
}

type RulesTable struct {
	Read     *string      `json:"read,omitempty"`
	Create   *string      `json:"create,omitempty"`
	Update   *string      `json:"update,omitempty"`
	Delete   *string      `json:"delete,omitempty"`
	Columns  []string     `json:"columns"`
	Writable []string     `json:"writable"`
	Validate []string     `json:"validate"`
	Set      *RulesValues `json:"set,omitempty"`
}

type RulesValues struct {
	Create map[string]string `json:"create,omitempty"`
	Update map[string]string `json:"update,omitempty"`
}

type RulesProblem struct {
	Table   string `json:"table"`
	Field   string `json:"field"`
	Code    string `json:"code"`
	Message string `json:"message"`
}

type Rules struct {
	Version   string                `json:"version" ulid:"required"`
	Tables    map[string]RulesTable `json:"tables"`
	Problems  []RulesProblem        `json:"problems"`
	Warnings  []RulesProblem        `json:"warnings"`
	UpdatedAt time.Time             `json:"updatedAt"`
	UpdatedBy string                `json:"updatedBy"`
}

type RulesVersion struct {
	Version   string    `json:"version" ulid:"required"`
	UpdatedAt time.Time `json:"updatedAt"`
	UpdatedBy string    `json:"updatedBy"`
	Digest    string    `json:"digest"`
}

type RulesTestAuth struct {
	UID         string         `json:"uid"`
	Claims      map[string]any `json:"claims,omitempty"`
	OrgID       *string        `json:"org_id,omitempty"`
	OrgRole     *string        `json:"org_role,omitempty"`
	IsAnonymous bool           `json:"is_anonymous"`
}

type RulesTestRequest struct {
	Operation string         `json:"operation"`
	Table     string         `json:"table"`
	Auth      RulesTestAuth  `json:"auth"`
	Row       map[string]any `json:"row,omitempty"`
	New       map[string]any `json:"new,omitempty"`
	Time      *int64         `json:"time,omitempty"`
	Rules     *RulesSet      `json:"rules,omitempty"`
}

type RulesTestValue struct {
	Rule  string `json:"rule"`
	Value bool   `json:"value"`
}

type RulesTestResult struct {
	Allowed   bool             `json:"allowed"`
	DecidedBy string           `json:"decidedBy"`
	Clause    *string          `json:"clause"`
	Results   []RulesTestValue `json:"results"`
	Columns   []string         `json:"columns"`
	Problems  []RulesProblem   `json:"problems"`
}

func rulesPrecondition(version *string) (http.Header, error) {
	if version == nil {
		return http.Header{"If-None-Match": {"*"}}, nil
	}
	if err := checkID(*version); err != nil {
		return nil, fmt.Errorf("terrabase: rules version: %w", err)
	}
	return http.Header{"If-Match": {"\"" + *version + "\""}}, nil
}

func (d *Database) GetRules(ctx context.Context) (*Rules, error) {
	p, err := d.path("rules")
	if err != nil {
		return nil, err
	}
	var out Rules
	_, err = d.c.call(ctx, request{method: http.MethodGet, path: p}, nil, &out)
	return &out, err
}

func (d *Database) DeployRules(ctx context.Context, rules RulesSet, currentVersion *string) (*Rules, error) {
	p, err := d.path("rules")
	if err != nil {
		return nil, err
	}
	h, err := rulesPrecondition(currentVersion)
	if err != nil {
		return nil, err
	}
	var out Rules
	_, err = d.c.call(ctx, request{method: http.MethodPut, path: p, header: h}, rules, &out)
	return &out, err
}

func (d *Database) ListRulesVersions(ctx context.Context, options PageOptions) (*Page[RulesVersion], error) {
	p, err := d.path("rules", "versions")
	if err != nil {
		return nil, err
	}
	var out Page[RulesVersion]
	_, err = d.c.call(ctx, request{method: http.MethodGet, path: p, query: options.query()}, nil, &out)
	return &out, err
}

func (d *Database) GetRulesVersion(ctx context.Context, version string) (*Rules, error) {
	if err := checkID(version); err != nil {
		return nil, err
	}
	p, err := d.path("rules", "versions", version)
	if err != nil {
		return nil, err
	}
	var out Rules
	_, err = d.c.call(ctx, request{method: http.MethodGet, path: p}, nil, &out)
	return &out, err
}

func (d *Database) RestoreRules(ctx context.Context, version string, currentVersion *string) (*Rules, error) {
	if err := checkID(version); err != nil {
		return nil, err
	}
	p, err := d.path("rules", "versions", version, "restore")
	if err != nil {
		return nil, err
	}
	h, err := rulesPrecondition(currentVersion)
	if err != nil {
		return nil, err
	}
	var out Rules
	_, err = d.c.call(ctx, request{method: http.MethodPost, path: p, header: h}, nil, &out)
	return &out, err
}

func (d *Database) TestRules(ctx context.Context, input RulesTestRequest) (*RulesTestResult, error) {
	p, err := d.path("rules:test")
	if err != nil {
		return nil, err
	}
	body := struct {
		RulesTestRequest
		Tables map[string]RulesTable `json:"tables,omitzero"`
	}{RulesTestRequest: input}
	body.Rules = nil
	if input.Rules != nil {
		body.Tables = input.Rules.Tables
	}
	var out RulesTestResult
	answer := struct {
		*RulesTestResult
		Condition *string `json:"condition"`
	}{RulesTestResult: &out}
	_, err = d.c.call(ctx, request{method: http.MethodPost, path: p}, body, &answer)
	out.Clause = answer.Condition
	return &out, err
}
