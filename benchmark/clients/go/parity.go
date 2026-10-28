package main

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"net/http/httptrace"
	"os"
	"runtime"
	"slices"
	"strings"
	"sync/atomic"
	"time"

	sdk "terrascale.tech/sdk"
	"terrascale.tech/sdk/tsauth"
	"terrascale.tech/sdk/v1"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

const parityOrigin = "https://api.terrascale.tech"

// parityTransport observes every attempt, including retries hidden by the SDK.
// Pin only public document reads; never forward the pin to auth or another host.
type parityTransport struct {
	base                               http.RoundTripper
	region, protocol                   string
	attempts, bad, connections, h1, h2 atomic.Int64
}

func (t *parityTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	if request.URL.Scheme != "https" || request.URL.Host != "api.terrascale.tech" || request.Method != http.MethodGet || !strings.HasSuffix(request.URL.Path, "/documents/by-key") {
		return nil, errors.New("parity transport accepts only public document reads")
	}
	request = request.Clone(request.Context())
	request.Header.Set("X-TerraScale-Region-Pin", t.region)
	request = request.WithContext(httptrace.WithClientTrace(request.Context(), &httptrace.ClientTrace{GotConn: func(info httptrace.GotConnInfo) {
		if !info.Reused {
			t.connections.Add(1)
		}
	}}))
	t.attempts.Add(1)
	response, err := t.base.RoundTrip(request)
	if err != nil {
		// RunStep cancels its final in-flight operations at the window boundary.
		if request.Context().Err() == nil {
			t.bad.Add(1)
		}
		return nil, err
	}
	if response.ProtoMajor == 1 {
		t.h1.Add(1)
	}
	if response.ProtoMajor == 2 {
		t.h2.Add(1)
	}
	if response.StatusCode != http.StatusOK || response.Header.Get("X-TerraBase-Region") != t.region ||
		len(response.Header.Values("X-TerraBase-Region")) != 1 ||
		t.protocol == "raw-h1" && response.ProtoMajor != 1 || t.protocol == "raw-h2" && response.ProtoMajor != 2 {
		t.bad.Add(1)
		response.Body.Close()
		return nil, errors.New("parity response status, region or protocol mismatch")
	}
	return response, nil
}

type paritySetupTransport struct {
	base            http.RoundTripper
	project, region string
}

func (t paritySetupTransport) RoundTrip(request *http.Request) (*http.Response, error) {
	if request.URL.Scheme != "https" || request.URL.Host != "api.terrascale.tech" || !strings.HasPrefix(request.URL.Path, "/v1/projects/"+t.project+"/databases") {
		return nil, errors.New("parity setup route mismatch")
	}
	request = request.Clone(request.Context())
	request.Header.Set("X-TerraScale-Region-Pin", t.region)
	return t.base.RoundTrip(request)
}

type parityDocument struct {
	id, version string
	body        json.RawMessage
}

func (d parityDocument) check(document *v1.Document) error {
	if document == nil || document.ID != d.id || document.Version != d.version || document.State != v1.StateValue || !bytes.Equal(document.Document, d.body) {
		return errors.New("parity document identity, version, state or body mismatch")
	}
	return nil
}

// runParity owns one synthetic database and emits every cell before rejecting it.
func runParity(args []string) (failure error) {
	fs := flag.NewFlagSet("parity", flag.ContinueOnError)
	credentials := fs.String("credentials", "", "private synthetic {api_key,project} file")
	output := fs.String("out", "", "new JSONL output file")
	if err := fs.Parse(args); err != nil {
		return err
	}
	key, project, err := loadCredentials(*credentials)
	if err != nil {
		return errors.New("parity credentials could not be loaded")
	}
	log, err := os.OpenFile(*output, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0o600)
	if err != nil {
		return errors.New("parity output must be a new writable file")
	}
	defer log.Close()
	encode := json.NewEncoder(log)
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Minute)
	defer cancel()
	discovery, err := sdk.NewClient(sdk.Config{})
	if err != nil {
		return errors.New("parity discovery client failed")
	}
	catalog, err := discovery.ListRegions(ctx)
	if err != nil {
		return errors.New("parity serving-region discovery failed")
	}
	for _, required := range []string{"gru", "sjc", "fra"} {
		if !slices.Contains(catalog.Regions, required) {
			return errors.New("parity required serving region is absent")
		}
	}
	source, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: key, Project: project})
	if err != nil {
		return errors.New("parity token source failed")
	}
	setupBase := sdkTransport().(*http.Transport)
	defer setupBase.CloseIdleConnections()
	setup, err := v1.New(v1.Config{TokenSource: source, HTTPClient: &http.Client{Transport: paritySetupTransport{setupBase, project, catalog.Regions[0]}, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}})
	if err != nil {
		return errors.New("parity setup client failed")
	}
	name := "api-perf-" + strings.ToLower(v1.NewID())
	database := setup.Database(project, name)
	// Even an uncertain create must be reconciled and deleted using this exact owned name.
	defer func() {
		cleanup, stop := context.WithTimeout(context.Background(), 30*time.Second)
		defer stop()
		cleaned := true
		if err := database.Delete(cleanup); err != nil {
			var refusal *v1.APIError
			if !errors.As(err, &refusal) || refusal.Status != http.StatusNotFound {
				cleaned = false
				failure = errors.Join(failure, errors.New("parity database cleanup failed"))
			}
		}
		if err := encode.Encode(map[string]any{"type": "cleanup", "accepted": cleaned, "database": name}); err != nil {
			failure = errors.Join(failure, err)
		}
	}()
	if _, err := database.CreateIn(ctx, v1.Placement{HomeRegion: catalog.Regions[0], AllRegions: true}); err != nil {
		return errors.New("parity database create failed")
	}
	if _, err := database.CreateCollection(ctx, "bench"); err != nil {
		return errors.New("parity collection create failed")
	}
	documents := make(map[int]parityDocument)
	for _, size := range []int{256, 4096} {
		body := json.RawMessage(`{"payload":"` + strings.Repeat("x", size-14) + `"}`)
		id := v1.NewID()
		written, err := database.Collection("bench").Put(ctx, id, body)
		if err != nil {
			return errors.New("parity seed write failed")
		}
		documents[size] = parityDocument{id: id, version: written.Version, body: body}
	}
	host, err := os.Hostname()
	if err != nil {
		return err
	}
	if err := encode.Encode(map[string]any{"type": "parity", "host": host, "sdk": sdkVersion(), "runtime": runtime.Version(), "regions": catalog.Regions, "database": name}); err != nil {
		return err
	}
	for _, region := range catalog.Regions {
		if err := parityRegion(ctx, encode, source, project, name, region, documents); err != nil {
			return err
		}
	}
	return nil
}

