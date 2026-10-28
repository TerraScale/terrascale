package main

import (
	"bytes"
	"context"
	"crypto/tls"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math/rand/v2"
	"net"
	"net/http"
	"os"
	"strconv"
	"strings"
	"time"

	"terrascale.tech/sdk/tsauth"
	"terrascale.tech/sdk/v1"
	"terrascale.tech/ts-benchmark/internal/apibench"
)

// op returns the operation for one step. The token and database handle are
// read here, so a step's ops never see a later refresh.
func (b *bench) op(s apibench.Step) apibench.Op {
	if s.Transport != "sdk" {
		return b.rawOp(s)
	}
	p := s.PayloadBytes
	seed, upd, ins, txn := fmt.Sprintf("seed_%d", p), fmt.Sprintf("upd_%d", p), fmt.Sprintf("ins_%d", p), fmt.Sprintf("txn_%d", p)
	ids, bodies, db := b.ids, b.bodies[p], b.db
	switch s.Scenario {
	case "auth.token":
		return func(ctx context.Context, _, _ int) string {
			ts, err := tsauth.NewAPIKeySource(b.auth)
			if err != nil {
				return status(err)
			}
			_, err = ts.Token(ctx)
			return status(err)
		}
	case "doc.get":
		col := db.Collection(seed)
		return func(ctx context.Context, _, _ int) string {
			_, err := col.Get(ctx, pick(ids[seed]))
			return status(err)
		}
	case "doc.insert":
		col := db.Collection(ins)
		return func(ctx context.Context, _, i int) string {
			_, err := col.Create(ctx, bodies[i%16])
			return status(err)
		}
	case "doc.update":
		col := db.Collection(upd)
		return func(ctx context.Context, _, i int) string {
			_, err := col.Put(ctx, pick(ids[upd]), bodies[i%16])
			return status(err)
		}
	case "doc.delete":
		col := db.Collection(ins)
		return func(ctx context.Context, _, i int) string {
			res, err := col.Create(ctx, bodies[i%16])
			if err != nil {
				return status(err)
			}
			apibench.Restart(ctx)
			_, err = col.Delete(ctx, res.ID)
			return status(err)
		}
	case "query.small":
		col := db.Collection("seed_256")
		return func(ctx context.Context, _, _ int) string {
			_, err := col.Query(ctx, v1.Query{Filter: v1.Eq("/g", rand.IntN(16)), Limit: 10})
			return status(err)
		}
	case "txn.batch":
		return func(ctx context.Context, _, i int) string {
			_, err := db.Tx().Create(txn, "", bodies[i%16]).Create(txn, "", bodies[i%16]).Commit(ctx)
			return status(err)
		}
	case "mixed.80-20":
		col := db.Collection(seed)
		return func(ctx context.Context, _, i int) string {
			if i%5 == 4 {
				_, err := col.Put(ctx, pick(ids[seed]), bodies[i%16])
				return status(err)
			}
			_, err := col.Get(ctx, pick(ids[seed]))
			return status(err)
		}
	}
	return unsupported(s)
}

// rawOp is the raw-HTTP baseline for doc.get and doc.insert. It uses the SDK's
// routes, bearer token and status keys.
func (b *bench) rawOp(s apibench.Step) apibench.Op {
	hc, tok, p := b.h2, b.token, s.PayloadBytes
	if s.Transport == "raw-h1" {
		hc = b.h1
	}
	base := func(col string) string {
		return fmt.Sprintf("%s/v1/projects/%s/databases/%s/collections/%s/documents", b.spec.Endpoint, b.project, b.dbName, col)
	}
	switch s.Scenario {
	case "doc.get":
		col := fmt.Sprintf("seed_%d", p)
		target, ids := base(col)+"/by-key?id=", b.ids[col]
		return func(ctx context.Context, _, _ int) string {
			return rawDo(ctx, hc, http.MethodGet, target+pick(ids), nil, tok)
		}
	case "doc.insert":
		target, bodies := base(fmt.Sprintf("ins_%d", p)), b.bodies[p]
		return func(ctx context.Context, _, i int) string {
			return rawDo(ctx, hc, http.MethodPost, target, bodies[i%16], tok)
		}
	}
	return unsupported(s)
}

// unsupported answers a step this client does not run. The runner only sends
// the steps the spec plans, so this is a diagnostic.
func unsupported(s apibench.Step) apibench.Op {
	fmt.Fprintf(os.Stderr, "unsupported step %s over %s\n", s.Scenario, s.Transport)
	return func(context.Context, int, int) string { return "transport" }
}

// rawClient forces HTTP/2 or HTTP/1.1 with a pool of 1024 connections per host.
func rawClient(m *apibench.Meter, h2 bool) *http.Client {
	var p http.Protocols
	p.SetHTTP2(h2)
	p.SetHTTP1(!h2)
	return &http.Client{Transport: m.Wrap(&http.Transport{
		TLSClientConfig:     &tls.Config{MinVersion: tls.VersionTLS13},
		MaxIdleConnsPerHost: 1024,
		IdleConnTimeout:     90 * time.Second,
		Protocols:           &p,
	})}
}

// rawDo sends one request and reads the whole body, so the timing covers the response.
func rawDo(ctx context.Context, hc *http.Client, method, target string, body []byte, tok string) string {
	req, err := http.NewRequestWithContext(ctx, method, target, bytes.NewReader(body))
	if err != nil {
		return status(err)
	}
	req.Header.Set("Authorization", "Bearer "+tok)
	req.Header.Set("Accept", "application/json")
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	resp, err := hc.Do(req)
	if err != nil {
		return status(err)
	}
	defer resp.Body.Close()
	if _, err := io.Copy(io.Discard, resp.Body); err != nil {
		return status(err)
	}
	if resp.StatusCode/100 == 2 {
		return "ok"
	}
	return strconv.Itoa(resp.StatusCode)
}

// status maps an operation's error to its status key. An SDK error carries the HTTP status.
func status(err error) string {
	var api *v1.APIError
	var auth *tsauth.Error
	var ne net.Error
	switch {
	case err == nil:
		return "ok"
	case errors.As(err, &api):
		return strconv.Itoa(api.Status)
	case errors.As(err, &auth):
		return "auth_" + strconv.Itoa(auth.Status)
	case errors.Is(err, context.DeadlineExceeded), errors.As(err, &ne) && ne.Timeout():
		return "timeout"
	case errors.Is(err, context.Canceled):
		return "canceled"
	}
	return "transport"
}

func pick(ids []string) string { return ids[rand.IntN(len(ids))] }

type docBody struct {
	G int    `json:"g"`
	P string `json:"p"`
}

// payloads returns one body per g in 0..15: {"g":g,"p":"xxx"}, exactly size UTF-8 bytes.
func payloads(size int) ([]json.RawMessage, error) {
	out := make([]json.RawMessage, 16)
	for g := range out {
		base, err := json.Marshal(docBody{G: g})
		if err != nil {
			return nil, err
		}
		if size < len(base) {
			return nil, fmt.Errorf("payload %d is below the %d byte minimum", size, len(base))
		}
		if out[g], err = json.Marshal(docBody{G: g, P: strings.Repeat("x", size-len(base))}); err != nil {
			return nil, err
		}
	}
	return out, nil
}
