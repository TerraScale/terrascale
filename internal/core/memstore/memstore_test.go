package memstore

import (
	"terrascale.tech/dashboard/internal/core"
	"terrascale.tech/dashboard/internal/core/storetest"
	"testing"
)

func TestConformance(t *testing.T) {
	storetest.Run(t, func(*testing.T) core.Store { return New() })
}
