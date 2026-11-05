package main

import (
	"fmt"
	"io"
	"net/http"
	"strings"
	"time"
)

func (s *sim) publishPlan() error {
	address := fmt.Sprintf("http://127.0.0.1:%d/admin/organizations/%s/plan", s.c.a.adminPort, s.org)
	request, err := http.NewRequestWithContext(s.ctx, http.MethodPut, address, strings.NewReader(`{"plan":"hobby"}`))
	if err != nil {
		return err
	}
	request.Header.Set("Content-Type", "application/json")
	client := &http.Client{Timeout: 10 * time.Second}
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
