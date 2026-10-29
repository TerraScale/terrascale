package v1

import (
	"context"
	"errors"
	"fmt"
	"io"
	"mime"
	"net/http"
	"strings"
	"time"
	"unicode/utf8"
)

const prometheusMedia = "text/plain; version=0.0.4; charset=utf-8"

// PrometheusMetrics owns the original project-scoped exposition bytes and media
// type. Treat Body as immutable when sharing the result between goroutines.
// These best-effort observations are not billing truth.
type PrometheusMetrics struct {
	ContentType string
	Body        []byte
}

// PrometheusProtocolError reports an unexpected success status, media, encoding,
// body size or read failure. It retains no exposition or credentials.
type PrometheusProtocolError struct {
	Status int
	cause  error
}

func (e *PrometheusProtocolError) Error() string { return "terrabase: invalid Prometheus response" }
func (e *PrometheusProtocolError) Unwrap() error { return e.cause }

// ProjectPrometheusMetrics reads GET project/metrics with the client's project
// access token and databases:read, preserving database narrowing. It sends once,
// without refreshing a refused token, retrying or following redirects. The 30s
// deadline includes endpoint/token acquisition and body reads; earlier caller
// deadlines win. Exposition is bounded to 8 MiB, with no background scraping.
func (c *Client) ProjectPrometheusMetrics(ctx context.Context, project string) (out *PrometheusMetrics, err error) {
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	defer func() {
		if cause := ctx.Err(); cause != nil {
			out, err = nil, cause
		}
	}()
	if err = ctx.Err(); err != nil {
		return nil, err
	}
	if err = checkID(project); err != nil {
		return nil, err
	}
	target, err := c.url(ctx, []string{"v1", "projects", project, "metrics"}, nil)
	if err != nil {
		return nil, err
	}
	token, err := c.bearerToken(ctx, "")
	if err != nil {
		return nil, err
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, target, nil)
	if err != nil {
		return nil, err
	}
	req.Header.Set("Accept", prometheusMedia)
	req.Header.Set("Authorization", "Bearer "+token)
	res, err := c.hc.Do(req)
	if err != nil {
		return nil, fmt.Errorf("terrabase: project Prometheus request: %w", err)
	}
	defer res.Body.Close()
	if res.StatusCode < 200 || res.StatusCode >= 300 {
		data, readErr := io.ReadAll(io.LimitReader(res.Body, (64<<10)+1))
		if len(data) > 64<<10 {
			data = data[:64<<10]
			readErr = errors.Join(readErr, &PrometheusProtocolError{Status: res.StatusCode})
		}
		api := parseAPIError(res.StatusCode, res.Header, data)
		api.cause = readErr
		return nil, api
	}
	invalid := &PrometheusProtocolError{Status: res.StatusCode}
	contentType := res.Header.Get("Content-Type")
	media, params, parseErr := mime.ParseMediaType(contentType)
	limit := min(int64(8<<20), c.maxResponse)
	if res.StatusCode != http.StatusOK || parseErr != nil || !strings.EqualFold(media, "text/plain") || params["version"] != "0.0.4" || !strings.EqualFold(params["charset"], "utf-8") || res.ContentLength > limit {
		return nil, invalid
	}
	data, readErr := io.ReadAll(io.LimitReader(res.Body, limit+1))
	if readErr != nil || int64(len(data)) > limit || !utf8.Valid(data) {
		invalid.cause = readErr
		return nil, invalid
	}
	return &PrometheusMetrics{ContentType: contentType, Body: data}, nil
}
