package main

import (
	"fmt"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"
)

func (s *sim) publishPlan() error {
	secret, err := os.ReadFile(filepath.Join(filepath.Dir(s.c.a.dir), "peer-secret"))
	if err != nil {
		return err
	}
	address := fmt.Sprintf("http://%s:%d/admin/organizations/%s/plan", s.c.a.ip, s.c.a.internalPort, s.org)
	request, err := http.NewRequestWithContext(s.ctx, http.MethodPut, address, strings.NewReader(`{"plan":"hobby"}`))
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("X-TerraBase-Peer-Credential", strings.TrimSpace(string(secret)))
	protocols := new(http.Protocols)
	protocols.SetUnencryptedHTTP2(true)
	transport := &http.Transport{Protocols: protocols}
	defer transport.CloseIdleConnections()
	client := &http.Client{Timeout: 10 * time.Second, Transport: transport}
	response, err := client.Do(request)
	if err != nil {
		return fmt.Errorf("publish synthetic organization plan: %w", err)
	}
	defer response.Body.Close()
	if response.StatusCode != http.StatusOK {
		body, _ := io.ReadAll(io.LimitReader(response.Body, 1024))
		return fmt.Errorf("publish synthetic organization plan: HTTP %d %s", response.StatusCode, body)
	}
	return nil
}
