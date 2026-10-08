package v1

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/base32"
	"fmt"
	"regexp"
	"time"
)

const ulidAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

var ulidBase32 = base32.NewEncoding(ulidAlphabet)

// DocumentIDFor returns the key itself for entity IDs and a deterministic ULID
// for natural keys. Natural keys are framed by collection to retain uniqueness.
func DocumentIDFor(collection, key string) string {
	if ulidPattern.MatchString(key) {
		return key
	}
	digest := sha256.Sum256([]byte(collection + "\x00" + key))
	return encodeULID(digest[:16])
}

// NewULID generates a canonical ULID using cryptographic randomness.
func NewULID() string {
	var id [16]byte
	rand.Read(id[:])
	ms := uint64(time.Now().UnixMilli())
	for i := 5; i >= 0; i-- {
		id[i] = byte(ms)
		ms >>= 8
	}
	return encodeULID(id[:])
}

func encodeULID(id []byte) string {
	var timestamp [10]byte
	ms := uint64(0)
	for _, b := range id[:6] {
		ms = ms<<8 | uint64(b)
	}
	for i := 9; i >= 0; i-- {
		timestamp[i] = ulidAlphabet[ms&31]
		ms >>= 5
	}
	return string(timestamp[:]) + ulidBase32.EncodeToString(id[6:16])
}

var (
	namePattern = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)
	ulidPattern = regexp.MustCompile(`^[0-7][0-9A-HJKMNP-TV-Z]{25}$`)
	// versionPattern is any non-empty RFC 9110 entity-tag value without obs-text.
	versionPattern = regexp.MustCompile(`^[\x21\x23-\x7e]+$`)
)

func checkName(kind, name string) error {
	if !namePattern.MatchString(name) {
		return fmt.Errorf("terrabase: %s name %q must match ^[a-z][a-z0-9_-]{0,62}$", kind, name)
	}
	return nil
}

// checkID accepts only canonical uppercase ULIDs.
func checkID(id string) error {
	if !ulidPattern.MatchString(id) {
		return fmt.Errorf("terrabase: document ID %q must be a canonical uppercase ULID", id)
	}
	return nil
}

// checkVersion treats versions as opaque (tbv1-…, tbv2-…, later formats) and
// only rejects what cannot be sent as a quoted ETag.
func checkVersion(v string) error {
	if !versionPattern.MatchString(v) {
		return fmt.Errorf("terrabase: version %q must be visible ASCII without spaces or quotes", v)
	}
	return nil
}
