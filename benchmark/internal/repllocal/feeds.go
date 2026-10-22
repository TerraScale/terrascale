package repllocal

import (
	"bufio"
	"context"
	"crypto/tls"
	"crypto/x509"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"strconv"
	"strings"
	"sync"
	"terrascale.tech/ts-benchmark/internal/replbench"
	"time"
)

func OpenFeeds(ctx context.Context, targets replbench.Targets, n int) (stop func() error, admitted int, rejected int, err error) {
	target, ok := targets.Regions["gru"]
	if n < 0 || !ok {
		return nil, 0, 0, errors.New("feeds need gru and a nonnegative count")
	}
	transport := &http.Transport{DisableKeepAlives: true, ResponseHeaderTimeout: 30 * time.Second}
	if target.CAPEM != "" {
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM([]byte(target.CAPEM)) {
			return nil, 0, 0, errors.New("feed target has invalid CA")
		}
		transport.TLSClientConfig = &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}
	}
	client := &http.Client{Transport: transport, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	run, cancel := context.WithCancel(ctx)
	deadline := time.AfterFunc(30*time.Second, cancel)
	var wg sync.WaitGroup
	var mu sync.Mutex
	var failures []error
	fail := func(err error) { mu.Lock(); failures = append(failures, err); mu.Unlock() }
	stop = sync.OnceValue(func() error {
		deadline.Stop()
		cancel()
		wg.Wait()
		transport.CloseIdleConnections()
		return errors.Join(append(failures, ctx.Err())...)
	})
	endpoint := strings.TrimSuffix(target.URL, "/") + "/v1/projects/" + targets.Project + "/databases/" + targets.Database + "/changes?cursor=now"
	for range n {
		outcome := make(chan int, 1)
		wg.Go(func() {
			reported := false
			defer func() {
				if !reported {
					outcome <- 0
				}
			}()
			for run.Err() == nil {
				req, err := http.NewRequestWithContext(run, http.MethodGet, endpoint, nil)
				if err != nil {
					fail(errors.New("invalid feed URL"))
					return
				}
				req.Header.Set("Authorization", "Bearer "+target.Token)
				req.Header.Set("Accept", "text/event-stream")
				if target.Pin {
					req.Header.Set("fly-prefer-region", "gru")
				}
				resp, err := client.Do(req)
				if err != nil {
					fail(errors.New("feed request failed"))
					return
				}
				if resp.StatusCode != http.StatusOK {
					var refusal struct{ Error struct{ Code string } }
					decodeErr := json.NewDecoder(io.LimitReader(resp.Body, 4096)).Decode(&refusal)
					resp.Body.Close()
					if decodeErr == nil && resp.StatusCode == http.StatusTooManyRequests {
						switch refusal.Error.Code {
						case "changes_busy": // The real 32-stream cap is an expected rejection.
							reported = true
							outcome <- -1
							return
						case "tenant_busy":
							seconds, _ := strconv.Atoi(resp.Header.Get("Retry-After"))
							if probePause(run, max(200*time.Millisecond, time.Duration(seconds)*time.Second)) {
								continue
							}
							return
						}
					}
					reason := "unknown refusal"
					switch refusal.Error.Code {
					case "tenant_memory", "storage_busy", "cursor_expired", "cursor_elsewhere", "invalid_cursor", "not_found", "unauthorized", "forbidden":
						reason = refusal.Error.Code
					}
					fail(fmt.Errorf("unexpected feed refusal: HTTP %d %s", resp.StatusCode, reason))
					return
				}
				defer resp.Body.Close()
				if target.Pin && resp.Header.Get("fly-region") != "gru" || !strings.HasPrefix(resp.Header.Get("Content-Type"), "text/event-stream") {
					fail(errors.New("feed answered from wrong region or with wrong content type"))
					return
				}
				scanner := bufio.NewScanner(resp.Body)
				scanner.Buffer(make([]byte, 4096), 64<<10)
				event := ""
				hasData := false
				for scanner.Scan() {
					line := scanner.Text()
					if value, ok := strings.CutPrefix(line, "event:"); ok {
						event = strings.TrimSpace(value)
					}
					if strings.HasPrefix(line, "data:") {
						hasData = true
					}
					if line != "" {
						continue
					}
					if event == "error" {
						fail(errors.New("feed emitted error event"))
						return
					}
					if !reported && event == "cursor" && hasData {
						reported = true
						outcome <- 1
					}
					event = ""
					hasData = false
				}
				if run.Err() == nil {
					fail(errors.New("feed ended before cancellation"))
				}
				return
			}
		})
		select {
		case result := <-outcome:
			switch result {
			case 1:
				admitted++
			case -1:
				rejected++
			default:
				err = errors.New("feed failed before admission")
			}
		case <-run.Done():
			err = errors.New("feed admission canceled or exceeded 30 seconds")
		}
		if err != nil {
			return stop, admitted, rejected, errors.Join(err, stop())
		}
	}
	if !deadline.Stop() || run.Err() != nil {
		return stop, admitted, rejected, errors.Join(errors.New("feed admission deadline/cancellation"), stop())
	}
	mu.Lock()
	err = errors.Join(failures...)
	mu.Unlock()
	if err != nil {
		err = errors.Join(err, stop())
	}
	return stop, admitted, rejected, err
}
