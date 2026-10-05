package terrabase

import (
	"context"
	"errors"
	"fmt"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/features/auth"
	v1 "terrascale.tech/sdk/v1"
)

const grantsCollection = "device_grants"

type deviceGrantsStore struct{ db *v1.Database }

type grantDocument struct {
	Kind         string         `json:"kind"`
	DeviceHash   string         `json:"device_hash"`
	UserCodeHash string         `json:"user_code_hash"`
	ClientKey    string         `json:"client_key"`
	ExpiresAt    time.Time      `json:"expires_at"`
	ExpiresNS    int64          `json:"expires_ns"`
	NextPoll     time.Time      `json:"next_poll"`
	Interval     time.Duration  `json:"interval"`
	Denied       bool           `json:"denied"`
	Redeeming    bool           `json:"redeeming"`
	Principal    core.Principal `json:"principal"`
	SessionHash  string         `json:"session_hash,omitempty"`
}

type grantClaim struct {
	Kind      string    `json:"kind"`
	OwnerID   string    `json:"owner_id"`
	ExpiresAt time.Time `json:"expires_at"`
}

func NewDeviceGrants(db *v1.Database) auth.DeviceGrants { return &deviceGrantsStore{db: db} }

func grantClaimID(codeHash string) string {
	return v1.DocumentIDFor(grantsCollection, "claim:user_code_hash:"+codeHash)
}

func encodeGrant(g auth.DeviceGrant) (grantDocument, error) {
	if g.Expires.IsZero() || g.Interval <= 0 {
		return grantDocument{}, core.ErrInvalid
	}
	if g.Session != "" {
		if _, err := parseDigest(g.Session); err != nil {
			return grantDocument{}, core.ErrInvalid
		}
	}
	return grantDocument{Kind: "device_grant", DeviceHash: digestText(g.DeviceHash), UserCodeHash: digestText(g.CodeHash), ClientKey: g.ClientKey, ExpiresAt: g.Expires.UTC(), ExpiresNS: g.Expires.UnixNano(), NextPoll: g.NextPoll.UTC(), Interval: g.Interval, Denied: g.Denied, Redeeming: g.Redeeming, Principal: g.Principal, SessionHash: g.Session}, nil
}

func (d grantDocument) decode(id string) (auth.DeviceGrant, error) {
	if d.Kind != "device_grant" || d.ExpiresAt.IsZero() || d.ExpiresNS != d.ExpiresAt.UnixNano() || d.Interval <= 0 || v1.DocumentIDFor(grantsCollection, d.DeviceHash) != id {
		return auth.DeviceGrant{}, documentError(fmt.Errorf("invalid device grant %s", id))
	}
	deviceHash, err := parseDigest(d.DeviceHash)
	if err != nil {
		return auth.DeviceGrant{}, err
	}
	codeHash, err := parseDigest(d.UserCodeHash)
	if err != nil {
		return auth.DeviceGrant{}, err
	}
	if d.SessionHash != "" {
		if _, err := parseDigest(d.SessionHash); err != nil {
			return auth.DeviceGrant{}, err
		}
	}
	return auth.DeviceGrant{DeviceHash: deviceHash, CodeHash: codeHash, ClientKey: d.ClientKey, Expires: d.ExpiresAt, NextPoll: d.NextPoll, Interval: d.Interval, Denied: d.Denied, Redeeming: d.Redeeming, Principal: d.Principal, Session: d.SessionHash}, nil
}

func validateGrantClaim(c grantClaim) error {
	if c.Kind != "unique_claim" || c.ExpiresAt.IsZero() {
		return documentError(errors.New("invalid device code claim"))
	}
	_, err := parseDigest(c.OwnerID)
	return err
}

