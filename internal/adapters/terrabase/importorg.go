package terrabase

import (
	"context"
	"errors"
	"fmt"
	"strings"
	"time"

	"terrascale.tech/dashboard/internal/core"
	v1 "terrascale.tech/sdk/v1"
)

// OIDCPendingPassword marks an imported owner that has no password: no
// password verifies against it, and the first OIDC sign-in links the account
// by email (TS-492).
const OIDCPendingPassword = "oidc-pending"

// ImportOrganization mirrors one TerraBase Management organization, matched by
// ID or name, with its projects and databases into the account of ownerEmail,
// creating that account when it does not exist. TerraBase IDs are kept, so
// rerunning it updates names and adds new resources without duplicates.
func ImportOrganization(ctx context.Context, store core.Store, m *v1.ManagementUser, orgRef, ownerEmail string) (core.Organization, error) {
	remote, err := findOrganization(ctx, m, orgRef)
	if err != nil {
		return core.Organization{}, err
	}
	projects, err := all(func(o v1.PageOptions) (*v1.Page[v1.Project], error) { return m.Projects(ctx, remote.ID, o) })
	if err != nil {
		return core.Organization{}, fmt.Errorf("list projects: %w", err)
	}
	databases := make(map[string][]v1.ManagedDatabase, len(projects))
	for _, p := range projects {
		if databases[p.ID], err = all(func(o v1.PageOptions) (*v1.Page[v1.ManagedDatabase], error) {
			return m.Databases(ctx, remote.ID, p.ID, o)
		}); err != nil {
			return core.Organization{}, fmt.Errorf("list databases of %s: %w", p.ID, err)
		}
	}
	account, err := ownerAccount(ctx, store, ownerEmail)
	if err != nil {
		return core.Organization{}, err
	}
	var imported core.Organization
	err = store.Update(ctx, account.ID, func(a *core.Account) error {
		now := time.Now().UTC()
		org, ok := a.Organizations[remote.ID]
		if !ok {
			// ponytail: internal organizations start on Business so mirrored
			// databases never trip the Free limit; billing stays with the dashboard.
			org = core.Organization{
				ID: remote.ID, Kind: "business", Status: "active", OwnerUserID: a.User.ID,
				PlanID: "plan_business", BillingStatus: "active", CreatedAt: remote.CreatedAt,
				Projects: map[string]core.Project{}, Members: map[string]core.Member{},
				Invitations: map[string]core.Invitation{}, CheckoutSessions: map[string]core.CheckoutSession{},
			}
		}
		org.Name, org.Slug = remote.Name, slug(remote.Name)
		if _, ok := org.Members[a.User.ID]; !ok {
			org.Members[a.User.ID] = core.Member{UserID: a.User.ID, Email: a.User.Email, Name: a.User.Name, Role: "owner", Status: "active", CreatedAt: now}
		}
		for _, rp := range projects {
			project, ok := org.Projects[rp.ID]
			if !ok {
				project = core.Project{
					ID: rp.ID, OrganizationID: org.ID, Status: "active", CreatedAt: rp.CreatedAt,
					Databases: map[string]core.Database{}, APIKeys: map[string]core.APIKey{},
					SavedQueries: map[string]core.SavedQuery{}, SharedResultConfigs: map[string]core.SharedResultConfig{},
					BackupPolicies: map[string]core.BackupPolicy{},
				}
			}
			project.Name, project.Slug = rp.Name, slug(rp.Name)
			for _, rd := range databases[rp.ID] {
				db, ok := project.Databases[rd.ID]
				if !ok {
					db = core.Database{ID: rd.ID, ProjectID: project.ID, Status: "ready", CreatedAt: rd.CreatedAt}
				}
				db.Name = rd.Name
				project.Databases[rd.ID] = db
			}
			org.Projects[rp.ID] = project
		}
		a.Organizations[org.ID] = org
		imported = org
		return nil
	})
	return imported, err
}

func findOrganization(ctx context.Context, m *v1.ManagementUser, ref string) (v1.Organization, error) {
	orgs, err := all(func(o v1.PageOptions) (*v1.Page[v1.Organization], error) { return m.Organizations(ctx, o) })
	if err != nil {
		return v1.Organization{}, fmt.Errorf("list organizations: %w", err)
	}
	var match []v1.Organization
	for _, org := range orgs {
		if org.ID == ref || org.Name == ref {
			match = append(match, org)
		}
	}
	if len(match) != 1 {
		return v1.Organization{}, fmt.Errorf("organization %q: %d matches, want 1", ref, len(match))
	}
	return match[0], nil
}

func ownerAccount(ctx context.Context, store core.Store, email string) (core.Account, error) {
	account, err := store.FindByEmail(ctx, email)
	if !errors.Is(err, core.ErrNotFound) {
		return account, err
	}
	name, _, _ := strings.Cut(email, "@")
	account, err = store.CreateAccount(ctx, core.User{Email: email, Name: name, PasswordHash: OIDCPendingPassword})
	if errors.Is(err, core.ErrConflict) {
		return store.FindByEmail(ctx, email)
	}
	return account, err
}

func all[T any](page func(v1.PageOptions) (*v1.Page[T], error)) ([]T, error) {
	var items []T
	for opts := (v1.PageOptions{Limit: 100}); ; {
		p, err := page(opts)
		if err != nil {
			return nil, err
		}
		items = append(items, p.Items...)
		if p.Next == "" {
			return items, nil
		}
		opts.After = p.Next
	}
}

func slug(name string) string {
	return strings.Trim(strings.Map(func(r rune) rune {
		switch {
		case r >= 'a' && r <= 'z', r >= '0' && r <= '9':
			return r
		case r >= 'A' && r <= 'Z':
			return r + 'a' - 'A'
		}
		return '-'
	}, name), "-")
}
