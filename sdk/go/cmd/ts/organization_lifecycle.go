package main

import (
	"context"
	"errors"
	"fmt"
	"io"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

func selectOrganization(ctx context.Context, u *tsauth.User, selector string) (*tsauth.Organization, error) {
	items, err := organizationInventory(ctx, u)
	if err != nil {
		return nil, err
	}
	var matches []tsauth.Organization
	for _, item := range items {
		if item.ID == selector {
			return &item, nil
		}
		if selector == "" || item.Name == selector {
			matches = append(matches, item)
		}
	}
	if len(matches) == 0 && selector != "" {
		return nil, fmt.Errorf("no organization %q: run ts orgs", selector)
	}
	if len(matches) != 1 {
		return nil, errors.New("organization must match exactly once: run ts orgs and select an ID")
	}
	return &matches[0], nil
}

func selectProject(ctx context.Context, u *tsauth.User, org, selector string) (*tsauth.Project, error) {
	sequence := projects(ctx, u, "")
	if org != "" {
		o, err := selectOrganization(ctx, u, org)
		if err != nil {
			return nil, err
		}
		sequence = pages(func(after string) (*v1.Page[tsauth.Project], error) {
			p, err := u.Projects(ctx, o.ID, tsauth.PageOptions{Limit: 100, After: after})
			return (*v1.Page[tsauth.Project])(p), err
		})
	}
	var exact *tsauth.Project
	var matches []tsauth.Project
	for item, err := range sequence {
		if err != nil {
			return nil, err
		}
		if item.ID == selector {
			exact = &item
		}
		if item.Name == selector {
			matches = append(matches, item)
		}
	}
	if exact != nil {
		return exact, nil
	}
	if len(matches) == 0 && org != "" {
		return nil, fmt.Errorf("no project %q in organization %q: run ts projects", selector, org)
	}
	if len(matches) == 0 {
		return nil, fmt.Errorf("no project %q: run ts projects", selector)
	}
	if len(matches) != 1 {
		return nil, errors.New("project name matches several projects: run ts projects and select an ID, optionally with -org")
	}
	return &matches[0], nil
}

// organizationLifecycle uses sign-in management APIs; never exchange a key for
// organization administration. Server role checks apply to every mutation.
func organizationLifecycle(ctx context.Context, u *tsauth.User, command string, args []string, out io.Writer) error {
	if command == "orgs create" {
		result, err := u.CreateOrganization(ctx, args[0])
		if err != nil {
			return err
		}
		return emitJSON(out, result)
	}
	o, err := selectOrganization(ctx, u, args[0])
	if err != nil {
		return err
	}
	var result *tsauth.Organization
	switch command {
	case "orgs info":
		result, err = u.Organization(ctx, o.ID)
	case "orgs rename":
		result, err = u.RenameOrganization(ctx, o.ID, args[1])
	default:
		return errUsage
	}
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}

func projectLifecycle(ctx context.Context, u *tsauth.User, org, command string, args []string, out io.Writer) error {
	p, err := selectProject(ctx, u, org, args[0])
	if err != nil {
		return err
	}
	var result *tsauth.Project
	switch command {
	case "projects info":
		result, err = u.Project(ctx, p.OrganizationID, p.ID)
	case "projects rename":
		result, err = u.RenameProject(ctx, p.OrganizationID, p.ID, args[1])
	default:
		return errUsage
	}
	if err != nil {
		return err
	}
	return emitJSON(out, result)
}