func (s *deviceGrantsStore) Issue(ctx context.Context, grant auth.DeviceGrant, now time.Time, maxPerClient, maxTotal int) error {
	if maxPerClient <= 0 {
		return auth.ErrDeviceGrantLimit
	}
	if maxTotal <= 0 {
		return auth.ErrDeviceGrantCapacity
	}
	if !now.Before(grant.Expires) {
		return core.ErrInvalid
	}
	doc, err := encodeGrant(grant)
	if err != nil {
		return err
	}
	id := v1.DocumentIDFor(grantsCollection, doc.DeviceHash)
	claimID := grantClaimID(doc.UserCodeHash)
	live := v1.And(v1.Eq("/kind", "device_grant"), v1.Gt("/expires_ns", now.UnixNano()))
	clientMax, totalMax := uint64(maxPerClient-1), uint64(maxTotal-1)
	return retryCAS(ctx, func() error {
		var claim grantClaim
		claimVersion, err := readDocument(ctx, s.db, grantsCollection, claimID, &claim)
		if err != nil && !errors.Is(err, core.ErrNotFound) {
			return err
		}
		if err == nil {
			if err := validateGrantClaim(claim); err != nil {
				return err
			}
			if now.Before(claim.ExpiresAt) {
				return auth.ErrDeviceCodeTaken
			}
		}
		var old grantDocument
		_, err = readDocument(ctx, s.db, grantsCollection, id, &old)
		if err == nil {
			return core.ErrConflict
		}
		if !errors.Is(err, core.ErrNotFound) {
			return err
		}
		tx := s.db.Tx().
			AssertCount(grantsCollection, v1.And(live, v1.Eq("/client_key", doc.ClientKey)), v1.CountBounds{Lte: &clientMax}).
			AssertCount(grantsCollection, live, v1.CountBounds{Lte: &totalMax}).
			Create(grantsCollection, id, doc)
		nextClaim := grantClaim{Kind: "unique_claim", OwnerID: doc.DeviceHash, ExpiresAt: doc.ExpiresAt}
		if claimVersion == "" {
			tx.Create(grantsCollection, claimID, nextClaim)
		} else {
			tx.Put(grantsCollection, claimID, nextClaim, v1.IfMatch(claimVersion))
		}
		err = commitDocuments(ctx, tx)
		var apiErr *v1.APIError
		if errors.Is(err, v1.ErrPreconditionFailed) && errors.As(err, &apiErr) && apiErr.Op != nil {
			switch *apiErr.Op {
			case 0:
				return auth.ErrDeviceGrantLimit
			case 1:
				return auth.ErrDeviceGrantCapacity
			}
		}
		return err
	})
}

func (s *deviceGrantsStore) ByCode(ctx context.Context, codeHash [32]byte) (auth.DeviceGrant, error) {
	var claim grantClaim
	_, err := readDocument(ctx, s.db, grantsCollection, grantClaimID(digestText(codeHash)), &claim)
	if err != nil {
		return auth.DeviceGrant{}, err
	}
	if err := validateGrantClaim(claim); err != nil {
		return auth.DeviceGrant{}, err
	}
	id := v1.DocumentIDFor(grantsCollection, claim.OwnerID)
	var doc grantDocument
	if _, err := readDocument(ctx, s.db, grantsCollection, id, &doc); err != nil {
		return auth.DeviceGrant{}, err
	}
	g, err := doc.decode(id)
	if err != nil {
		return auth.DeviceGrant{}, err
	}
	if g.CodeHash != codeHash {
		return auth.DeviceGrant{}, documentError(errors.New("mismatched device code claim"))
	}
	return g, nil
}

func (s *deviceGrantsStore) remove(ctx context.Context, tx *v1.Tx, id, version string, doc grantDocument) error {
	tx.Delete(grantsCollection, id, v1.IfMatch(version))
	claimID := grantClaimID(doc.UserCodeHash)
	var claim grantClaim
	claimVersion, err := readDocument(ctx, s.db, grantsCollection, claimID, &claim)
	if errors.Is(err, core.ErrNotFound) {
		return nil
	}
	if err != nil {
		return err
	}
	if err := validateGrantClaim(claim); err != nil {
		return err
	}
	// An expired code may already have been reused by a new grant.
	if claim.OwnerID == doc.DeviceHash {
		tx.Delete(grantsCollection, claimID, v1.IfMatch(claimVersion))
	}
	return nil
}

func (s *deviceGrantsStore) Update(ctx context.Context, deviceHash [32]byte, change func(*auth.DeviceGrant) bool) error {
	id := v1.DocumentIDFor(grantsCollection, digestText(deviceHash))
	return retryCAS(ctx, func() error {
		var doc grantDocument
		version, err := readDocument(ctx, s.db, grantsCollection, id, &doc)
		if err != nil {
			return err
		}
		g, err := doc.decode(id)
		if err != nil {
			return err
		}
		codeHash, clientKey, expiry := g.CodeHash, g.ClientKey, g.Expires
		tx := s.db.Tx()
		if change(&g) {
			if err := s.remove(ctx, tx, id, version, doc); err != nil {
				return err
			}
		} else {
			// These identities and the original lifetime are fixed at issue.
			g.DeviceHash, g.CodeHash, g.ClientKey, g.Expires = deviceHash, codeHash, clientKey, expiry
			doc, err := encodeGrant(g)
			if err != nil {
				return err
			}
			tx.Put(grantsCollection, id, doc, v1.IfMatch(version))
		}
		return commitDocuments(ctx, tx)
	})
}

func (s *deviceGrantsStore) Delete(ctx context.Context, deviceHash [32]byte) error {
	id := v1.DocumentIDFor(grantsCollection, digestText(deviceHash))
	return retryCAS(ctx, func() error {
		var doc grantDocument
		version, err := readDocument(ctx, s.db, grantsCollection, id, &doc)
		if errors.Is(err, core.ErrNotFound) {
			return nil
		}
		if err != nil {
			return err
		}
		if _, err := doc.decode(id); err != nil {
			return err
		}
		tx := s.db.Tx()
		if err := s.remove(ctx, tx, id, version, doc); err != nil {
			return err
		}
		return commitDocuments(ctx, tx)
	})
}
