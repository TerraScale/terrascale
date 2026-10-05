package throttle

import (
	"context"
	"sync"
	"time"
)

// Bucket names one budget's token bucket.
type Bucket struct {
	Key    string
	Budget Budget
}

// Store keeps each bucket's theoretical arrival time (GCRA): a bucket is full
// once that time has passed, and one Interval later per attempt taken. A
// bucket whose time has passed can be forgotten, which lets a database store
// expire it with a TTL.
type Store interface {
	// Reserve takes one attempt from every bucket, atomically, when none of
	// them must wait. Otherwise it takes nothing and reports the longest
	// wait.
	Reserve(ctx context.Context, now time.Time, buckets []Bucket) (time.Duration, bool, error)
	// Refund returns one attempt to the bucket.
	Refund(ctx context.Context, now time.Time, bucket Bucket) error
	// Reset refills the bucket.
	Reset(ctx context.Context, key string) error
}

const (
	// maxKeys bounds the memory a MemoryStore holds. Once it is reached,
	// buckets that have refilled are dropped, and a new client or account is
	// refused while every tracked bucket is still in use.
	maxKeys = 100_000
	// pruneEvery spaces out the scans for refilled buckets while the store
	// is full, so a flood of new keys cannot make every request scan.
	pruneEvery = time.Second
)

// MemoryStore is a Store in this process's memory, like the demo's sessions.
type MemoryStore struct {
	mu       sync.Mutex
	due      map[string]time.Time
	prunedAt time.Time
}

var _ Store = (*MemoryStore)(nil)

func NewMemoryStore() *MemoryStore {
	return &MemoryStore{due: make(map[string]time.Time)}
}

func (m *MemoryStore) Reserve(_ context.Context, now time.Time, buckets []Bucket) (time.Duration, bool, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	var wait time.Duration
	newKeys := make(map[string]struct{})
	for _, bucket := range buckets {
		wait = max(wait, m.waitLocked(bucket.Key, bucket.Budget, now))
		if _, tracked := m.due[bucket.Key]; !tracked {
			newKeys[bucket.Key] = struct{}{}
		}
	}
	if len(m.due)+len(newKeys) > maxKeys {
		m.pruneLocked(now)
		// Pruning may also have removed keys this batch will reserve again.
		for _, bucket := range buckets {
			if _, tracked := m.due[bucket.Key]; !tracked {
				newKeys[bucket.Key] = struct{}{}
			}
		}
		if len(m.due)+len(newKeys) > maxKeys {
			for _, bucket := range buckets {
				if _, untracked := newKeys[bucket.Key]; untracked {
					wait = max(wait, bucket.Budget.Interval)
				}
			}
		}
	}
	if wait > 0 {
		return wait, false, nil
	}
	for _, bucket := range buckets {
		m.takeLocked(bucket.Key, bucket.Budget, now)
	}
	return 0, true, nil
}

func (m *MemoryStore) Refund(_ context.Context, now time.Time, bucket Bucket) error {
	m.mu.Lock()
	defer m.mu.Unlock()
	if due, ok := m.due[bucket.Key]; ok {
		if due = due.Add(-bucket.Budget.Interval); due.After(now) {
			m.due[bucket.Key] = due
		} else {
			delete(m.due, bucket.Key)
		}
	}
	return nil
}

func (m *MemoryStore) Reset(_ context.Context, key string) error {
	m.mu.Lock()
	delete(m.due, key)
	m.mu.Unlock()
	return nil
}

// waitLocked reports how long key must wait for its next attempt.
func (m *MemoryStore) waitLocked(key string, budget Budget, now time.Time) time.Duration {
	due, tracked := m.due[key]
	if !tracked {
		return 0
	}
	tolerance := time.Duration(budget.Burst-1) * budget.Interval
	return max(0, due.Sub(now)-tolerance)
}

func (m *MemoryStore) takeLocked(key string, budget Budget, now time.Time) {
	due := m.due[key]
	if due.Before(now) {
		due = now
	}
	m.due[key] = due.Add(budget.Interval)
}

// pruneLocked drops refilled buckets, at most once per pruneEvery.
func (m *MemoryStore) pruneLocked(now time.Time) {
	if now.Sub(m.prunedAt) >= pruneEvery {
		m.prunedAt = now
		for key, due := range m.due {
			if !due.After(now) {
				delete(m.due, key)
			}
		}
	}
}
