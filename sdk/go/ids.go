package sdk

import "terrascale.tech/sdk/internal/entityid"

// NewID returns a canonical uppercase ULID, monotonic within a millisecond.
// It is safe for concurrent use. It panics if ULID generation fails.
func NewID() string { return entityid.New() }

// ValidateID checks the shared TerraScale entity ID contract.
func ValidateID(id string) error { return entityid.Check(id) }
