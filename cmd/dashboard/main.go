package main

import (
	"log/slog"
	"net/http"
	"os"
	"time"

	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/config"
)

func main() {
	cfg, err := config.Load()
	if err != nil {
		slog.Error("invalid configuration", "error", err)
		os.Exit(1)
	}
	application, err := app.New(cfg)
	if err != nil {
		slog.Error("could not initialize dashboard", "error", err)
		os.Exit(1)
	}
	server := &http.Server{
		Addr: cfg.Addr, Handler: application.Handler,
		ReadHeaderTimeout: 10 * time.Second, IdleTimeout: 120 * time.Second,
	}
	slog.Info("dashboard listening", "addr", cfg.Addr, "demo", cfg.Demo)
	if err := server.ListenAndServe(); err != nil && err != http.ErrServerClosed {
		slog.Error("dashboard stopped", "error", err)
		os.Exit(1)
	}
}
