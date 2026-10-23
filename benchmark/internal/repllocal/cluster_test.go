package repllocal

import (
	"strconv"
	"testing"
)

func TestStartClusterRejectsNonpositiveDatabaseCount(t *testing.T) {
	t.Parallel()
	for _, databases := range []int{0, -1} {
		t.Run(strconv.Itoa(databases), func(t *testing.T) {
			t.Parallel()
			cluster, err := StartCluster(t.Context(), t.TempDir(), databases)
			if cluster != nil || err == nil {
				t.Fatalf("StartCluster(databases=%d) = %v, %v; want nil and a validation error", databases, cluster, err)
			}
		})
	}
}
