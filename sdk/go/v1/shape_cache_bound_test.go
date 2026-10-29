package v1

import (
	"context"
	"fmt"
	"testing"
	"time"
)

func TestShapeCacheCardinalityAndLazyEpochExpiry(t *testing.T) {
	now := time.Unix(60001, 0)
	cache := shapeKeyCache{now: func() time.Time { return now }}
	calls := 0
	fetch := func() (*ShapeKey, error) {
		calls++
		return &ShapeKey{key: func() []byte { return make([]byte, 32) }}, nil
	}
	for i := 0; i < maxShapeKeys+10; i++ {
		id := shapeKeyIdentity{scope: "tenant/collection", handle: fmt.Sprint(i), epoch: 100}
		if _, err := cache.get(t.Context(), id, fetch); err != nil {
			t.Fatal(err)
		}
		if len(cache.entries) > maxShapeKeys {
			t.Fatal("cache exceeded fixed bound")
		}
	}
	if calls != maxShapeKeys+10 {
		t.Fatal(calls)
	}
	now = time.Unix(60601, 0)
	if _, err := cache.get(t.Context(), shapeKeyIdentity{scope: "tenant/collection", epoch: 101}, fetch); err != nil {
		t.Fatal(err)
	}
	if len(cache.entries) != 1 {
		t.Fatalf("expired entries=%d", len(cache.entries))
	}
	cache.evict("tenant/collection")
	if len(cache.entries) != 0 {
		t.Fatal("scope eviction retained entries")
	}
}

func TestShapeCacheSaturationDoesNotEvictPendingLoads(t *testing.T) {
	cache := shapeKeyCache{entries: make(map[shapeKeyIdentity]*shapeKeyEntry)}
	for i := 0; i < maxShapeKeys; i++ {
		cache.entries[shapeKeyIdentity{handle: fmt.Sprint(i)}] = &shapeKeyEntry{ready: make(chan struct{})}
	}
	_, err := cache.get(context.Background(), shapeKeyIdentity{handle: "new"}, func() (*ShapeKey, error) { t.Error("started excess work"); return nil, nil })
	if err != ErrShapeBusy || len(cache.entries) != maxShapeKeys {
		t.Fatalf("error=%v entries=%d", err, len(cache.entries))
	}
}
