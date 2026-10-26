package main

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"strings"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

// managementConsent is the CLI's terminal/explicit-yes consent boundary. The
// callback must display this resolved identity, and return an error on refusal.
type managementConsent func(kind, name, id string) error

func confirmManagementTarget(action, name, id string, yes bool, in io.Reader, out io.Writer, tty bool) error {
	if yes {
		return nil
	}
	if !tty {
		return errors.New("resource change requires a terminal: pass -yes before the command to explicitly consent")
	}
	if _, err := fmt.Fprintf(out, "%s %q (ID %s).\nType the ID to confirm: ", action, name, id); err != nil {
		return err
	}
	line, err := bufio.NewReader(in).ReadString('\n')
	if err != nil || strings.TrimSpace(line) != id {
		return errors.New("resource change cancelled: confirmation ID did not match")
	}
	return nil
}

func deleteOrganizationLifecycle(ctx context.Context, u *tsauth.User, selector string, consent managementConsent, out io.Writer) error {
	o, err := selectOrganization(ctx, u, selector)
	if err != nil {
		return err
	}
	if err := consent("organization", o.Name, o.ID); err != nil {
		return err
	}
	// ts-auth enforces owner role and refuses organizations with projects.
	if err := u.DeleteOrganization(ctx, o.ID); err != nil {
		return err
	}
	return emitJSON(out, struct {
		Deleted        bool   `json:"deleted"`
		OrganizationID string `json:"organizationId"`
	}{true, o.ID})
}

// The caller resolves p through selectProject and opens tb with that project's
// current sign-in grant. Refuse deletion unless the full database list is empty.
// This preflight cannot serialize concurrent database creation with ts-auth's
// deletion; it does not promise TerraBase tenant or billing cleanup.
func deleteProjectLifecycle(ctx context.Context, u *tsauth.User, tb *v1.Client, p *tsauth.Project, consent managementConsent, out io.Writer) error {
	for _, err := range pages(func(after string) (*v1.Page[v1.ManagedDatabase], error) {
		return tb.Databases(ctx, p.ID, v1.PageOptions{Limit: 1, After: after})
	}) {
		if err != nil {
			return err
		}
		return errors.New("project still has TerraBase databases: delete them explicitly before deleting the project")
	}
	if err := consent("project", p.Name, p.ID); err != nil {
		return err
	}
	result, err := u.DeleteProject(ctx, p.OrganizationID, p.ID)
	if err != nil {
		return err
	}
	return emitJSON(out, struct {
		Deleted        bool   `json:"deleted"`
		Pending        bool   `json:"pending"`
		OrganizationID string `json:"organizationId"`
		ProjectID      string `json:"projectId"`
	}{result == tsauth.ProjectDeletionCompleted, result == tsauth.ProjectDeletionPending, p.OrganizationID, p.ID})
}
