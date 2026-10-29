package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"

	"terrascale.tech/sdk/internal/entityid"
	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

// cliContext contains selectors only, never credentials or an authentication source.
type cliContext struct {
	AccountID      string `json:"accountId"`
	OrganizationID string `json:"organizationId"`
	ProjectID      string `json:"projectId"`
	Database       string `json:"database,omitempty"`
}

func contextPath() (string, error) {
	p, err := credentialsPath()
	return filepath.Join(filepath.Dir(p), "context.json"), err
}

func readCLIContext(path string) (cliContext, error) {
	var c cliContext
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return c, nil
	}
	if err != nil {
		return c, err
	}
	if json.Unmarshal(data, &c) != nil || entityid.Check(c.AccountID) != nil ||
		entityid.Check(c.OrganizationID) != nil || entityid.Check(c.ProjectID) != nil {
		return cliContext{}, errors.New("invalid saved context: run ts context clear, then ts context set")
	}
	return c, nil
}

func saveCLIContext(path string, c cliContext) error {
	for _, id := range []string{c.AccountID, c.OrganizationID, c.ProjectID} {
		if err := entityid.Check(id); err != nil {
			return fmt.Errorf("context requires verified account, organization and project IDs: %w", err)
		}
	}
	data, err := json.Marshal(c)
	if err != nil {
		return err
	}
	dir := filepath.Dir(path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}
	info, err := os.Lstat(dir)
	if err != nil {
		return err
	}
	if !info.IsDir() {
		return errors.New("context directory must be a directory, not a symlink")
	}
	if err := os.Chmod(dir, 0o700); err != nil {
		return err
	}
	f, err := os.CreateTemp(dir, ".context-*")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	defer f.Close()
	if _, err := f.Write(data); err != nil {
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	return os.Rename(f.Name(), path)
}

// Explicit selectors discard dependent defaults. API-key callers must not call
// this helper: context belongs only to its signed-in account.
func contextDefaults(c cliContext, account, org, project string) (string, string, string, error) {
	if org != "" || project != "" {
		return org, project, "", nil
	}
	if c.AccountID != "" && c.AccountID != account {
		return "", "", "", errors.New("saved context belongs to another account: run ts context clear or set explicit -org/-project")
	}
	return c.OrganizationID, c.ProjectID, c.Database, nil
}

// An explicit @ selects the persisted database without making optional
// positional operands (notably doc put's ID) ambiguous. Expand before consent.
func contextDatabase(operands []string, database string) ([]string, error) {
	if len(operands) == 0 || operands[0] != "@" {
		return operands, nil
	}
	if database == "" {
		return nil, fmt.Errorf("@ needs a saved database: run ts context set <org> <project> <database>: %w", errUsage)
	}
	return append([]string{database}, operands[1:]...), nil
}

// resolveCLIContext rechecks organization and project access through ts-auth.
// Names must match once; canonical IDs take precedence over names.
func resolveCLIContext(ctx context.Context, u *tsauth.User, account, org, project string) (cliContext, error) {
	c := cliContext{AccountID: account}
	items, err := organizationInventory(ctx, u)
	if err != nil {
		return c, err
	}
	var matches []string
	for _, item := range items {
		if item.ID == org {
			c.OrganizationID = item.ID
		}
		if item.Name == org {
			matches = append(matches, item.ID)
		}
	}
	if c.OrganizationID == "" && len(matches) == 1 {
		c.OrganizationID = matches[0]
	}
	if c.OrganizationID == "" {
		return c, errors.New("organization selector must match exactly once: run ts orgs and select an ID")
	}
	matches = nil
	for p, err := range pages(func(after string) (*v1.Page[tsauth.Project], error) {
		p, err := u.Projects(ctx, c.OrganizationID, tsauth.PageOptions{Limit: 100, After: after})
		return (*v1.Page[tsauth.Project])(p), err
	}) {
		if err != nil {
			return c, err
		}
		if p.ID == project {
			c.ProjectID = p.ID
		}
		if p.Name == project {
			matches = append(matches, p.ID)
		}
	}
	if c.ProjectID == "" && len(matches) == 1 {
		c.ProjectID = matches[0]
	}
	if c.ProjectID == "" {
		return c, errors.New("project selector must match exactly once in the organization: run ts projects and select an ID")
	}
	return c, nil
}

func showCLIContext(path string, out io.Writer) error {
	c, err := readCLIContext(path)
	if err != nil {
		return err
	}
	return emitJSON(out, c)
}

func clearCLIContext(path string, out io.Writer) error {
	if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return emitJSON(out, struct {
		Cleared bool `json:"cleared"`
	}{true})
}

// selectCLIContext verifies every selector before replacing the saved context.
func selectCLIContext(ctx context.Context, u *tsauth.User, token string, operands []string, out io.Writer) error {
	var claims struct {
		Sub string `json:"sub"`
	}
	decodeClaims(token, &claims)
	c, err := resolveCLIContext(ctx, u, claims.Sub, operands[0], operands[1])
	if err != nil {
		return err
	}
	if len(operands) == 3 {
		s, err := open(ctx, u, token, c.OrganizationID, c.ProjectID)
		if err != nil {
			return err
		}
		if _, err := s.tb.Database(c.ProjectID, operands[2]).Info(ctx); err != nil {
			return err
		}
		c.Database = operands[2]
	}
	path, err := contextPath()
	if err != nil {
		return err
	}
	if err := saveCLIContext(path, c); err != nil {
		return err
	}
	return emitJSON(out, c)
}

func usesDatabaseOperand(name string) bool {
	if name == "db create" || name == "db list" || name == "import" || name == "migrate new" || name == "backup destination" {
		return false
	}
	for _, prefix := range []string{"db ", "backup ", "collection ", "doc ", "schema ", "ttl ", "index ", "migrate ", "functions "} {
		if strings.HasPrefix(name, prefix) {
			return true
		}
	}
	return name == "sql" || name == "exec" || name == "shell" || name == "export" || name == "usage"
}
