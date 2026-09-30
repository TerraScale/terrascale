// Package e2e drives the dashboard through real Chromium with playwright-go.
//
// By default every run starts the dashboard in-process on a loopback port.
// Set DASHBOARD_E2E_BASE_URL to exercise an already-running deployment.
// Tests that need an in-process fixture are skipped with -short.
// Run with -parallel 1 (or make test-fast/test-slow): concurrent Chromium
// journeys can starve the browser and time out under the race detector.
package e2e

import (
	"cmp"
	"errors"
	"fmt"
	"net/http/httptest"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strconv"
	"strings"
	"testing"

	"github.com/mxschmitt/playwright-go"

	"terrascale.tech/dashboard/internal/app"
	"terrascale.tech/dashboard/internal/features/auth"
	"terrascale.tech/dashboard/internal/platform/config"
)

var (
	baseURL string
	admin   credentials
	browser playwright.Browser
	expect  = playwright.NewPlaywrightAssertions(10_000)
)

func TestMain(m *testing.M) {
	os.Exit(run(m))
}

func run(m *testing.M) int {
	// The in-process dashboard runs under the race detector, where full-cost
	// password hashing queues parallel sign-ups past the browser timeouts.
	auth.UseTestPasswordCost()
	admin = credentials{
		Email:    cmp.Or(os.Getenv("DASHBOARD_E2E_ADMIN_EMAIL"), "e2e-admin-seed@example.test"),
		Password: cmp.Or(os.Getenv("DASHBOARD_E2E_ADMIN_PASSWORD"), "E2e-admin-seed-123!"),
	}
	baseURL = os.Getenv("DASHBOARD_E2E_BASE_URL")
	if baseURL == "" {
		server, err := startDashboard()
		if err != nil {
			fmt.Fprintln(os.Stderr, "e2e: start dashboard:", err)
			return 1
		}
		defer server.Close()
		baseURL = server.URL
	}

	pw, err := startPlaywright()
	if err != nil {
		fmt.Fprintln(os.Stderr, "e2e: start playwright:", err)
		return 1
	}
	defer pw.Stop()
	browser, err = launchChromium(pw)
	if err != nil {
		fmt.Fprintln(os.Stderr, "e2e: launch chromium:", err)
		return 1
	}
	defer browser.Close()
	return m.Run()
}

func startDashboard() (*httptest.Server, error) {
	application, err := app.New(config.Config{Demo: true, AdminEmail: admin.Email, AdminPassword: admin.Password})
	if err != nil {
		return nil, err
	}
	return httptest.NewServer(application.Handler), nil
}

// launchChromium prefers the driver's own Chromium revision and otherwise
// falls back to the newest installed headless shell, such as the one baked
// into the CI image for a newer Playwright release.
func launchChromium(pw *playwright.Playwright) (playwright.Browser, error) {
	options := playwright.BrowserTypeLaunchOptions{Timeout: new(30_000.0)}
	launched, err := pw.Chromium.Launch(options)
	if err == nil {
		return launched, nil
	}
	root := os.Getenv("PLAYWRIGHT_BROWSERS_PATH")
	if root == "" {
		cache, cacheErr := os.UserCacheDir()
		if cacheErr != nil {
			return nil, errors.Join(err, cacheErr)
		}
		root = filepath.Join(cache, "ms-playwright")
	}
	shells, _ := filepath.Glob(filepath.Join(root, "chromium_headless_shell-*", "chrome-headless-shell-linux64", "chrome-headless-shell"))
	if len(shells) == 0 {
		return nil, err
	}
	slices.SortFunc(shells, func(a, b string) int { return cmp.Compare(revision(a), revision(b)) })
	options.ExecutablePath = new(shells[len(shells)-1])
	return pw.Chromium.Launch(options)
}

func revision(shell string) int {
	dir := filepath.Base(filepath.Dir(filepath.Dir(shell)))
	n, _ := strconv.Atoi(strings.TrimPrefix(dir, "chromium_headless_shell-"))
	return n
}

// startPlaywright runs the repository's pinned playwright-core driver from
// node_modules, so no driver is downloaded at test time.
func startPlaywright() (*playwright.Playwright, error) {
	if os.Getenv("PLAYWRIGHT_CLI_PATH") == "" {
		cli, err := filepath.Abs("../../node_modules/playwright-core/cli.js")
		if err != nil {
			return nil, err
		}
		if _, err := os.Stat(cli); err != nil {
			return nil, fmt.Errorf("run npm ci to install the Playwright driver: %w", err)
		}
		os.Setenv("PLAYWRIGHT_CLI_PATH", cli)
	}
	if os.Getenv("PLAYWRIGHT_NODEJS_PATH") == "" {
		node, err := exec.LookPath("node")
		if err != nil {
			return nil, err
		}
		os.Setenv("PLAYWRIGHT_NODEJS_PATH", node)
	}
	return playwright.Run(&playwright.RunOptions{SkipInstallBrowsers: true, Verbose: false})
}
