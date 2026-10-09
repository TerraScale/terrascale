package v1

import (
	"crypto/sha256"
	"encoding/binary"
	"fmt"
	"regexp"
	"strings"
	"unicode"
	"unicode/utf16"
	"unicode/utf8"

	"github.com/oklog/ulid/v2"

	"terrascale.tech/sdk/internal/entityid"
)

var (
	namePattern = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)
	// versionPattern is any non-empty RFC 9110 entity-tag value without obs-text.
	versionPattern = regexp.MustCompile(`^[\x21\x23-\x7e]+$`)
)

func checkName(kind, name string) error {
	if !namePattern.MatchString(name) {
		return fmt.Errorf("terrabase: %s name %q must match ^[a-z][a-z0-9_-]{0,62}$", kind, name)
	}
	return nil
}

// NewID returns a canonical uppercase ULID, monotonic within a millisecond.
// It is safe for concurrent use.
func NewID() string { return entityid.New() }

// ValidateID checks the shared TerraScale entity ID contract.
func ValidateID(id string) error { return entityid.Check(id) }

func checkID(id string) error { return ValidateID(id) }

// DocumentIDFor preserves canonical entity IDs and derives collection-scoped
// ULID addresses for natural keys. Length framing prevents ambiguous tuples.
func DocumentIDFor(collection, key string) string {
	if ValidateID(key) == nil {
		return key
	}
	h := sha256.New()
	for _, part := range []string{collection, key} {
		var size [8]byte
		binary.BigEndian.PutUint64(size[:], uint64(len(part)))
		h.Write(size[:])
		h.Write([]byte(part))
	}
	return ulid.ULID(h.Sum(nil)[:16]).String()
}

// ValidateDocumentKey checks raw key bounds without changing the key.
// Only generated keys and entity selectors follow the ULID contract.
func ValidateDocumentKey(id string) error {
	if id == "" || !utf8.ValidString(id) || len(utf16.Encode([]rune(id))) > 256 || strings.ContainsFunc(id, unicode.IsControl) {
		return fmt.Errorf("terrabase: document key must be 1 to 256 UTF-16 units without control characters")
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
