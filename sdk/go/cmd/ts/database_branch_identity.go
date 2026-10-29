package main

import (
	"context"
	"fmt"
	"io"

	v1 "terrascale.tech/sdk/v1"
)

func branchExpectedDatabase(ctx context.Context, db *v1.Database, name, expectedID string, out io.Writer) error {
	branch, err := db.BranchIfID(ctx, name, expectedID)
	if err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, branch)
	}
	_, err = fmt.Fprintf(out, "Branched %s into %s (%s)\n", db.Name(), branch.Name, branch.ID)
	return err
}
