package atlas_test

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"math/big"
	"net/http"
	"net/http/httptest"
	"strconv"
	"testing"
	"time"

	"terrascale.tech/sdk/atlas"
)

// atlasServer verifies publishes as ts-atlas src/pin.ts publishSigned does:
// a timestamp within 300 s and an ES256 raw r||s signature by key over
// "atlas-publish-v1\n<timestamp>\n<body>".
func atlasServer(t *testing.T, key *ecdsa.PublicKey) (*httptest.Server, *atlas.Document) {
	var got atlas.Document
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		body, _ := io.ReadAll(r.Body)
		ts := r.Header.Get("X-Atlas-Timestamp")
		sig, err := base64.RawURLEncoding.DecodeString(r.Header.Get("X-Atlas-Signature"))
		sec, _ := strconv.ParseInt(ts, 10, 64)
		digest := sha256.Sum256([]byte("atlas-publish-v1\n" + ts + "\n" + string(body)))
		if r.Method != http.MethodPost || r.URL.Path != "/v1/publish" || err != nil || len(sig) != 64 ||
			time.Since(time.Unix(sec, 0)).Abs() > 300*time.Second ||
			!ecdsa.Verify(key, digest[:], new(big.Int).SetBytes(sig[:32]), new(big.Int).SetBytes(sig[32:])) {
			w.WriteHeader(http.StatusUnauthorized)
			io.WriteString(w, `{"error":{"code":"invalid_signature","message":"the publish signature is missing, expired, or not from a publisher key","fix":"sign it"}}`)
			return
		}
		if err := json.Unmarshal(body, &got); err != nil {
			t.Error(err)
		}
		io.WriteString(w, `{"topology":"updated","placements":[{"project":"p1","result":"stale"}]}`)
	}))
	t.Cleanup(srv.Close)
	return srv, &got
}

func newKey(t *testing.T) *ecdsa.PrivateKey {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return key
}

func TestPublish(t *testing.T) {
	key := newKey(t)
	srv, got := atlasServer(t, &key.PublicKey)
	doc := atlas.Document{
		Topology: &atlas.Topology{V: 1, Updated: 1791159807,
			Regions:  []atlas.Region{{ID: "gru-1", Name: "São Paulo", Country: "BR", API: "https://gru.api.terrascale.tech", Auth: "https://gru.auth.terrascale.tech", OK: true}},
			Services: []atlas.Service{{ID: "terrabase", Name: "TerraBase", URL: "https://api.terrascale.tech", Health: "https://api.terrascale.tech/readyz"}}},
		Placements: []atlas.Placement{{V: 1, Project: "p1", Region: "gru-1", Databases: map[string]string{"orders": "gru-1"}, Updated: 1791159807}},
	}
	if _, err := (&atlas.Publisher{BaseURL: srv.URL + "/", Key: key}).Publish(t.Context(), doc); err != nil {
		t.Fatalf("trailing slash: %v", err)
	}
	p := atlas.Publisher{BaseURL: srv.URL, Key: key}
	report, err := p.Publish(t.Context(), doc)
	if err != nil {
		t.Fatal(err)
	}
	if report.Topology != "updated" || len(report.Placements) != 1 || report.Placements[0].Result != "stale" {
		t.Fatalf("report: %+v", report)
	}
	if got.Topology == nil || got.Topology.Regions[0].Name != "São Paulo" || got.Placements[0].Databases["orders"] != "gru-1" {
		t.Fatalf("published: %+v", got)
	}

	// A key Atlas does not know is refused with its error body.
	p.Key = newKey(t)
	_, err = p.Publish(t.Context(), doc)
	var atlasErr *atlas.Error
	if !errors.As(err, &atlasErr) || atlasErr.Status != http.StatusUnauthorized || atlasErr.Code != "invalid_signature" || atlasErr.Fix == "" {
		t.Fatalf("unknown key: %v", err)
	}
}

func TestPublishDoesNotFollowRedirects(t *testing.T) {
	elsewhere := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {
		t.Error("the signed publish followed a redirect")
	}))
	t.Cleanup(elsewhere.Close)
	for _, status := range []int{http.StatusTemporaryRedirect, http.StatusPermanentRedirect} {
		redirect := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			http.Redirect(w, r, elsewhere.URL+"/v1/publish", status)
		}))
		t.Cleanup(redirect.Close)
		p := atlas.Publisher{BaseURL: redirect.URL, Key: newKey(t), HTTPClient: &http.Client{}}
		_, err := p.Publish(t.Context(), atlas.Document{})
		var atlasErr *atlas.Error
		if !errors.As(err, &atlasErr) || atlasErr.Status != status {
			t.Fatalf("redirect %d: %v", status, err)
		}
	}
}

func TestSignRejectsOtherKeys(t *testing.T) {
	p384, err := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	for name, key := range map[string]*ecdsa.PrivateKey{"nil": nil, "P-384": p384} {
		if _, err := atlas.Sign(key, "1", nil); err == nil {
			t.Errorf("%s key: no error", name)
		}
	}
}

func TestPublishRejectsLocally(t *testing.T) {
	key := newKey(t)
	p384, err := ecdsa.GenerateKey(elliptic.P384(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	for name, p := range map[string]atlas.Publisher{
		"no key":     {BaseURL: "https://atlas.terrascale.tech"},
		"P-384 key":  {BaseURL: "https://atlas.terrascale.tech", Key: p384},
		"plain HTTP": {BaseURL: "http://atlas.terrascale.tech", Key: key},
		"query":      {BaseURL: "https://atlas.terrascale.tech/?k=v", Key: key},
		"no base":    {Key: key},
		"user info":  {BaseURL: "https://u:p@atlas.terrascale.tech", Key: key},
	} {
		p.HTTPClient = &http.Client{Transport: roundTripFunc(func(*http.Request) (*http.Response, error) {
			t.Errorf("%s: request sent", name)
			return nil, errors.New("unexpected request")
		})}
		if _, err := p.Publish(t.Context(), atlas.Document{}); err == nil {
			t.Errorf("%s: no error", name)
		}
	}
}

type roundTripFunc func(*http.Request) (*http.Response, error)

func (f roundTripFunc) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }
