package main

import (
	"errors"

	"terrascale.tech/dashboard/internal/platform/config"
	v1 "terrascale.tech/sdk/v1"
)

// openDatabase connects to the configured dashboard database.
func openDatabase() (config.Config, *v1.Database, error) {
	cfg, err := config.Load()
	if err != nil {
		return cfg, nil, err
	}
	if cfg.TerraBaseURL == "" {
		return cfg, nil, errors.New("TERRABASE_URL, TERRABASE_API_KEY, TERRABASE_PROJECT_ID and TERRABASE_DATABASE are required")
	}
	client, err := v1.New(v1.Config{BaseURL: cfg.TerraBaseURL, Token: cfg.TerraBaseAPIKey})
	if err != nil {
		return cfg, nil, err
	}
	return cfg, client.Database(cfg.TerraBaseProjectID, cfg.TerraBaseDatabase), nil
}
