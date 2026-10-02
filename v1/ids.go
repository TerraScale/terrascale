package v1

import (
	"crypto/sha1"
	"encoding/hex"
	"fmt"
	"regexp"
)

// namespaceURL is RFC 9562's NAMESPACE_URL, 6ba7b811-9dad-11d1-80b4-00c04fd430c8.
var namespaceURL = [16]byte{0x6b, 0xa7, 0xb8, 0x11, 0x9d, 0xad, 0x11, 0xd1, 0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8}

// DocumentIDFor returns the deterministic document ID for a natural key in a
// collection: uuidv5(uuidv5(NAMESPACE_URL, "terrabase:collection:" + collection), key).
func DocumentIDFor(collection, key string) string {
	ns := uuidV5(namespaceURL, "terrabase:collection:"+collection)
	return formatUUID(uuidV5(ns, key))
}

func uuidV5(ns [16]byte, name string) [16]byte {
	h := sha1.New()
	h.Write(ns[:])
	h.Write([]byte(name))
	var u [16]byte
	copy(u[:], h.Sum(nil))
	u[6] = u[6]&0x0f | 0x50
	u[8] = u[8]&0x3f | 0x80
	return u
}

func formatUUID(u [16]byte) string {
	var b [36]byte
	hex.Encode(b[0:8], u[0:4])
	b[8] = '-'
	hex.Encode(b[9:13], u[4:6])
	b[13] = '-'
	hex.Encode(b[14:18], u[6:8])
	b[18] = '-'
	hex.Encode(b[19:23], u[8:10])
	b[23] = '-'
	hex.Encode(b[24:], u[10:])
	return string(b[:])
}

var (
	namePattern    = regexp.MustCompile(`^[a-z][a-z0-9_-]{0,62}$`)
	idPattern      = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)
	versionPattern = regexp.MustCompile(`^tbv1-[0-9a-f]{32}$`)
	shapeIDPattern = regexp.MustCompile(`^[0-9a-f]{32}$`)
)

func checkName(kind, name string) error {
	if !namePattern.MatchString(name) {
		return fmt.Errorf("terrabase: %s name %q must match ^[a-z][a-z0-9_-]{0,62}$", kind, name)
	}
	return nil
}

func checkID(id string) error {
	if !idPattern.MatchString(id) {
		return fmt.Errorf("terrabase: document ID %q must be a lowercase hyphenated UUID", id)
	}
	return nil
}

func checkVersion(v string) error {
	if !versionPattern.MatchString(v) {
		return fmt.Errorf("terrabase: version %q must be tbv1- followed by 32 lowercase hex characters", v)
	}
	return nil
}
