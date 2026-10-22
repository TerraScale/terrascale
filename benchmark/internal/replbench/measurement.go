package replbench

import (
	"context"
	"fmt"
	"net/http"
	"time"

	"github.com/oklog/ulid/v2"
)

// AwaitReplication follows each writing origin with a sentinel and waits for every replica to serve it.
// It lets resource snapshots exclude the full document listing; Converge still supplies exact verification.
func (r *Runner) AwaitReplication(ctx context.Context) error {
	for _, origin := range r.regions {
		if !r.cfg.writes(origin.Name) {
			continue
		}
		id := ulid.Make().String()
		path := DocPath(r.collection+"-probe", id)
		if resp, ok, _ := r.call(ctx, origin, http.MethodPut, path, []byte(`{"barrier":true}`), "application/json"); !ok {
			return fmt.Errorf("measurement barrier write: HTTP %d", resp.Status)
		}
		for _, replica := range r.regions {
			if err := until(ctx, 2*time.Minute, func() bool {
				resp, err := replica.Do(ctx, http.MethodGet, path, nil, "")
				return err == nil && resp.Status == http.StatusOK
			}); err != nil {
				return fmt.Errorf("measurement barrier on %s: %w", replica.Name, err)
			}
		}
	}
	return nil
}
