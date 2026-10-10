package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"os/signal"
	"syscall"
	"time"

	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/platform/buildinfo"
	"terrascale.tech/dashboard/internal/platform/config"
	"terrascale.tech/dashboard/internal/platform/errortracking"
	"terrascale.tech/dashboard/internal/platform/logging"
)

// shutdownTimeout bounds how long in-flight requests may finish after a
// termination signal before the remaining connections are closed.
const shutdownTimeout = 10 * time.Second

func main() {
	logger := slog.New(errortracking.LogHandler(logging.Handler(os.Stderr)))
	slog.SetDefault(logger.With(logging.Fields(os.Getenv("FLY_REGION"), os.Getenv("FLY_MACHINE_ID"))...))
	if len(os.Args) > 1 {
		command, ok := map[string]func([]string) error{
			"report-internal-billing":    reportInternalBilling,
			"grant-platform-admin":       grantPlatformAdmin,
			"configure-internal-billing": configureInternalBilling,
		}[os.Args[1]]
		if !ok {
			fmt.Fprintf(os.Stderr, "unknown command %q\n", os.Args[1])
			os.Exit(1)
		}
		if err := command(os.Args[2:]); err != nil {
			fmt.Fprintln(os.Stderr, os.Args[1]+":", err)
			os.Exit(1)
		}
		return
	}
	flush := func() {}
	err := run(&flush)
	if err != nil {
		slog.Error("dashboard stopped", "error", err)
	}
	flush()
	if err != nil {
		os.Exit(1)
	}
}

func run(flush *func()) error {
	cfg, err := config.Load()
	if err != nil {
		return fmt.Errorf("invalid configuration: %w", err)
	}
	*flush, err = errortracking.Init(errortracking.Options{
		DSN: cfg.SentryDSN, Release: buildinfo.Version(), Environment: cfg.SentryEnvironment,
	})
	if err != nil {
		return err
	}
	application, err := app.New(cfg)
	if err != nil {
		return fmt.Errorf("could not initialize dashboard: %w", err)
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// Long-lived Datastar streams watch the request context, so they end as
	// soon as shutdown starts instead of holding it open until the timeout.
	base, cancelRequests := context.WithCancel(context.Background())
	defer cancelRequests()
	server := &http.Server{
		Addr:              cfg.Addr,
		Handler:           application.Handler,
		ReadHeaderTimeout: 10 * time.Second,
		IdleTimeout:       120 * time.Second,
		BaseContext:       func(net.Listener) context.Context { return base },
	}
	server.RegisterOnShutdown(cancelRequests)

	serveErr := make(chan error, 1)
	go func() {
		slog.Info("dashboard listening", "addr", cfg.Addr, "demo", cfg.Demo)
		serveErr <- server.ListenAndServe()
	}()
	select {
	case err := <-serveErr:
		return err
	case <-ctx.Done():
	}
	stop()
	slog.Info("dashboard shutting down")
	shutdownCtx, cancel := context.WithTimeout(context.Background(), shutdownTimeout)
	defer cancel()
	if err := server.Shutdown(shutdownCtx); err != nil {
		return err
	}
	if err := <-serveErr; !errors.Is(err, http.ErrServerClosed) {
		return err
	}
	return nil
}
