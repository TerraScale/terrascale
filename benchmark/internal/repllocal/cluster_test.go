package repllocal

import (
	"context"
	"strconv"
	"testing"
)

func TestStartClusterRejectsNonpositiveDatabaseCount(t *testing.T) {
	t.Parallel()
	for name, start := range map[string]func(context.Context, string, int) (*Cluster, error){
		"StartCluster": StartCluster,
		"StartPair":    StartPair,
	} {
		t.Run(name, func(t *testing.T) {
			t.Parallel()
			for _, databases := range []int{0, -1} {
				t.Run(strconv.Itoa(databases), func(t *testing.T) {
					t.Parallel()
					cluster, err := start(t.Context(), t.TempDir(), databases)
					if cluster != nil || err == nil {
						t.Fatalf("%s(databases=%d) = %v, %v; want nil and a validation error", name, databases, cluster, err)
					}
				})
			}
		})
	}
}
