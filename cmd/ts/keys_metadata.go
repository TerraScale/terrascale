package main

import (
	"cmp"
	"context"
	"errors"
	"fmt"
	"io"
	"strings"
	"text/tabwriter"
	"time"

	"terrascale.tech/sdk/tsauth"
)

// Buffer all pages so a later API failure cannot leave a successful-looking list.
func listKeyMetadata(ctx context.Context, s *session, out io.Writer) error {
	items := make([]tsauth.APIKey, 0)
	seen := make(map[string]bool)
	after := ""
	for {
		page, err := s.user.APIKeys(ctx, s.org, s.project, tsauth.PageOptions{Limit: 100, After: after})
		if err != nil {
			return keyOperationError(err)
		}
		if page == nil || page.Items == nil {
			return errors.New("API key listing did not include an items array.")
		}
		items = append(items, page.Items...)
		if page.Next == "" {
			break
		}
		if seen[page.Next] {
			return errors.New("API key listing returned a repeated cursor.")
		}
		seen[page.Next], after = true, page.Next
	}
	if jsonMode(out) {
		return keyOutputError(emitJSON(out, items)) // APIKey has no secret field.
	}
	w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
	if _, err := fmt.Fprintln(w, "NAME\tID\tDATABASE\tSCOPES\tEXPIRES\tALLOWED CIDRS\tCREATED BY\tCREATED AT\tLAST USED"); err != nil {
		return keyOutputError(err)
	}
	for _, k := range items {
		expires, used := "never", "never"
		if k.ExpiresAt != nil {
			expires = k.ExpiresAt.UTC().Format(time.RFC3339)
		}
		if k.LastUsedAt != nil {
			used = k.LastUsedAt.UTC().Format(time.RFC3339)
		}
		if _, err := fmt.Fprintf(w, "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n", k.Name, k.ID,
			cmp.Or(k.Database, "*"), strings.Join(k.Scopes, ","), expires,
			cmp.Or(strings.Join(k.AllowedCIDRs, ","), "*"), k.CreatedBy, k.CreatedAt.UTC().Format(time.RFC3339), used); err != nil {
			return keyOutputError(err)
		}
	}
	return keyOutputError(w.Flush())
}