func parityRegion(ctx context.Context, encode *json.Encoder, source *tsauth.TokenSource, project, database, region string, documents map[int]parityDocument) error {
	for repeat := 1; repeat <= 3; repeat++ {
		lanes := []string{"raw-h1", "raw-h2", "sdk"}
		if repeat%2 == 0 {
			slices.Reverse(lanes)
		}
		for _, size := range []int{256, 4096} {
			document := documents[size]
			for _, concurrency := range []int{1, 16} {
				for _, lane := range lanes {
					// Same shipped transport settings; only raw controls force a protocol.
					base := sdkTransport().(*http.Transport)
					if lane != "sdk" {
						var protocols http.Protocols
						protocols.SetHTTP1(lane == "raw-h1")
						protocols.SetHTTP2(lane == "raw-h2")
						base.Protocols = &protocols
					}
					observed := &parityTransport{base: base, region: region, protocol: lane}
					meter := &apibench.Meter{}
					httpClient := &http.Client{Transport: meter.Wrap(observed), CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
					client, err := v1.New(v1.Config{TokenSource: source, HTTPClient: httpClient})
					if err != nil {
						base.CloseIdleConnections()
						return errors.New("parity data client failed")
					}
					collection := client.Database(project, database).Collection("bench")
					// Refresh before the cell. The SDK and raw lane share the same token source.
					_, err = source.Token(ctx)
					if err != nil {
						base.CloseIdleConnections()
						return errors.New("parity token refresh failed")
					}
					op := func(call context.Context, _, _ int) string {
						var value *v1.Document
						var err error
						if lane == "sdk" {
							value, err = collection.Get(call, document.id)
						} else {
							var request *http.Request
							request, err = http.NewRequestWithContext(call, http.MethodGet, fmt.Sprintf("%s/v1/projects/%s/databases/%s/collections/bench/documents/by-key?id=%s", parityOrigin, project, database, document.id), nil)
							if err == nil {
								token, tokenErr := source.Token(call)
								if tokenErr != nil {
									if call.Err() == nil {
										observed.bad.Add(1)
									}
									return status(tokenErr)
								}
								request.Header.Set("Authorization", "Bearer "+token)
								request.Header.Set("Accept", "application/json")
								var response *http.Response
								response, err = httpClient.Do(request)
								if err == nil {
									var data []byte
									data, err = io.ReadAll(io.LimitReader(response.Body, 1_048_577))
									response.Body.Close()
									if len(data) > 1_048_576 {
										err = errors.New("parity response too large")
									}
									if err == nil {
										value = &v1.Document{}
										err = json.Unmarshal(data, value)
									}
								}
							}
						}
						if err == nil {
							err = document.check(value)
						}
						if err != nil && call.Err() == nil {
							observed.bad.Add(1)
						}
						return status(err)
					}
					// Readiness is untimed and bounded; a newly replicated seed may be absent.
					ready, stop := context.WithTimeout(ctx, 60*time.Second)
					for op(ready, 0, 0) != "ok" && ready.Err() == nil {
						select {
						case <-ready.Done():
						case <-time.After(100 * time.Millisecond):
						}
					}
					readyErr := ready.Err()
					stop()
					if readyErr != nil {
						base.CloseIdleConnections()
						return errors.New("parity replica readiness failed")
					}
					beforeBad := observed.bad.Load()
					step := apibench.Step{Type: "step", Scenario: "doc.get", Transport: lane, PayloadBytes: size, Mode: "closed", Concurrency: concurrency, WarmupS: 2, StepS: 5, AbortErrorRate: .01}
					result := apibench.RunStep(ctx, step, op, meter)
					row := struct {
						apibench.Result
						Region      string `json:"region"`
						Repeat      int    `json:"repeat"`
						Connections int64  `json:"connections"`
						Attempts    int64  `json:"attempts"`
						BadAttempts int64  `json:"bad_attempts"`
						H1          int64  `json:"h1"`
						H2          int64  `json:"h2"`
					}{result, region, repeat, observed.connections.Load(), observed.attempts.Load(), observed.bad.Load() - beforeBad, observed.h1.Load(), observed.h2.Load()}
					base.CloseIdleConnections()
					if err := encode.Encode(row); err != nil {
						return err
					}
					if result.OK == 0 || result.Errors != 0 || row.BadAttempts != 0 || ctx.Err() != nil {
						return errors.New("parity rejected errored or empty cell; evidence retained")
					}
				}
			}
		}
	}
	return nil
}
