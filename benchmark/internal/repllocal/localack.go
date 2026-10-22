package repllocal

import (
	"context"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/oklog/ulid/v2"
)

func (c *Cluster) LocalAck(ctx context.Context) (map[string]float64, error) {
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	id := ulid.Make().String()
	dir := filepath.Join(c.Work, "gru")
	path := filepath.Join(dir, "sample.request")
	if err := os.WriteFile(path+".partial", []byte(id), 0o600); err != nil {
		return nil, err
	}
	if err := os.Rename(path+".partial", path); err != nil {
		return nil, err
	}
	for {
		var s struct {
			ID     string   `json:"id"`
			PID    int      `json:"pid"`
			Count  float64  `json:"local_ack_count"`
			P50    *float64 `json:"local_ack_p50_ms"`
			P99    *float64 `json:"local_ack_p99_ms"`
			Capped float64  `json:"local_ack_capped_count"`
		}
		if b, err := os.ReadFile(filepath.Join(dir, "sample.json")); err == nil && json.Unmarshal(b, &s) == nil && s.ID == id {
			if s.PID != c.Nodes["gru"].Target.PID {
				return nil, fmt.Errorf("local acknowledgement process changed")
			}
			values := map[string]float64{"local_ack.count": s.Count, "local_ack.capped": s.Capped}
			if s.P50 != nil && s.P99 != nil {
				values["local_ack.p50_ms"], values["local_ack.p99_ms"] = *s.P50, *s.P99
			}
			return values, nil
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
}
