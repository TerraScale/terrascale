package terrabase

import (
	"context"
	"encoding/hex"
	"errors"
	"fmt"

	"terrascale.tech/dashboard/internal/core"
	v1 "terrascale.tech/sdk/v1"
)

const casAttempts = 5

func documentError(err error) error {
	if err == nil {
		return nil
	}
	return fmt.Errorf("%w: documents: %w", core.ErrUnavailable, err)
}

func digestText(key [32]byte) string { return hex.EncodeToString(key[:]) }

func parseDigest(text string) ([32]byte, error) {
	var key [32]byte
	if len(text) != hex.EncodedLen(len(key)) {
		return key, documentError(errors.New("invalid stored digest"))
	}
	if _, err := hex.Decode(key[:], []byte(text)); err != nil {
		return key, documentError(err)
	}
	return key, nil
}

func validateDocument(doc *v1.Document) error {
	if doc.State != v1.StateValue || doc.Version == "" {
		return documentError(fmt.Errorf("invalid or conflicted document %s", doc.ID))
	}
	return nil
}

func readDocument(ctx context.Context, db *v1.Database, collection, id string, dst any) (string, error) {
	doc, err := db.Collection(collection).Get(ctx, id)
	if errors.Is(err, v1.ErrNotFound) {
		return "", core.ErrNotFound
	}
	if err != nil {
		return "", documentError(err)
	}
	if err := validateDocument(doc); err != nil {
		return "", err
	}
	if err := doc.Decode(dst); err != nil {
		return "", documentError(err)
	}
	return doc.Version, nil
}

func queryDocuments(ctx context.Context, db *v1.Database, collection string, query v1.Query, visit func(v1.Document) error) error {
	if query.Limit == 0 {
		query.Limit = 1000
	}
	for doc, err := range db.Collection(collection).Items(ctx, query) {
		if err != nil {
			return documentError(err)
		}
		if err := validateDocument(&doc); err != nil {
			return err
		}
		if err := visit(doc); err != nil {
			return err
		}
	}
	return nil
}

// commitDocuments never replays an uncertain write. SDK transient rejection
// retries are safe only when the transaction already has version conditions.
func commitDocuments(ctx context.Context, tx *v1.Tx) error {
	if tx.Len() == 0 {
		return nil
	}
	_, err := tx.Commit(ctx)
	return documentError(err)
}

func retryCAS(ctx context.Context, change func() error) error {
	for range casAttempts {
		if err := ctx.Err(); err != nil {
			return documentError(err)
		}
		err := change()
		if !errors.Is(err, v1.ErrPreconditionFailed) || errors.Is(err, v1.ErrCommitUncertain) {
			return err
		}
	}
	return documentError(errors.New("CAS contention exhausted"))
}

func deleteDocument(ctx context.Context, db *v1.Database, collection, id string) error {
	return retryCAS(ctx, func() error {
		var value map[string]any
		version, err := readDocument(ctx, db, collection, id, &value)
		if errors.Is(err, core.ErrNotFound) {
			return nil
		}
		if err != nil {
			return err
		}
		return commitDocuments(ctx, db.Tx().Delete(collection, id, v1.IfMatch(version)))
	})
}
