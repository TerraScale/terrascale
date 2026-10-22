package main

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/oklog/ulid/v2"
	"terrascale.tech/ts-benchmark/internal/replbench"
)

func recoveryPhase(ctx context.Context, c *cluster, repeat int, name string) (sample, error) {
	s := sample{Repeat: repeat, Phase: name, Metrics: map[string]float64{}}
	docs := 100000
	if name == "rebuild" {
		docs = 1000000
	}
	t := c.targets()
	runner, err := newRunner(t, docs, 16)
	if err != nil {
		return s, err
	}
	if err := runner.Setup(ctx); err != nil {
		return s, err
	}
	lagging := c.nodes["fra"]
	if name == "catchup" {
		if err := c.stop(lagging); err != nil {
			return s, err
		}
	}
	started := time.Now()
	stopLoad := startLoadSampler()
	elapsed, err := runner.Load(ctx)
	if err != nil {
		stopLoad()
		return s, err
	}
	part, state := runner.Partial(started, elapsed.Seconds()), runner.State()
	if len(part.Expected) != docs || len(part.Unknown) > 0 || part.Writes.Failed > 0 {
		stopLoad()
		return s, errors.New("recovery seed load incomplete")
	}
	if name == "rebuild" {
		proof, err := runner.Converge(ctx, time.Now())
		if err != nil || !proof.Converged {
			stopLoad()
			return s, fmt.Errorf("rebuild seed convergence: %v", err)
		}
		if err := c.stop(lagging); err != nil {
			stopLoad()
			return s, err
		}
		// Only this run's stopped replica is replaced. Keep its original bytes until run cleanup.
		dir := filepath.Join(c.work, "fra", "data")
		if err := os.Rename(dir, dir+".before-rebuild"); err != nil {
			stopLoad()
			return s, err
		}
		lagging.config.Machine = ulid.Make().String()
	}
	restart := time.Now()
	if err := c.launch(lagging); err != nil {
		stopLoad()
		return s, err
	}
	if err := c.ready(ctx); err != nil {
		stopLoad()
		return s, err
	}
	// A restarted worker has a fresh issuer/token and ports; preserve expected hashes, refresh all clients.
	verify, err := newRunner(c.targets(), docs, 16)
	if err != nil {
		stopLoad()
		return s, err
	}
	verify.Attach(state)
	if err := verify.Merge([]replbench.Partial{part}); err != nil {
		stopLoad()
		return s, err
	}
	proof, err := verify.Converge(ctx, restart)
	s.Load = stopLoad()
	if err != nil {
		return s, err
	}
	s.Result = replbench.Result{Convergence: proof}
	if !proof.Converged || proof.Unknown != 0 || proof.Expected != docs {
		return s, errors.New("recovery exact verification failed")
	}
	s.Metrics["recovery.ready_seconds"] = time.Since(restart).Seconds()
	s.Metrics["recovery.verified_by_seconds"] = proof.VerifiedBySec
	s.Metrics["recovery.first_exact_round_seconds"] = proof.VerifiedSecs
	s.Metrics["recovery.expected_docs"] = float64(docs)
	return s, nil
}
