package main

import (
	"bytes"
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"

	"terrascale.tech/sdk/internal/entityid"
	"terrascale.tech/sdk/tsauth"
)

var agentOrigin = strings.TrimSuffix(tsauth.DefaultIssuer, "/terrascale")

type agentCredential struct {
	AgentID string `json:"agent_id"`
	Kid     string `json:"kid"`
	Key     []byte `json:"key"`
}

type agentEnrollment struct {
	ID        string `json:"enrollment_id"`
	Code      string `json:"user_code"`
	URL       string `json:"verification_uri_complete"`
	ExpiresIn int    `json:"expires_in"`
	Interval  int    `json:"interval"`
}

type agentPolicy struct {
	Scopes []string `json:"allowed_scopes"`
}

type agentRequestError struct {
	code string
}

func (e *agentRequestError) Error() string { return "Agent request refused: " + e.code }

func agentRequest(ctx context.Context, method, path, credential string, body []byte, form bool, result any) error {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	req, err := http.NewRequestWithContext(ctx, method, agentOrigin+path, bytes.NewReader(body))
	if err != nil {
		return errors.New("Invalid agent request")
	}
	req.Header.Set("Accept", "application/json")
	if body != nil {
		contentType := "application/json"
		if form {
			contentType = "application/x-www-form-urlencoded"
		}
		req.Header.Set("Content-Type", contentType)
	}
	if credential != "" {
		req.Header.Set("Authorization", "Bearer "+credential)
	}
	client := &http.Client{CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	response, err := client.Do(req)
	if err != nil {
		if ctx.Err() != nil {
			return ctx.Err()
		}
		return errors.New("Agent request transport failed")
	}
	defer response.Body.Close()
	data, err := io.ReadAll(io.LimitReader(response.Body, 65537))
	if err != nil || len(data) > 65536 {
		return errors.New("Invalid agent response")
	}
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		var envelope struct {
			Error json.RawMessage `json:"error"`
		}
		var code string
		if json.Unmarshal(data, &envelope) == nil {
			var nested struct {
				Code string `json:"code"`
			}
			if json.Unmarshal(envelope.Error, &code) != nil && json.Unmarshal(envelope.Error, &nested) == nil {
				code = nested.Code
			}
		}
		switch code {
		case "authorization_pending", "slow_down", "access_denied", "expired_token", "invalid_proof", "invalid_grant", "invalid_request", "not_found", "rate_limited", "forbidden_role":
			return &agentRequestError{code}
		default:
			return fmt.Errorf("Agent request failed (HTTP %d)", response.StatusCode)
		}
	}
	if result != nil && json.Unmarshal(data, result) != nil {
		return errors.New("Invalid agent response")
	}
	return nil
}

func agentJSON(value any) ([]byte, error) { return json.Marshal(value) }

func agentAssertion(key *ecdsa.PrivateKey, audience, agent, kid string) (string, error) {
	header := map[string]string{"alg": "ES256", "typ": "JWT"}
	if kid != "" {
		header["kid"] = kid
	}
	nonce := make([]byte, 24)
	if _, err := rand.Read(nonce); err != nil {
		return "", err
	}
	now := time.Now().Unix()
	claims := map[string]any{"aud": audience, "iat": now, "exp": now + 120, "jti": base64.RawURLEncoding.EncodeToString(nonce)}
	if agent != "" {
		claims["iss"], claims["sub"] = agent, agent
	}
	h, err := json.Marshal(header)
	if err != nil {
		return "", err
	}
	c, err := json.Marshal(claims)
	if err != nil {
		return "", err
	}
	input := base64.RawURLEncoding.EncodeToString(h) + "." + base64.RawURLEncoding.EncodeToString(c)
	digest := sha256.Sum256([]byte(input))
	r, s, err := ecdsa.Sign(rand.Reader, key, digest[:])
	if err != nil {
		return "", err
	}
	signature := append(r.FillBytes(make([]byte, 32)), s.FillBytes(make([]byte, 32))...)
	return input + "." + base64.RawURLEncoding.EncodeToString(signature), nil
}

func agentCredentialPath(project, agent string) (string, error) {
	path, err := credentialsPath()
	if err != nil {
		return "", err
	}
	return filepath.Join(filepath.Dir(path), "agent-"+project+"-"+agent+".json"), nil
}

