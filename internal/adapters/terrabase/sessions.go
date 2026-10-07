package terrabase

import (
	"context"
	"errors"
	"fmt"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/session"
	v1 "terrascale.tech/sdk/v1"
)

const sessionsCollection = "sessions"

type sessionsStore struct{ db *v1.Database }

type sessionDocument struct {
	Kind        string    `json:"kind"`
	TokenHash   string    `json:"token_hash"`
	AccountID   string    `json:"account_id"`
	UserID      string    `json:"user_id"`
	AuthVersion uint64    `json:"auth_version"`
	ExpiresAt   time.Time `json:"expires_at"`
	Refresh     string    `json:"refresh,omitempty"`
	RefreshAt   time.Time `json:"refresh_at,omitzero"`
}

func NewSessions(db *v1.Database) session.Store { return &sessionsStore{db: db} }

func (d sessionDocument) record() session.Record {
	return session.Record{AccountID: d.AccountID, UserID: d.UserID, AuthVersion: d.AuthVersion, ExpiresAt: d.ExpiresAt, Refresh: d.Refresh, RefreshAt: d.RefreshAt}
}

func newSessionDocument(key [32]byte, rec session.Record) sessionDocument {
	return sessionDocument{
		Kind: "session", TokenHash: digestText(key), AccountID: rec.AccountID, UserID: rec.UserID, AuthVersion: rec.AuthVersion,
		ExpiresAt: rec.ExpiresAt.UTC(), Refresh: rec.Refresh, RefreshAt: rec.RefreshAt.UTC(),
	}
}

// sameSession compares records by value, since a time read back from a
// document carries no monotonic reading or the caller's location.
func sameSession(a, b session.Record) bool {
	return a.AccountID == b.AccountID && a.UserID == b.UserID && a.AuthVersion == b.AuthVersion && a.Refresh == b.Refresh &&
		a.ExpiresAt.Equal(b.ExpiresAt) && a.RefreshAt.Equal(b.RefreshAt)
}

func (d sessionDocument) validate(id string) error {
	if d.Kind != "session" || d.AccountID == "" || d.UserID == "" || d.ExpiresAt.IsZero() || v1.DocumentIDFor(sessionsCollection, d.TokenHash) != id {
		return documentError(fmt.Errorf("invalid session document %s", id))
	}
	_, err := parseDigest(d.TokenHash)
	return err
}

func (s *sessionsStore) Put(ctx context.Context, key [32]byte, rec session.Record) error {
	if rec.AccountID == "" || rec.UserID == "" || rec.ExpiresAt.IsZero() {
		return core.ErrInvalid
	}
	doc := newSessionDocument(key, rec)
	id := v1.DocumentIDFor(sessionsCollection, doc.TokenHash)
	return retryCAS(ctx, func() error {
		var old sessionDocument
		version, err := readDocument(ctx, s.db, sessionsCollection, id, &old)
		tx := s.db.Tx()
		if errors.Is(err, core.ErrNotFound) {
			tx.Create(sessionsCollection, id, doc)
		} else if err != nil {
			return err
		} else {
			if err := old.validate(id); err != nil {
				return err
			}
			tx.Put(sessionsCollection, id, doc, v1.IfMatch(version))
		}
		return commitDocuments(ctx, tx)
	})
}

func (s *sessionsStore) Get(ctx context.Context, key [32]byte, now time.Time) (session.Record, error) {
	id := v1.DocumentIDFor(sessionsCollection, digestText(key))
	var doc sessionDocument
	_, err := readDocument(ctx, s.db, sessionsCollection, id, &doc)
	if errors.Is(err, core.ErrNotFound) {
		return session.Record{}, session.ErrNotFound
	}
	if err != nil {
		return session.Record{}, err
	}
	if err := doc.validate(id); err != nil {
		return session.Record{}, err
	}
	if !now.Before(doc.ExpiresAt) {
		return session.Record{}, session.ErrNotFound
	}
	return doc.record(), nil
}

// Replace commits next with if_match on the version that still holds old. An
// expired next deletes the session the same way.
func (s *sessionsStore) Replace(ctx context.Context, key [32]byte, old, next session.Record) error {
	id := v1.DocumentIDFor(sessionsCollection, digestText(key))
	return retryCAS(ctx, func() error {
		var doc sessionDocument
		version, err := readDocument(ctx, s.db, sessionsCollection, id, &doc)
		if errors.Is(err, core.ErrNotFound) {
			return session.ErrNotFound
		}
		if err != nil {
			return err
		}
		if err := doc.validate(id); err != nil {
			return err
		}
		if !sameSession(doc.record(), old) {
			return session.ErrNotFound
		}
		tx := s.db.Tx()
		if next.ExpiresAt.After(time.Now()) {
			tx.Put(sessionsCollection, id, newSessionDocument(key, next), v1.IfMatch(version))
		} else {
			tx.Delete(sessionsCollection, id, v1.IfMatch(version))
		}
		return commitDocuments(ctx, tx)
	})
}

func (s *sessionsStore) Delete(ctx context.Context, key [32]byte) error {
	return deleteDocument(ctx, s.db, sessionsCollection, v1.DocumentIDFor(sessionsCollection, digestText(key)))
}

func sessionUserFilter(accountID, userID string) v1.Filter {
	return v1.And(v1.Eq("/kind", "session"), v1.Eq("/account_id", accountID), v1.Eq("/user_id", userID))
}

func (s *sessionsStore) DeleteUser(ctx context.Context, accountID, userID string) error {
	// Delete one page at a time; credential-version checks protect concurrent
	// issuance during credential revocation. No page can exceed 1024 ops.
	for {
		empty := false
		err := retryCAS(ctx, func() error {
			page, err := s.db.Collection(sessionsCollection).Query(ctx, v1.Query{Filter: sessionUserFilter(accountID, userID), Limit: 1000})
			if err != nil {
				return documentError(err)
			}
			empty = len(page.Items) == 0 && page.NextCursor == ""
			tx := s.db.Tx()
			for _, doc := range page.Items {
				if err := validateDocument(&doc); err != nil {
					return err
				}
				tx.Delete(sessionsCollection, doc.ID, v1.IfMatch(doc.Version))
			}
			// Bounded scans can return an empty page with a cursor; consume those
			// through the paging helper before retrying the first page.
			if len(page.Items) == 0 && page.NextCursor != "" {
				err = queryDocuments(ctx, s.db, sessionsCollection, v1.Query{Filter: sessionUserFilter(accountID, userID), Cursor: page.NextCursor, Limit: 1000}, func(doc v1.Document) error {
					return deleteDocument(ctx, s.db, sessionsCollection, doc.ID)
				})
				if err != nil {
					return err
				}
				empty = true
			}
			return commitDocuments(ctx, tx)
		})
		if err != nil || empty {
			return err
		}
	}
}

func (s *sessionsStore) ListUser(ctx context.Context, accountID, userID string, now time.Time) ([]session.Entry, error) {
	var entries []session.Entry
	err := queryDocuments(ctx, s.db, sessionsCollection, v1.Query{Filter: sessionUserFilter(accountID, userID)}, func(doc v1.Document) error {
		var rec sessionDocument
		if err := doc.Decode(&rec); err != nil {
			return documentError(err)
		}
		if err := rec.validate(doc.ID); err != nil {
			return err
		}
		if !now.Before(rec.ExpiresAt) {
			return nil
		}
		key, err := parseDigest(rec.TokenHash)
		if err != nil {
			return err
		}
		entries = append(entries, session.Entry{Key: key, Record: rec.record()})
		return nil
	})
	if err != nil {
		return nil, err
	}
	return entries, nil
}
