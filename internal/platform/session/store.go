package session

import (
	"context"
	"errors"
	"maps"
	"sync"
	"time"
)

// ErrNotFound reports a session that does not exist or has expired.
var ErrNotFound = errors.New("session not found")

// Record is one live session. Its key is the SHA-256 digest of the bearer
// token, so the token itself is never stored.
type Record struct {
	AccountID   string
	UserID      string
	AuthVersion uint64
	ExpiresAt   time.Time
	// Refresh is what Options.Refresh rechecks the session with once
	// RefreshAt passes, such as a ts-auth refresh token. It is empty for a
	// session that is never rechecked.
	Refresh   string
	RefreshAt time.Time
}

// Entry is a stored session with its key.
type Entry struct {
	Key [32]byte
	Record
}

// Store keeps live sessions. A record whose ExpiresAt is not after now is
// treated as absent, which lets a database store expire records with a TTL.
type Store interface {
	// Put stores rec under key.
	Put(ctx context.Context, key [32]byte, rec Record) error
	// Get returns the unexpired record under key, or ErrNotFound.
	Get(ctx context.Context, key [32]byte, now time.Time) (Record, error)
	// Replace stores next under key only if key still holds old, and
	// returns ErrNotFound otherwise, so a change computed from an earlier
	// Get never brings back a session deleted or replaced since. An expired
	// next ends the session.
	Replace(ctx context.Context, key [32]byte, old, next Record) error
	// Delete removes the record under key, if any.
	Delete(ctx context.Context, key [32]byte) error
	// DeleteUser removes every session of the user in the account.
	DeleteUser(ctx context.Context, accountID, userID string) error
	// ListUser returns the user's unexpired sessions in the account.
	ListUser(ctx context.Context, accountID, userID string, now time.Time) ([]Entry, error)
}

// MemoryStore is a Store in this process's memory, the demo's session store.
type MemoryStore struct {
	mu    sync.RWMutex
	items map[[32]byte]Record
}

var _ Store = (*MemoryStore)(nil)

func NewMemoryStore() *MemoryStore {
	return &MemoryStore{items: make(map[[32]byte]Record)}
}

// Put also drops every expired record, which bounds the memory sessions hold.
func (m *MemoryStore) Put(_ context.Context, key [32]byte, rec Record) error {
	now := time.Now()
	m.mu.Lock()
	defer m.mu.Unlock()
	maps.DeleteFunc(m.items, func(_ [32]byte, item Record) bool { return !now.Before(item.ExpiresAt) })
	m.items[key] = rec
	return nil
}

func (m *MemoryStore) Get(_ context.Context, key [32]byte, now time.Time) (Record, error) {
	m.mu.RLock()
	item, ok := m.items[key]
	m.mu.RUnlock()
	if !ok {
		return Record{}, ErrNotFound
	}
	if !now.Before(item.ExpiresAt) {
		m.mu.Lock()
		delete(m.items, key)
		m.mu.Unlock()
		return Record{}, ErrNotFound
	}
	return item, nil
}

func (m *MemoryStore) Replace(_ context.Context, key [32]byte, old, next Record) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if current, ok := m.items[key]; !ok || current != old {
		return ErrNotFound
	}
	m.items[key] = next
	return nil
}

func (m *MemoryStore) Delete(_ context.Context, key [32]byte) error {
	m.mu.Lock()
	delete(m.items, key)
	m.mu.Unlock()
	return nil
}

func (m *MemoryStore) DeleteUser(_ context.Context, accountID, userID string) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	maps.DeleteFunc(m.items, func(_ [32]byte, item Record) bool {
		return item.AccountID == accountID && item.UserID == userID
	})
	return nil
}

func (m *MemoryStore) ListUser(_ context.Context, accountID, userID string, now time.Time) ([]Entry, error) {
	var result []Entry
	m.mu.RLock()
	defer m.mu.RUnlock()
	for key, item := range m.items {
		if item.AccountID == accountID && item.UserID == userID && now.Before(item.ExpiresAt) {
			result = append(result, Entry{Key: key, Record: item})
		}
	}
	return result, nil
}