func enrollAgent(ctx context.Context, project, name string, scopes []string, self bool, out, prompt io.Writer) error {
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return err
	}
	jwk := struct {
		Crv string `json:"crv"`
		Kty string `json:"kty"`
		X   string `json:"x"`
		Y   string `json:"y"`
	}{"P-256", "EC", base64.RawURLEncoding.EncodeToString(key.X.FillBytes(make([]byte, 32))), base64.RawURLEncoding.EncodeToString(key.Y.FillBytes(make([]byte, 32)))}
	canonical, err := json.Marshal(jwk)
	if err != nil {
		return err
	}
	fingerprint := sha256.Sum256(canonical)
	body, err := agentJSON(struct {
		Name   string   `json:"name"`
		Scopes []string `json:"requested_scopes"`
		Self   bool     `json:"allow_self_subject"`
		JWK    any      `json:"public_jwk"`
	}{name, scopes, self, jwk})
	if err != nil {
		return err
	}
	root := "/v1/" + project + "/agent-enrollments"
	var enrollment agentEnrollment
	if err := agentRequest(ctx, "POST", root, "", body, false, &enrollment); err != nil {
		return err
	}
	if entityid.Check(enrollment.ID) != nil || enrollment.Interval < 1 || enrollment.Interval > 600 || enrollment.ExpiresIn < 1 || enrollment.ExpiresIn > 600 || len(enrollment.Code) != 9 || enrollment.Code[4] != '-' || strings.Count(enrollment.Code, "-") != 1 || strings.Trim(enrollment.Code, "BCDFGHJKLMNPQRSTVWXZ-") != "" {
		return errors.New("Invalid agent enrollment response")
	}
	link, err := url.Parse(enrollment.URL)
	if err != nil || link.Scheme != "https" || link.Host != strings.TrimPrefix(agentOrigin, "https://") || link.User != nil || link.Path != "/ui/"+project+"/agent-enrollments" || link.RawQuery != "user_code="+enrollment.Code || link.Fragment != "" {
		return errors.New("Invalid agent approval URL")
	}
	if _, err := fmt.Fprintf(prompt, "Code: %s\nOpen %s\nKey fingerprint: %s\n", enrollment.Code, enrollment.URL, base64.RawURLEncoding.EncodeToString(fingerprint[:])[:16]); err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, time.Duration(enrollment.ExpiresIn)*time.Second)
	defer cancel()
	interval := time.Duration(enrollment.Interval) * time.Second
	pollPath := root + "/" + enrollment.ID + "/token"
	for {
		timer := time.NewTimer(interval)
		select {
		case <-ctx.Done():
			timer.Stop()
			return ctx.Err()
		case <-timer.C:
		}
		proof, err := agentAssertion(key, agentOrigin+pollPath, "", "")
		if err != nil {
			return err
		}
		body, err := agentJSON(struct {
			Proof string `json:"proof"`
		}{proof})
		if err != nil {
			return err
		}
		var identity agentCredential
		err = agentRequest(ctx, "POST", pollPath, "", body, false, &identity)
		if err == nil {
			if entityid.Check(identity.AgentID) != nil || entityid.Check(identity.Kid) != nil {
				return errors.New("Invalid agent identity")
			}
			identity.Key, err = x509.MarshalPKCS8PrivateKey(key)
			if err != nil {
				return err
			}
			data, err := json.Marshal(identity)
			if err != nil {
				return err
			}
			path, err := agentCredentialPath(project, identity.AgentID)
			if err != nil {
				return err
			}
			if err := saveSecretCredentials(path, data, (*os.File).Write); err != nil {
				return errors.New("Could not save agent credentials")
			}
			return emitJSON(out, struct {
				Agent string `json:"agent_id"`
				Kid   string `json:"kid"`
			}{identity.AgentID, identity.Kid})
		}
		var refused *agentRequestError
		if !errors.As(err, &refused) {
			return err
		}
		switch refused.code {
		case "authorization_pending":
		case "slow_down":
			interval += 5 * time.Second
		default:
			return err
		}
	}
}

