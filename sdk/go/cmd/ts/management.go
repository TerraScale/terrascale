package main

import (
	"context"
	"fmt"
	"io"
	"text/tabwriter"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

// scopedKeyRequest preserves the lifetime, network and database restrictions
// while selecting only scopes the customer requested. The server checks grants.
func scopedKeyRequest(operands []string, expires, allowCIDR, scope string) (tsauth.APIKeyRequest, error) {
	req, err := newKeyRequest(operands, expires, allowCIDR)
	if err != nil {
		return req, err
	}
	switch scope {
	case "read":
		req.Scopes = []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDocumentsRead}
	case "", "read-write":
		// Preserve the existing default; read access is an explicit choice.
	default:
		return tsauth.APIKeyRequest{}, fmt.Errorf("-scope must be read or read-write: %w", errUsage)
	}
	return req, nil
}

// organizationInventory includes organizations without projects. Fetch all pages
// before rendering, so a failed request cannot leave a successful-looking list.
func organizationInventory(ctx context.Context, u *tsauth.User) ([]tsauth.Organization, error) {
	items := make([]tsauth.Organization, 0)
	for item, err := range pages(func(after string) (*v1.Page[tsauth.Organization], error) {
		p, err := u.Organizations(ctx, tsauth.PageOptions{Limit: 100, After: after})
		return (*v1.Page[tsauth.Organization])(p), err
	}) {
		if err != nil {
			return nil, err
		}
		items = append(items, item)
	}
	return items, nil
}

func listOrganizations(ctx context.Context, u *tsauth.User, out io.Writer) error {
	items, err := organizationInventory(ctx, u)
	if err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, items)
	}
	w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
	if _, err := fmt.Fprintln(w, "NAME\tID\tROLE"); err != nil {
		return err
	}
	for _, item := range items {
		if _, err := fmt.Fprintf(w, "%s\t%s\t%s\n", item.Name, item.ID, item.Role); err != nil {
			return err
		}
	}
	return w.Flush()
}
