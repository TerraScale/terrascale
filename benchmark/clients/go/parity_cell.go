package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"time"

	"terrascale.tech/sdk/v1"
)

// parityRow is one emitted cell. Rejected lists every reason; empty accepts it.
type parityRow struct {
	Type         string `json:"type"`
	Region       string `json:"region"`
	Lane         string `json:"lane"`
	Repeat       int    `json:"repeat"`
	PayloadBytes int    `json:"payload_bytes"`
	parityResult
	Transport       parityCounts `json:"transport"` // warmup, window and drain; readiness excluded
	MaxConnectionUS int64        `json:"max_connection_us"`
	MaxHeadersUS    int64        `json:"max_headers_us"`
	Evidence        string       `json:"evidence"`
	Rejected        []string     `json:"rejected"`
}

// parityCell builds a fresh client over base, waits untimed (at most 60 s) for
// the document to be readable in the pinned region, then measures. The SDK lane
// keeps the shipped retry and token refresh policy; raw lanes force one protocol.
func parityCell(ctx context.Context, source v1.TokenSource, base *http.Transport, target parityTarget, plan parityPlan) (parityRow, error) {
	defer base.CloseIdleConnections()
	if target.Lane != "sdk" {
		var protocols http.Protocols
		protocols.SetHTTP1(target.Lane == "raw-h1")
		protocols.SetHTTP2(target.Lane == "raw-h2")
		base.Protocols = &protocols
	}
	observed := &parityTransport{base: base, region: target.Region, protocol: target.Lane}
	httpClient := &http.Client{Transport: observed, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	client, err := v1.New(v1.Config{TokenSource: source, HTTPClient: httpClient})
	if err != nil {
		return parityRow{}, errors.New("parity data client failed")
	}
	collection := client.Database(target.Project, target.Database).Collection("bench")
	address := fmt.Sprintf("%s/v1/projects/%s/databases/%s/collections/bench/documents/by-key?id=%s", parityOrigin,
		url.PathEscape(target.Project), url.PathEscape(target.Database), url.QueryEscape(target.Document.id))
	op := func(call context.Context) string {
		var document *v1.Document
		var err error
		if target.Lane == "sdk" {
			document, err = collection.Get(call, target.Document.id)
		} else {
			document, err = parityRawGet(call, httpClient, source, address)
		}
		if err == nil {
			if err = target.Document.check(document); err != nil {
				return "document"
			}
		}
		return status(err)
	}
	ready, stop := context.WithTimeout(ctx, 60*time.Second)
	for op(ready) != "ok" && ready.Err() == nil {
		select {
		case <-ready.Done():
		case <-time.After(100 * time.Millisecond):
		}
	}
	readyErr := ready.Err()
	stop()
	if readyErr != nil {
		return parityRow{}, errors.New("parity replica readiness failed")
	}
	before := observed.counts()
	result := parityMeasure(ctx, plan, op)
	row := parityRow{Type: "cell", Region: target.Region, Lane: target.Lane, PayloadBytes: len(target.Document.body), parityResult: result,
		Transport: observed.counts().minus(before), MaxConnectionUS: observed.maxConnectionUS.Load(), MaxHeadersUS: observed.maxHeadersUS.Load(), Evidence: parityEvidence}
	row.Rejected = parityRejections(row)
	return row, nil
}

func parityRejections(row parityRow) []string {
	reasons := []string{}
	for _, check := range []struct {
		failed bool
		reason string
	}{
		{row.Canceled, "canceled"},
		{row.Unfinished != 0, "unfinished"},
		{row.OK == 0, "empty"},
		{row.OK != row.Measured, "errors"},
		{row.Transport.Attempts != row.Starts, "attempts_differ_from_starts"},
		{row.Transport.Bad != 0, "bad_attempts"},
		{row.Transport.BodyErrors != 0, "body_errors"},
	} {
		if check.failed {
			reasons = append(reasons, check.reason)
		}
	}
	return reasons
}

// parityRawGet is one HTTP exchange with the token the SDK would send.
func parityRawGet(ctx context.Context, httpClient *http.Client, source v1.TokenSource, address string) (*v1.Document, error) {
	token, err := source.Token(ctx)
	if err != nil {
		return nil, err
	}
	request, err := http.NewRequestWithContext(ctx, http.MethodGet, address, nil)
	if err != nil {
		return nil, err
	}
	request.Header.Set("Authorization", "Bearer "+token)
	request.Header.Set("Accept", "application/json")
	response, err := httpClient.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	data, err := io.ReadAll(io.LimitReader(response.Body, 1_048_577))
	if err != nil {
		return nil, err
	}
	if response.StatusCode != http.StatusOK {
		return nil, &v1.APIError{Status: response.StatusCode}
	}
	if len(data) > 1_048_576 {
		return nil, errors.New("parity response too large")
	}
	var document v1.Document
	if err := json.Unmarshal(data, &document); err != nil {
		return nil, err
	}
	return &document, nil
}
