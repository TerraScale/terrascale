package main

import (
	"context"
	"errors"
	"flag"
	"os"
	"os/exec"
	"strings"
	"testing"
	"time"
)

func TestCommandDispatch(t *testing.T) {
	if os.Getenv("TS_DASHBOARD_CLI_TEST_PROCESS") == "1" {
		os.Args = append(os.Args[:1], flag.Args()...)
		main()
		return
	}
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	for _, tt := range []struct {
		command string
		message string
	}{
		{"import-org", `unknown command "import-org"`},
		{"unknown", `unknown command "unknown"`},
		{"--help", `unknown command "--help"`},
		{"grant-platform-admin", "grant-platform-admin: --email is required"},
	} {
		t.Run(tt.command, func(t *testing.T) {
			ctx, cancel := context.WithTimeout(t.Context(), 5*time.Second)
			defer cancel()
			cmd := exec.CommandContext(ctx, executable, "-test.run=^TestCommandDispatch$", "--", tt.command)
			// Partial backend configuration fails before any server or external
			// call if a command accidentally falls through to normal startup.
			cmd.Env = []string{"TS_DASHBOARD_CLI_TEST_PROCESS=1", "TS_DASHBOARD_PRODUCTION_DEMO=1", "TERRABASE_URL=http://127.0.0.1:1"}
			output, err := cmd.CombinedOutput()
			var exit *exec.ExitError
			if !errors.As(err, &exit) || exit.ExitCode() != 1 {
				t.Fatalf("exit = %v, want 1; output: %s", err, output)
			}
			if strings.TrimSpace(string(output)) != tt.message {
				t.Fatalf("output = %q, want %q", output, tt.message)
			}
		})
	}
}
