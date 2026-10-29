package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"strings"
)

// Regions is the ordered live serving-region catalog, used for database placement.
// Region ids are lowercase and may include regions unknown when this SDK shipped.
type Regions struct {
	Regions []string `json:"regions"`
}

// ListRegions reads anonymous GET /v1/regions at the fixed global API origin
// (or explicit test origin). It sends once, with no credentials, cache, regional
// discovery or failover. The server permits HTTP caching for five seconds.
func (c *Client) ListRegions(ctx context.Context) (*Regions, error) {
	if ctx == nil {
		return nil, &ValidationError{Field: "context", Message: "cannot be nil"}
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	requestCtx, cancel := context.WithTimeout(ctx, MaxRequestTimeout)
	defer cancel()
	status, headers, body, err := c.tsAuthRoundTrip(requestCtx, c.cfg.APIBaseURL, http.MethodGet, "/v1/regions", nil, nil, "", tsAuthAuthorization{}, "listRegions")
	if err != nil {
		var apiErr *APIError
		if errors.As(err, &apiErr) && apiErr.Code == "response_too_large" {
			return nil, apiErr
		}
		if contextErr := requestCtx.Err(); contextErr != nil {
			return nil, contextErr
		}
		if apiErr != nil {
			return nil, apiErr
		}
		return nil, fmt.Errorf("sdk: GET /v1/regions (listRegions): %w", err)
	}
	if status < 200 || status >= 300 {
		return nil, tsAuthErrorFromResponse("listRegions", http.MethodGet, "/v1/regions", status, headers, body)
	}
	var out Regions
	if err := decodeSpecialResponse(&out, status, body); err != nil {
		return nil, err
	}
	return &out, nil
}

func (r *Regions) decodeHTTPResponse(status int, body []byte) error {
	if status != http.StatusOK || json.Unmarshal(body, r) != nil || len(r.Regions) == 0 {
		return errors.New("invalid region catalog")
	}
	for i, region := range r.Regions {
		if !isRegionID(region) || i > 0 && r.Regions[i-1] >= region {
			return errors.New("invalid region catalog")
		}
	}
	return nil
}

// Mirrors TerraBaseOptions.IsRegion at the pinned producer; not a serving list.
func isRegionID(region string) bool {
	if len(region) == 0 || len(region) > 63 || region[0] < 'a' || region[0] > 'z' || region[len(region)-1] == '-' {
		return false
	}
	for _, ch := range region {
		if !(ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9' || ch == '-') {
			return false
		}
	}
	return true
}

// Reject non-ASCII characters before Unicode case normalization can change them.
func normalizeRegionCode(input string) (RegionCode, bool) {
	input = strings.TrimSpace(input)
	for _, ch := range input {
		if ch > 127 {
			return "", false
		}
	}
	canonical := strings.ToLower(input)
	return RegionCode(strings.ToUpper(canonical)), isRegionID(canonical)
}
