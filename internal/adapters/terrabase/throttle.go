package terrabase

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"math"
	"time"

	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/platform/throttle"
	v1 "terrascale.tech/sdk/v1"
)

const throttleCollection = "login_throttle"

type throttleStore struct{ db *v1.Database }

type throttleDocument struct {
	Kind    string    `json:"kind"`
	KeyHash string    `json:"key_hash"`
	Due     time.Time `json:"due"`
}

type throttleReservation struct {
	doc     throttleDocument
	version string
}

func NewThrottle(db *v1.Database) throttle.Store { return &throttleStore{db: db} }

func throttleHash(key string) string { return digestText(sha256.Sum256([]byte(key))) }

func validateBudget(bucket throttle.Bucket) error {
	if bucket.Key == "" || bucket.Budget.Burst < 1 || bucket.Budget.Interval <= 0 || int64(bucket.Budget.Burst-1) > math.MaxInt64/int64(bucket.Budget.Interval) {
		return core.ErrInvalid
	}
	return nil
}

func (s *throttleStore) read(ctx context.Context, hash string) (throttleReservation, error) {
	id := v1.DocumentIDFor(throttleCollection, hash)
	var rec throttleReservation
	version, err := readDocument(ctx, s.db, throttleCollection, id, &rec.doc)
	if errors.Is(err, core.ErrNotFound) {
		return throttleReservation{doc: throttleDocument{Kind: "throttle", KeyHash: hash}}, nil
	}
	if err != nil {
		return rec, err
	}
	if rec.doc.Kind != "throttle" || rec.doc.KeyHash != hash || rec.doc.Due.IsZero() {
		return rec, documentError(fmt.Errorf("invalid throttle document %s", id))
	}
	rec.version = version
	return rec, nil
}

func writeThrottle(tx *v1.Tx, rec throttleReservation) {
	id := v1.DocumentIDFor(throttleCollection, rec.doc.KeyHash)
	if rec.version == "" {
		tx.Create(throttleCollection, id, rec.doc)
	} else {
		tx.Put(throttleCollection, id, rec.doc, v1.IfMatch(rec.version))
	}
}

func (s *throttleStore) Reserve(ctx context.Context, now time.Time, buckets []throttle.Bucket) (time.Duration, bool, error) {
	for _, bucket := range buckets {
		if err := validateBudget(bucket); err != nil {
			return 0, false, err
		}
	}
	var wait time.Duration
	err := retryCAS(ctx, func() error {
		wait = 0
		records := make(map[string]throttleReservation, len(buckets))
		for _, bucket := range buckets {
			hash := throttleHash(bucket.Key)
			rec, ok := records[hash]
			if !ok {
				var err error
				rec, err = s.read(ctx, hash)
				if err != nil {
					return err
				}
				records[hash] = rec
			}
			tolerance := time.Duration(bucket.Budget.Burst-1) * bucket.Budget.Interval
			wait = max(wait, rec.doc.Due.Sub(now)-tolerance)
		}
		if wait > 0 {
			return nil
		}
		if len(records) > 1024 {
			return core.ErrInvalid
		}
		// Coalesce duplicate keys into one conditional write while retaining
		// one reservation per input bucket, as the memory store does.
		for _, bucket := range buckets {
			hash := throttleHash(bucket.Key)
			rec := records[hash]
			if rec.doc.Due.Before(now) {
				rec.doc.Due = now.UTC()
			}
			rec.doc.Due = rec.doc.Due.Add(bucket.Budget.Interval)
			records[hash] = rec
		}
		tx := s.db.Tx()
		for _, rec := range records {
			writeThrottle(tx, rec)
		}
		return commitDocuments(ctx, tx)
	})
	if err != nil {
		return 0, false, err
	}
	return wait, wait == 0, nil
}

func (s *throttleStore) Refund(ctx context.Context, now time.Time, bucket throttle.Bucket) error {
	if err := validateBudget(bucket); err != nil {
		return err
	}
	return retryCAS(ctx, func() error {
		rec, err := s.read(ctx, throttleHash(bucket.Key))
		if err != nil || rec.version == "" {
			return err
		}
		rec.doc.Due = rec.doc.Due.Add(-bucket.Budget.Interval)
		tx := s.db.Tx()
		if rec.doc.Due.After(now) {
			writeThrottle(tx, rec)
		} else {
			tx.Delete(throttleCollection, v1.DocumentIDFor(throttleCollection, rec.doc.KeyHash), v1.IfMatch(rec.version))
		}
		return commitDocuments(ctx, tx)
	})
}

func (s *throttleStore) Reset(ctx context.Context, key string) error {
	if key == "" {
		return core.ErrInvalid
	}
	return deleteDocument(ctx, s.db, throttleCollection, v1.DocumentIDFor(throttleCollection, throttleHash(key)))
}
