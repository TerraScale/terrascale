package v1

import (
	"fmt"
	"regexp"

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
// It is safe for concurrent use. It panics if ULID generation fails.
func NewID() string { return entityid.New() }

// ValidateID checks the shared TerraScale entity ID contract.
func ValidateID(id string) error { return entityid.Check(id) }

func checkID(id string) error { return ValidateID(id) }

// checkVersion treats versions as opaque (tbv1-…, tbv2-…, later formats) and
// only rejects what cannot be sent as a quoted ETag.
func checkVersion(v string) error {
	if !versionPattern.MatchString(v) {
		return fmt.Errorf("terrabase: version %q must be visible ASCII without spaces or quotes", v)
	}
	return nil
}
