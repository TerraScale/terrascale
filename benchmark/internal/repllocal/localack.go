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
	b, err := c.sampleWorker(ctx, "gru")
	if err != nil {
		return nil, err
	}
	var s struct {
		Count  float64  `json:"local_ack_count"`
		P50    *float64 `json:"local_ack_p50_ms"`
		P99    *float64 `json:"local_ack_p99_ms"`
		Capped float64  `json:"local_ack_capped_count"`
	}
	if err := json.Unmarshal(b, &s); err != nil {
		return nil, err
	}
	values := map[string]float64{"local_ack.count": s.Count, "local_ack.capped": s.Capped}
	if s.P50 != nil && s.P99 != nil {
		values["local_ack.p50_ms"], values["local_ack.p99_ms"] = *s.P50, *s.P99
	}
	return values, nil
}

func (c *Cluster) sampleWorker(ctx context.Context, region string) ([]byte, error) {
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	id := ulid.Make().String()
	dir := filepath.Join(c.Work, region)
	path := filepath.Join(dir, "sample.request")
	if err := os.WriteFile(path+".partial", []byte(id), 0o600); err != nil {
		return nil, err
	}
	if err := os.Rename(path+".partial", path); err != nil {
		return nil, err
	}
	for {
		var s struct {
			ID  string `json:"id"`
			PID int    `json:"pid"`
		}
		if b, err := os.ReadFile(filepath.Join(dir, "sample.json")); err == nil && json.Unmarshal(b, &s) == nil && s.ID == id {
			if s.PID != c.Nodes[region].Target.PID {
				return nil, fmt.Errorf("local acknowledgement process changed")
			}
			return b, nil
		}
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		case <-time.After(10 * time.Millisecond):
		}
	}
}
