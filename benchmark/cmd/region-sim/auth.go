package main

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log"
	"math/big"
	"net"
	"net/http"
	"strings"
	"sync"
	"time"

	"github.com/oklog/ulid/v2"
)

// Test-only ts-auth stand-in. Real ts-auth cannot approve a device code without a browser session and an emailed
// passcode, and stores its state in TerraBase, so the harness serves the subset of the platform contract the ts CLI and
// TerraBase use: OIDC discovery and JWKS, the device grant (auto-approved), refresh and token exchange, and the
// organization/project calls of `ts projects create`. Tokens mirror tests/TerraBase.TestHarness/TestIssuer.cs:
// ES256 with a kid, typ at+jwt, a ULID sub, project_id/org_id/scope for project tokens.
const (
	cliClientID    = "00000000000000000000000007"
	publicIssuer   = "https://" + authHost + "/terrascale"
	platformAud    = "https://" + apiHost
	tenantPath     = "/terrascale"
	managementPath = "/v1/terrascale"
	allScopes      = "databases:read databases:write documents:read documents:write"
	tokenLifetime  = time.Hour
)

type org struct {
	ID        string    `json:"id"`
	Name      string    `json:"name"`
	CreatedBy string    `json:"created_by"`
	CreatedAt time.Time `json:"created_at"`
	Role      string    `json:"role"`
}

type project struct {
	ID             string    `json:"id"`
	OrganizationID string    `json:"organization_id"`
	Name           string    `json:"name"`
	Issuer         string    `json:"issuer"`
	CreatedBy      string    `json:"created_by"`
	CreatedAt      time.Time `json:"created_at"`
	Role           string    `json:"role"`
	AllowedCIDRs   []string  `json:"allowed_cidrs"`
}

type authStandIn struct {
	issuer string // the iss TerraBase validates: https://localhost:<port>/terrascale
	key    *ecdsa.PrivateKey
	kid    string
	user   string
	srv    *http.Server

	mu       sync.Mutex
	devices  map[string]bool
	refresh  map[string]bool
	orgs     []org
	projects []project
}

func startAuth(ca *authority) (*authStandIn, error) {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, err
	}
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	cert, err := ca.leaf("localhost", "127.0.0.1")
	if err != nil {
		ln.Close()
		return nil, err
	}
	a := &authStandIn{
		issuer:  "https://localhost:" + itoa(ln.Addr().(*net.TCPAddr).Port) + tenantPath,
		key:     key,
		kid:     newULID(),
		user:    newULID(),
		devices: map[string]bool{},
		refresh: map[string]bool{},
	}
	a.srv = &http.Server{Handler: a, ReadHeaderTimeout: 10 * time.Second, ErrorLog: log.New(io.Discard, "", 0),
		TLSConfig: &tls.Config{Certificates: []tls.Certificate{*cert}, MinVersion: tls.VersionTLS12}}
	go a.srv.ServeTLS(ln, "", "")
	return a, nil
}

func (a *authStandIn) close() { a.srv.Close() }

func newULID() string { return ulid.Make().String() }

func randomHex(n int) string {
	b := make([]byte, n)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}

func (a *authStandIn) ServeHTTP(w http.ResponseWriter, r *http.Request) {
	p := r.URL.Path
	switch {
	case r.Method == http.MethodGet && p == tenantPath+"/.well-known/openid-configuration":
		writeJSON(w, 200, map[string]any{
			"issuer": a.issuer, "jwks_uri": a.issuer + "/.well-known/jwks.json",
			"token_endpoint": a.issuer + "/oauth/token", "device_authorization_endpoint": a.issuer + "/oauth/device",
		})
	case r.Method == http.MethodGet && p == tenantPath+"/.well-known/jwks.json":
		point, _ := a.key.PublicKey.ECDH()
		raw := point.Bytes() // 0x04 || X || Y
		writeJSON(w, 200, map[string]any{"keys": []map[string]string{{
			"kty": "EC", "crv": "P-256", "kid": a.kid, "use": "sig", "alg": "ES256",
			"x": b64(raw[1:33]), "y": b64(raw[33:65]),
		}}})
	case r.Method == http.MethodPost && p == tenantPath+"/oauth/device":
		a.device(w, r)
	case r.Method == http.MethodPost && p == tenantPath+"/oauth/token":
		a.token(w, r)
	case r.Method == http.MethodPost && p == tenantPath+"/oauth/revoke":
		w.WriteHeader(http.StatusOK)
	case r.Method == http.MethodGet && p == tenantPath+"/oauth/userinfo":
		if sub, ok := a.bearer(r); ok {
			writeJSON(w, 200, map[string]string{"sub": sub})
			return
		}
		w.WriteHeader(http.StatusUnauthorized)
	case strings.HasPrefix(p, managementPath+"/"):
		a.management(w, r, strings.Split(strings.TrimPrefix(p, managementPath+"/"), "/"))
	default:
		writeAPIError(w, http.StatusNotFound, "not_found", "region-sim ts-auth stand-in does not serve this path")
	}
}

