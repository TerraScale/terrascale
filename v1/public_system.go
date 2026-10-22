package v1

import (
	"context"
	"net/http"
)

// Probes reads anonymous TerraBase health routes without sending credentials.
type Probes struct{ c *Client }

// NewProbes uses the configured endpoint and transport, discarding any token
// or token source. Constructing it performs no network requests.
func NewProbes(cfg Config) (*Probes, error) {
	cfg.Token, cfg.TokenSource = "", nil
	c, err := newClient(cfg)
	if err != nil {
		return nil, err
	}
	return &Probes{c: c}, nil
}

// ProbeStatus reports the exact build, region, machine and storage format.
type ProbeStatus struct {
	Status        string  `json:"status"`
	Version       string  `json:"version"`
	Region        string  `json:"region"`
	StorageFormat string  `json:"storageFormat"`
	Revision      *string `json:"revision"`
	Machine       string  `json:"machine"`
}

// ReplicationHealth is the anonymous regional lag summary.
type ReplicationHealth struct {
	Region        string                  `json:"region"`
	Status        string                  `json:"status"`
	MaxLagSeconds *float64                `json:"maxLagSeconds"`
	Peers         []ReplicationPeerHealth `json:"peers"`
}

// ReplicationPeerHealth preserves unknown lag separately from measured zero.
type ReplicationPeerHealth struct {
	Region     string   `json:"region"`
	Status     string   `json:"status"`
	LagSeconds *float64 `json:"lagSeconds"`
}

// Health reads process liveness.
func (p *Probes) Health(ctx context.Context) (*ProbeStatus, error) { return p.status(ctx, "healthz") }

// Ready reads readiness. On 503 it returns both the status body and an APIError.
func (p *Probes) Ready(ctx context.Context) (*ProbeStatus, error) { return p.status(ctx, "readyz") }

// Backup reads continuous-backup health, returning the body on 503 too.
func (p *Probes) Backup(ctx context.Context) (*ProbeStatus, error) { return p.status(ctx, "backupz") }

func (p *Probes) status(ctx context.Context, endpoint string) (*ProbeStatus, error) {
	var out ProbeStatus
	resp, err := p.c.call(ctx, request{method: http.MethodGet, path: []string{endpoint}, replaySafe: true}, nil, &out)
	if err != nil {
		if resp == nil || resp.status != http.StatusServiceUnavailable || decodeExact(resp.body, &out) != nil || out.Status == "" {
			return nil, err
		}
	}
	return &out, err
}

// Replication reads the regional lag summary; degraded state still returns 200.
func (p *Probes) Replication(ctx context.Context) (*ReplicationHealth, error) {
	return publicCall[ReplicationHealth](ctx, p.c, request{method: http.MethodGet, path: []string{"health", "replication"}, replaySafe: true}, nil)
}
