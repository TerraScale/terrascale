package auth

import (
	"context"
	"errors"
	"sync"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

var (
	// ErrDeviceGrantLimit refuses a grant for a client that already holds
	// its share of unexpired grants.
	ErrDeviceGrantLimit = errors.New("device grant limit reached for this client")
	// ErrDeviceGrantCapacity refuses a grant while the store is full.
	ErrDeviceGrantCapacity = errors.New("device grant capacity reached")
	// ErrDeviceCodeTaken refuses a grant whose user code another grant uses.
	ErrDeviceCodeTaken = errors.New("device user code taken")
)

// DeviceGrant is one CLI device authorization. Only digests of the device
// code and the user code are kept.
type DeviceGrant struct {
	DeviceHash [32]byte
	CodeHash   [32]byte
	// ClientKey identifies the requesting client for the per-client quota.
	ClientKey string
	Expires   time.Time
	NextPoll  time.Time
	Interval  time.Duration
	Denied    bool
	// Redeeming marks a grant whose token is being issued, so concurrent
	// polls cannot redeem it twice.
	Redeeming bool
	// Principal and Session are set on approval. Session is the approving
	// browser session's core.CLIGrantSessions binding, re-resolved at
	// redemption.
	Principal core.Principal
	Session   string
}

// pending reports whether the grant still awaits a decision at now.
func (g DeviceGrant) pending(now time.Time) bool {
	return now.Before(g.Expires) && !g.Denied && g.Session == ""
}

// DeviceGrants stores device grants. Grants past Expires stay readable until
// the store drops them, so a late poll can still learn that its code expired.
type DeviceGrants interface {
	// Issue stores grant unless its client already holds maxPerClient
	// unexpired grants (ErrDeviceGrantLimit), maxTotal unexpired grants exist
	// (ErrDeviceGrantCapacity), or its user code is taken
	// (ErrDeviceCodeTaken).
	Issue(ctx context.Context, grant DeviceGrant, now time.Time, maxPerClient, maxTotal int) error
	// ByCode returns the grant with the user code digest, or
	// core.ErrNotFound.
	ByCode(ctx context.Context, codeHash [32]byte) (DeviceGrant, error)
	// Update applies change to the grant atomically, or fails with
	// core.ErrNotFound. change reports whether to delete the grant instead
	// of saving it, and may run more than once.
	Update(ctx context.Context, deviceHash [32]byte, change func(*DeviceGrant) (remove bool)) error
	// Delete removes the grant, if any.
	Delete(ctx context.Context, deviceHash [32]byte) error
}

// MemoryDeviceGrants keeps device grants in this process's memory, like the
// demo's sessions.
type MemoryDeviceGrants struct {
	mu     sync.Mutex
	grants map[[32]byte]DeviceGrant
	codes  map[[32]byte][32]byte
}

var _ DeviceGrants = (*MemoryDeviceGrants)(nil)

func NewMemoryDeviceGrants() *MemoryDeviceGrants {
	return &MemoryDeviceGrants{grants: make(map[[32]byte]DeviceGrant), codes: make(map[[32]byte][32]byte)}
}

// Issue also drops every expired grant, which bounds the memory grants hold.
func (m *MemoryDeviceGrants) Issue(_ context.Context, grant DeviceGrant, now time.Time, maxPerClient, maxTotal int) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	for key, existing := range m.grants {
		if !now.Before(existing.Expires) {
			m.removeLocked(key)
		}
	}
	clientGrants := 0
	for _, existing := range m.grants {
		if existing.ClientKey == grant.ClientKey {
			clientGrants++
		}
	}
	if clientGrants >= maxPerClient {
		return ErrDeviceGrantLimit
	}
	if len(m.grants) >= maxTotal {
		return ErrDeviceGrantCapacity
	}
	if _, taken := m.codes[grant.CodeHash]; taken {
		return ErrDeviceCodeTaken
	}
	m.grants[grant.DeviceHash] = grant
	m.codes[grant.CodeHash] = grant.DeviceHash
	return nil
}

func (m *MemoryDeviceGrants) ByCode(_ context.Context, codeHash [32]byte) (DeviceGrant, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	key, ok := m.codes[codeHash]
	if !ok {
		return DeviceGrant{}, core.ErrNotFound
	}
	grant, ok := m.grants[key]
	if !ok {
		return DeviceGrant{}, core.ErrNotFound
	}
	return grant, nil
}

func (m *MemoryDeviceGrants) Update(_ context.Context, deviceHash [32]byte, change func(*DeviceGrant) bool) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	grant, ok := m.grants[deviceHash]
	if !ok {
		return core.ErrNotFound
	}
	if change(&grant) {
		m.removeLocked(deviceHash)
		return nil
	}
	grant.DeviceHash, grant.CodeHash = deviceHash, m.grants[deviceHash].CodeHash
	m.grants[deviceHash] = grant
	return nil
}

func (m *MemoryDeviceGrants) Delete(_ context.Context, deviceHash [32]byte) error {
	m.mu.Lock()
	m.removeLocked(deviceHash)
	m.mu.Unlock()
	return nil
}

func (m *MemoryDeviceGrants) removeLocked(deviceHash [32]byte) {
	if grant, ok := m.grants[deviceHash]; ok {
		delete(m.codes, grant.CodeHash)
		delete(m.grants, deviceHash)
	}
}