func (a *authStandIn) device(w http.ResponseWriter, r *http.Request) {
	if r.ParseForm() != nil || r.PostForm.Get("client_id") != cliClientID {
		writeJSON(w, 400, map[string]string{"error": "invalid_client"})
		return
	}
	code := randomHex(16)
	a.mu.Lock()
	a.devices[code] = true // auto-approved: the harness is the user
	a.mu.Unlock()
	user := strings.ToUpper(randomHex(2)) + "-" + strings.ToUpper(randomHex(2))
	writeJSON(w, 200, map[string]any{
		"device_code": code, "user_code": user, "verification_uri": publicIssuer + "/device",
		"verification_uri_complete": publicIssuer + "/device?user_code=" + user, "expires_in": 600, "interval": 1,
	})
}

func (a *authStandIn) token(w http.ResponseWriter, r *http.Request) {
	if r.ParseForm() != nil {
		writeJSON(w, 400, map[string]string{"error": "invalid_request"})
		return
	}
	f := r.PostForm
	switch f.Get("grant_type") {
	case "urn:ietf:params:oauth:grant-type:device_code":
		a.mu.Lock()
		ok := a.devices[f.Get("device_code")]
		delete(a.devices, f.Get("device_code"))
		a.mu.Unlock()
		if !ok {
			writeJSON(w, 400, map[string]string{"error": "expired_token"})
			return
		}
		a.userTokens(w)
	case "refresh_token":
		a.mu.Lock()
		ok := a.refresh[f.Get("refresh_token")]
		delete(a.refresh, f.Get("refresh_token"))
		a.mu.Unlock()
		if !ok {
			writeJSON(w, 400, map[string]string{"error": "invalid_grant"})
			return
		}
		a.userTokens(w)
	case "urn:ietf:params:oauth:grant-type:token-exchange":
		sub, ok := a.verify(f.Get("subject_token"))
		if !ok || sub != a.user {
			writeJSON(w, 400, map[string]string{"error": "invalid_grant"})
			return
		}
		a.mu.Lock()
		var found bool
		for _, p := range a.projects {
			found = found || p.ID == f.Get("project") && p.OrganizationID == f.Get("organization")
		}
		a.mu.Unlock()
		if !found {
			writeJSON(w, 400, map[string]string{"error": "invalid_target"})
			return
		}
		writeJSON(w, 200, map[string]any{"access_token": a.projectToken(f.Get("organization"), f.Get("project")),
			"token_type": "Bearer", "expires_in": int(tokenLifetime.Seconds()), "scope": allScopes})
	default:
		writeJSON(w, 400, map[string]string{"error": "unsupported_grant_type"})
	}
}

func (a *authStandIn) userTokens(w http.ResponseWriter) {
	refresh := randomHex(24)
	a.mu.Lock()
	a.refresh[refresh] = true
	a.mu.Unlock()
	access := a.mint(map[string]any{"sub": a.user, "email": "region-sim@example.test", "client_id": cliClientID,
		"scope": "openid email offline_access"})
	writeJSON(w, 200, map[string]any{"access_token": access, "token_type": "Bearer", "expires_in": int(tokenLifetime.Seconds()),
		"refresh_token": refresh, "scope": "openid email offline_access"})
}

// projectToken is what ts-auth's token exchange returns for the user's project: TerraBase's project access token.
func (a *authStandIn) projectToken(organization, project string) string {
	return a.mint(map[string]any{"sub": a.user, "client_id": cliClientID, "project_id": project, "org_id": organization, "scope": allScopes})
}

func (a *authStandIn) mint(claims map[string]any) string {
	now := time.Now()
	claims["iss"] = a.issuer
	claims["aud"] = platformAud
	claims["iat"] = now.Unix()
	claims["nbf"] = now.Add(-time.Minute).Unix()
	claims["exp"] = now.Add(tokenLifetime).Unix()
	claims["jti"] = newULID()
	header, _ := json.Marshal(map[string]string{"alg": "ES256", "typ": "at+jwt", "kid": a.kid})
	payload, _ := json.Marshal(claims)
	signing := b64(header) + "." + b64(payload)
	digest := sha256.Sum256([]byte(signing))
	rr, ss, err := ecdsa.Sign(rand.Reader, a.key, digest[:])
	if err != nil {
		panic(err)
	}
	sig := make([]byte, 64)
	rr.FillBytes(sig[:32])
	ss.FillBytes(sig[32:])
	return signing + "." + b64(sig)
}