func agentCommand(ctx context.Context, args []string, out, prompt io.Writer) error {
	fs := flag.NewFlagSet("agent", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	project := fs.String("project", "", "project id")
	name := fs.String("name", "", "agent name")
	agent := fs.String("agent", "", "agent id")
	self := fs.Bool("allow-self-subject", false, "allow agent subject")
	subject := fs.String("subject", "owner", "owner or agent")
	scopes := []string{}
	fs.Func("scope", "scope (repeatable)", func(s string) error { scopes = append(scopes, s); return nil })
	operands, err := parseOptions(fs, args)
	if err != nil {
		return err
	}
	command := strings.TrimPrefix(strings.Join(operands, " "), "agent ")
	validFlags := true
	fs.Visit(func(f *flag.Flag) {
		allowed := f.Name == "project" ||
			command == "enroll" && (f.Name == "name" || f.Name == "scope" || f.Name == "allow-self-subject") ||
			command == "token" && (f.Name == "agent" || f.Name == "scope" || f.Name == "subject") ||
			command == "revoke" && f.Name == "agent" || command == "policy set" && f.Name == "scope"
		validFlags = validFlags && allowed
	})
	if !validFlags {
		return errUsage
	}
	if entityid.Check(*project) != nil {
		return fmt.Errorf("agent requires --project <ULID>: %w", errUsage)
	}
	if (*agent != "" && entityid.Check(*agent) != nil) || (*subject != "owner" && *subject != "agent") {
		return errUsage
	}
	switch command {
	case "enroll":
		if *name == "" || len(scopes) == 0 || *agent != "" {
			return errUsage
		}
		return enrollAgent(ctx, *project, *name, scopes, *self, out, prompt)
	case "token":
		if *agent == "" {
			return errUsage
		}
		path, err := agentCredentialPath(*project, *agent)
		if err != nil {
			return err
		}
		info, err := os.Lstat(path)
		if err != nil || !info.Mode().IsRegular() || info.Mode().Perm() != 0o600 {
			return errors.New("Agent credentials must be a regular mode 0600 file")
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return errors.New("Could not read agent credentials")
		}
		var identity agentCredential
		if json.Unmarshal(data, &identity) != nil || identity.AgentID != *agent || entityid.Check(identity.Kid) != nil {
			return errors.New("Invalid stored agent identity")
		}
		parsed, err := x509.ParsePKCS8PrivateKey(identity.Key)
		if err != nil {
			return errors.New("Invalid stored agent key")
		}
		key, valid := parsed.(*ecdsa.PrivateKey)
		if !valid || key.Curve != elliptic.P256() {
			return errors.New("Agent key must be P-256")
		}
		path = "/" + *project + "/oauth/token"
		assertion, err := agentAssertion(key, agentOrigin+path, *agent, identity.Kid)
		if err != nil {
			return err
		}
		form := url.Values{"grant_type": {"urn:ietf:params:oauth:grant-type:jwt-bearer"}, "assertion": {assertion}, "requested_subject": {*subject}}
		if len(scopes) > 0 {
			form.Set("scope", strings.Join(scopes, " "))
		}
		var token struct {
			AccessToken string `json:"access_token"`
			TokenType   string `json:"token_type"`
			ExpiresIn   int    `json:"expires_in"`
		}
		if err := agentRequest(ctx, "POST", path, "", []byte(form.Encode()), true, &token); err != nil {
			return err
		}
		if token.AccessToken == "" || strings.ContainsAny(token.AccessToken, "\r\n\t ") || token.TokenType != "Bearer" || token.ExpiresIn < 1 || token.ExpiresIn > 300 {
			return errors.New("Invalid agent token response")
		}
		if jsonMode(out) {
			return emitJSON(out, token)
		}
		if _, err := fmt.Fprintln(out, token.AccessToken); err != nil {
			return errors.New("Could not write agent token")
		}
		return nil
	case "list", "revoke", "policy get", "policy set":
		if command == "revoke" && *agent == "" {
			return errUsage
		}
	default:
		return errUsage
	}
	credential := os.Getenv("TERRASCALE_API_KEY")
	if credential == "" {
		device, err := tsauth.NewDevice(tsauth.DeviceConfig{})
		if err != nil {
			return err
		}
		credential, err = accessToken(ctx, device)
		if err != nil {
			return err
		}
	}
	root := "/v1/" + *project
	switch command {
	case "revoke":
		if err := agentRequest(ctx, "DELETE", root+"/agents/"+*agent, credential, nil, false, nil); err != nil {
			return err
		}
		return emitJSON(out, struct {
			Revoked string `json:"revoked"`
		}{*agent})
	case "list":
		var page struct {
			Items []struct {
				ID     string `json:"id"`
				Name   string `json:"name"`
				Owner  string `json:"owner_user_id"`
				Status string `json:"status"`
			} `json:"items"`
			Next *string `json:"next"`
		}
		if err := agentRequest(ctx, "GET", root+"/agents", credential, nil, false, &page); err != nil {
			return err
		}
		return emitJSON(out, page)
	case "policy get", "policy set":
		method := "GET"
		var body []byte
		if command == "policy set" {
			method = "PUT"
			body, err = agentJSON(agentPolicy{scopes})
			if err != nil {
				return err
			}
		}
		var policy agentPolicy
		if err := agentRequest(ctx, method, root+"/agent-policy", credential, body, false, &policy); err != nil {
			return err
		}
		return emitJSON(out, policy)
	}
	return errUsage
}
