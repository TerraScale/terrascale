package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"net/http"
	"os"
	"os/signal"
	"runtime"
	"slices"
	"strings"
	"syscall"
	"time"

	"terrascale.tech/sdk/tsauth"
	"terrascale.tech/sdk/v1"
)

// paritySetupDeadline bounds the whole setup: create, collection and seeds.
const paritySetupDeadline = 60 * time.Second

// paritySetupTransport pins setup and cleanup to the home region and refuses any other route.
type paritySetupTransport struct {
	base            http.RoundTripper
	project, region string
}

func (t paritySetupTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	if request.URL.Scheme != "https" || request.URL.Host != parityHost || !strings.HasPrefix(request.URL.Path, "/v1/projects/"+t.project+"/databases") {
		return nil, errors.New("parity setup route mismatch")
	}
	request = request.Clone(request.Context())
	request.Header.Set("X-TerraScale-Region-Pin", t.region)
	return t.base.RoundTrip(request)
}

// runParity measures every serving region in the peers file against one owned
// synthetic database, and emits every cell before rejecting it.
func runParity(args []string) error {
	fs := flag.NewFlagSet("parity", flag.ContinueOnError)
	credentials := fs.String("credentials", "", "private synthetic {api_key,project} file")
	output := fs.String("out", "", "new JSONL output file")
	if err := fs.Parse(args); err != nil {
		return err
	}
	regions, err := parityRegions(os.Getenv("TS_PEERS_FILE"))
	if err != nil {
		return err
	}
	key, project, err := loadCredentials(*credentials)
	if err != nil {
		return errors.New("parity credentials could not be loaded")
	}
	source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: key, Project: project})
	if err != nil {
		return errors.New("parity token source failed")
	}
	log, err := os.OpenFile(*output, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return errors.New("parity output must be a new writable file")
	}
	defer log.Close()
	// The first signal cancels the run. Until stop runs, after cleanup, repeated
	// signals stay caught, so they cannot kill the process mid-cleanup.
	signals, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	ctx, cancel := context.WithTimeout(signals, 30*time.Minute)
	defer cancel()
	base := sdkTransport().(*http.Transport)
	defer base.CloseIdleConnections()
	home := regions[0]
	setup, err := v1.New(v1.Config{TokenSource: source, HTTPClient: &http.Client{Transport: paritySetupTransport{base, project, home}}})
	if err != nil {
		return errors.New("parity setup client failed")
	}
	database := setup.Database(project, "api-perf-"+strings.ToLower(v1.NewID()))
	return parityOwn(ctx, json.NewEncoder(log), database, home, paritySetupDeadline, func(documents map[int]parityDocument) error {
		host, err := os.Hostname()
		if err != nil {
			return err
		}
		if err := json.NewEncoder(log).Encode(map[string]any{"type": "parity", "host": host, "sdk": sdkVersion(), "runtime": runtime.Version(),
			"regions": regions, "home": home, "database": database.Name(), "evidence": parityEvidence}); err != nil {
			return err
		}
		return parityRegionsRun(ctx, json.NewEncoder(log), source, project, database.Name(), regions, documents, func() *http.Transport { return sdkTransport().(*http.Transport) }, parityLive)
	})
}

// parityOwn records the owned database name before the first mutation, so an
// uncertain create can be reconciled, and always deletes that exact name on a
// fresh 30 s context, even after cancellation. A 204 is logical deletion
// accepted by the home region; remote erasure is not verified here.
func parityOwn(ctx context.Context, encode *json.Encoder, database *v1.Database, home string, setupDeadline time.Duration, run func(map[int]parityDocument) error) (failure error) {
	if err := encode.Encode(map[string]any{"type": "owned", "database": database.Name(), "home": home}); err != nil {
		return err
	}
	create := "not_attempted"
	defer func() {
		cleanup, stop := context.WithTimeout(context.Background(), 30*time.Second)
		defer stop()
		row := map[string]any{"type": "cleanup", "database": database.Name(), "create": create, "remote_erasure": "unverified"}
		var refusal *v1.APIError
		switch err := database.Delete(cleanup); {
		case err == nil:
			row["deletion"] = "logical_accepted"
		case errors.As(err, &refusal) && refusal.Status == http.StatusNotFound:
			row["deletion"] = "absent"
			row["reconcile_required"] = create == "uncertain"
		default:
			row["deletion"], row["status"], row["reconcile_required"] = "failed", status(err), true
			failure = errors.Join(failure, errors.New("parity database cleanup failed"))
		}
		failure = errors.Join(failure, encode.Encode(row))
	}()
	setup, stop := context.WithTimeout(ctx, setupDeadline)
	defer stop()
	create = "uncertain"
	if _, err := database.CreateIn(setup, v1.Placement{HomeRegion: home, AllRegions: true}); err != nil {
		var refusal *v1.APIError
		var token *tsauth.Error
		switch {
		case errors.As(err, &token):
			create = "not_sent"
		case errors.As(err, &refusal) && refusal.Status < 500:
			create = "refused"
		}
		return fmt.Errorf("parity database create failed: %s", status(err))
	}
	create = "created"
	if _, err := database.CreateCollection(setup, "bench"); err != nil {
		return fmt.Errorf("parity collection create failed: %s", status(err))
	}
	documents := make(map[int]parityDocument)
	for _, size := range []int{256, 4096} {
		body := json.RawMessage(`{"payload":"` + strings.Repeat("x", size-14) + `"}`)
		id := v1.NewID()
		written, err := database.Collection("bench").Put(setup, id, body)
		if err != nil {
			return fmt.Errorf("parity seed write failed: %s", status(err))
		}
		documents[size] = parityDocument{id: id, version: written.Version, body: body}
	}
	stop()
	return run(documents)
}

// parityRegionsRun measures three repeats per region, reversing lane order on
// even repeats. It stops at the first rejected cell, after emitting it.
func parityRegionsRun(ctx context.Context, encode *json.Encoder, source v1.TokenSource, project, database string, regions []string,
	documents map[int]parityDocument, transport func() *http.Transport, plan parityPlan) error {
	for _, region := range regions {
		for repeat := 1; repeat <= 3; repeat++ {
			lanes := []string{"raw-h1", "raw-h2", "sdk"}
			if repeat%2 == 0 {
				slices.Reverse(lanes)
			}
			for _, size := range []int{256, 4096} {
				for _, concurrency := range []int{1, 16} {
					for _, lane := range lanes {
						plan.Concurrency = concurrency
						target := parityTarget{Lane: lane, Region: region, Project: project, Database: database, Document: documents[size]}
						row, err := parityCell(ctx, source, transport(), target, plan)
						if err != nil {
							return err
						}
						row.Repeat = repeat
						if err := encode.Encode(row); err != nil {
							return err
						}
						if len(row.Rejected) != 0 {
							return fmt.Errorf("parity rejected %s %s c%d %d B: %s; evidence retained", region, lane, concurrency, size, strings.Join(row.Rejected, ","))
						}
					}
				}
			}
		}
	}
	return nil
}