// verify checks a token this stand-in minted and returns its sub.
func (a *authStandIn) verify(token string) (string, bool) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return "", false
	}
	sig, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil || len(sig) != 64 {
		return "", false
	}
	digest := sha256.Sum256([]byte(parts[0] + "." + parts[1]))
	if !ecdsa.Verify(&a.key.PublicKey, digest[:], new(big.Int).SetBytes(sig[:32]), new(big.Int).SetBytes(sig[32:])) {
		return "", false
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	var claims struct {
		Sub       string `json:"sub"`
		Exp       int64  `json:"exp"`
		ProjectID string `json:"project_id"`
	}
	if err != nil || json.Unmarshal(payload, &claims) != nil || time.Now().Unix() >= claims.Exp || claims.ProjectID != "" {
		return "", false // management calls take the user's own sign-in token only
	}
	return claims.Sub, true
}

func (a *authStandIn) bearer(r *http.Request) (string, bool) {
	token, ok := strings.CutPrefix(r.Header.Get("Authorization"), "Bearer ")
	if !ok {
		return "", false
	}
	return a.verify(token)
}

func (a *authStandIn) management(w http.ResponseWriter, r *http.Request, path []string) {
	sub, ok := a.bearer(r)
	if !ok || sub != a.user {
		writeAPIError(w, http.StatusUnauthorized, "unauthorized", "sign in again")
		return
	}
	a.mu.Lock()
	defer a.mu.Unlock()
	now := time.Now().UTC().Truncate(time.Second)
	switch {
	case len(path) == 1 && path[0] == "organizations" && r.Method == http.MethodGet:
		writeJSON(w, 200, map[string]any{"items": append([]org{}, a.orgs...), "next": ""})
	case len(path) == 1 && path[0] == "organizations" && r.Method == http.MethodPost:
		name, err := readName(r)
		if err != nil {
			writeAPIError(w, 400, "invalid_request", err.Error())
			return
		}
		o := org{ID: newULID(), Name: name, CreatedBy: a.user, CreatedAt: now, Role: "owner"}
		a.orgs = append(a.orgs, o)
		writeJSON(w, 201, o)
	case len(path) == 2 && path[0] == "organizations" && r.Method == http.MethodGet:
		for _, o := range a.orgs {
			if o.ID == path[1] {
				writeJSON(w, 200, o)
				return
			}
		}
		writeAPIError(w, 404, "not_found", "no such organization")
	case len(path) == 3 && path[0] == "organizations" && path[2] == "projects":
		if !a.hasOrg(path[1]) {
			writeAPIError(w, 404, "not_found", "no such organization")
			return
		}
		if r.Method == http.MethodGet {
			items := []project{}
			for _, p := range a.projects {
				if p.OrganizationID == path[1] {
					items = append(items, p)
				}
			}
			writeJSON(w, 200, map[string]any{"items": items, "next": ""})
			return
		}
		if r.Method != http.MethodPost {
			writeAPIError(w, 405, "method_not_allowed", "GET or POST")
			return
		}
		name, err := readName(r)
		if err != nil {
			writeAPIError(w, 400, "invalid_request", err.Error())
			return
		}
		id := newULID()
		p := project{ID: id, OrganizationID: path[1], Name: name, Issuer: "https://" + authHost + "/" + strings.ToLower(id),
			CreatedBy: a.user, CreatedAt: now, Role: "admin", AllowedCIDRs: []string{}}
		a.projects = append(a.projects, p)
		writeJSON(w, 201, p)
	case len(path) == 4 && path[0] == "organizations" && path[2] == "projects" && r.Method == http.MethodGet:
		for _, p := range a.projects {
			if p.ID == path[3] && p.OrganizationID == path[1] {
				writeJSON(w, 200, p)
				return
			}
		}
		writeAPIError(w, 404, "not_found", "no such project")
	default:
		writeAPIError(w, 404, "not_found", "region-sim ts-auth stand-in does not serve this management call")
	}
}

func (a *authStandIn) hasOrg(id string) bool {
	for _, o := range a.orgs {
		if o.ID == id {
			return true
		}
	}
	return false
}

func readName(r *http.Request) (string, error) {
	var body struct {
		Name string `json:"name"`
	}
	if err := json.NewDecoder(io.LimitReader(r.Body, 1<<16)).Decode(&body); err != nil || strings.TrimSpace(body.Name) == "" {
		return "", errors.New("name is required")
	}
	return body.Name, nil
}

func writeJSON(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	json.NewEncoder(w).Encode(v)
}

func writeAPIError(w http.ResponseWriter, status int, code, message string) {
	writeJSON(w, status, map[string]any{"error": map[string]string{"code": code, "message": message}})
}

func b64(b []byte) string { return base64.RawURLEncoding.EncodeToString(b) }
