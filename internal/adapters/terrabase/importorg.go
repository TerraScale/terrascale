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

// MirrorOrganizations mirrors every TerraBase organization the user of m
// belongs to into the existing account of email. A
// platform admin's sign-in runs it, so every service's database in
// TerraBase (ts-auth, ts-dashboard, ts-router, ts-status, …) appears in the
// dashboard as soon as TerraBase holds it.
func MirrorOrganizations(ctx context.Context, store core.Store, m *v1.ManagementUser, email string) error {
	orgs, err := all(func(o v1.PageOptions) (*v1.Page[v1.Organization], error) { return m.Organizations(ctx, o) })
	if err != nil {
		return fmt.Errorf("list organizations: %w", err)
	}
	var errs []error
	for _, org := range orgs {
		if _, err := mirror(ctx, store, m, org, email); err != nil {
			errs = append(errs, fmt.Errorf("mirror %s: %w", org.ID, err))
		}
	}
	return errors.Join(errs...)
}

func mirror(ctx context.Context, store core.Store, m *v1.ManagementUser, remote v1.Organization, ownerEmail string) (core.Organization, error) {
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
	account, err := store.FindByEmail(ctx, ownerEmail)
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
				PlanID: "09XCWRH05BWWBADSBPTPDMFA7D", BillingStatus: "active", CreatedAt: remote.CreatedAt,
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
					ID: rp.ID, OrganizationID: org.ID, System: true, Status: "active", CreatedAt: rp.CreatedAt,
					Databases: map[string]core.Database{}, APIKeys: map[string]core.APIKey{},
					SavedQueries: map[string]core.SavedQuery{}, SharedResultConfigs: map[string]core.SharedResultConfig{},
					BackupPolicies: map[string]core.BackupPolicy{},
				}
			}
			project.Name, project.Slug = rp.Name, slug(rp.Name)
			for _, rd := range databases[rp.ID] {
				db, ok := project.Databases[rd.ID]
				if !ok {
					db = core.Database{ID: rd.ID, ProjectID: project.ID, System: project.System, Status: "ready", CreatedAt: rd.CreatedAt}
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
