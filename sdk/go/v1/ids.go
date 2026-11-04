package v1

import (
	"fmt"
	"regexp"

	"terrascale.tech/sdk/internal/entityid"
)

var namePattern = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)

func checkName(kind, name string) error {
	if !namePattern.MatchString(name) {
		return fmt.Errorf("terrabase: %s name %q must match ^[a-z][a-z0-9_-]{0,62}$", kind, name)
	}
	return nil
}

// checkDatabaseSelector accepts a canonical ULID or a current database name.
func checkDatabaseSelector(selector string) error {
	if checkID(selector) == nil {
		return nil
	}
	return checkName("database", selector)
}

// NewID returns a canonical uppercase ULID, monotonic within a millisecond.
// It is safe for concurrent use. It panics if ULID generation fails.
func NewID() string { return entityid.New() }

// ValidateID checks the shared TerraScale entity ID contract.
func ValidateID(id string) error { return entityid.Check(id) }

func checkID(id string) error { return ValidateID(id) }
