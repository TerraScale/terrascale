package main

import (
	"bytes"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strings"
	"testing"
)

func TestCompletionGeneratorContext(t *testing.T) {
	t.Parallel()
	for _, shell := range []string{"bash", "zsh", "fish"} {
		var out bytes.Buffer
		if err := completion(shell, &out); err != nil {
			t.Fatal(err)
		}
		for _, word := range []string{"project", "org", "expires", "allow-cidr", "collection", "yes", "bash", "fish", "zsh"} {
			if !strings.Contains(out.String(), word) {
				t.Errorf("%s generator lacks %s", shell, word)
			}
		}
		if shell == "zsh" && (!strings.Contains(out.String(), "$+functions[compdef]") || !strings.Contains(out.String(), "$+functions[complete]")) {
			t.Error("zsh initialization must guard both completion systems")
		}
	}
}

func TestCompletionRealShells(t *testing.T) {
	t.Parallel()
	// Bash is required in CI. Exercise additional installed shells without
	// presenting an absent tool as a successful or skipped shell check.
	shells := []string{"bash"}
	for _, shell := range []string{"zsh", "fish"} {
		if _, err := exec.LookPath(shell); err == nil {
			shells = append(shells, shell)
		}
	}
	for _, shell := range shells {
		t.Run(shell, func(t *testing.T) {
			t.Parallel()
			bin, err := exec.LookPath(shell)
			if err != nil {
				t.Fatalf("required shell %s unavailable: %v", shell, err)
			}
			db := "branch changes create delete info list regions"
			type probe struct {
				args []string
				want string
			}
			probes := []probe{
				{[]string{"keys", ""}, "create list revoke rotate"},
				{[]string{"-project", "demo", "keys", "r"}, "revoke rotate"},
				{[]string{"db", ""}, db},
				{[]string{"-project", "demo", "db", ""}, db},
				{[]string{"-project", "demo", "d"}, "db doc"},
				{[]string{"-project", "demo", "completion", ""}, "bash fish zsh"},
				{[]string{"completion", "z"}, "zsh"},
				{[]string{"-p"}, "-placement -project"},
				{[]string{"--p"}, "--placement --project"},
				{[]string{"backup", "destination", ""}, "delete get set"},
				{[]string{"-project", "demo", "backup", "destination", "s"}, "set"},
				{[]string{"-yes", "db", ""}, db},
				{[]string{"--yes=false", "db", ""}, db},
				{[]string{"-yes=true", "db", ""}, db},
				{[]string{"-yes=false", "db", ""}, db},
				{[]string{"--yes", "completion", ""}, "bash fish zsh"},
				{[]string{"-org", "db", "-project", "my project", "db", "c"}, "changes create"},
				{[]string{"-project", "db", "db", "list", ""}, ""},
			}
			for _, flag := range []string{"project", "org", "expires", "allow-cidr", "collection", "placement", "home-region", "replica-regions", "cursor"} {
				for _, prefix := range []string{"-", "--"} {
					probes = append(probes,
						probe{[]string{prefix + flag, "db", "db", ""}, db},
						probe{[]string{prefix + flag + "=db", "db", ""}, db},
						probe{[]string{prefix + flag, ""}, ""})
				}
			}
			if shell == "bash" {
				// Readline splits '=' using COMP_WORDBREAKS by default.
				probes = append(probes, probe{[]string{"-project", "=", "demo", "db", ""}, db})
			}
			quote := func(s string) string { return "'" + strings.ReplaceAll(s, "'", "'\\''") + "'" }
			dir := t.TempDir()
			var generated, script bytes.Buffer
			if err := completion(shell, &generated); err != nil {
				t.Fatal(err)
			}
			path := filepath.Join(dir, "completion")
			if err := os.WriteFile(path, generated.Bytes(), 0o600); err != nil {
				t.Fatal(err)
			}
			fmt.Fprintf(&script, "source %s\n", quote(path))
			if shell == "zsh" {
				// Cold source must register ts; subsequent sources must preserve setup.
				script.WriteString("[[ ${_comps[ts]} == '_bash_complete -F _ts' ]] || exit 90\n")
				script.WriteString("compinit() { return 91; }; bashcompinit() { return 92; }\n")
				fmt.Fprintf(&script, "source %s || exit 93\n", quote(path))
				script.WriteString("unfunction complete bashcompinit; autoload -Uz bashcompinit\n")
				fmt.Fprintf(&script, "source %s || exit 94\n", quote(path))
			}
			for i, p := range probes {
				fmt.Fprintf(&script, "printf 'CASE:%d\\n'\n", i)
				args := make([]string, 0, len(p.args))
				for _, arg := range p.args {
					args = append(args, quote(arg))
				}
				if shell == "fish" {
					args[len(args)-1] = p.args[len(p.args)-1]
					fmt.Fprintf(&script, "complete -C %s\n", quote("ts "+strings.Join(args, " ")))
				} else {
					fmt.Fprintf(&script, "COMP_WORDS=(ts %s); COMP_CWORD=%d; _ts\nprintf '%%s\\n' \"${COMPREPLY[@]}\"\n", strings.Join(args, " "), len(p.args))
				}
			}
			options := []string{"--noprofile", "--norc", "-c"}
			if shell == "zsh" {
				options = []string{"-f", "-c"}
			} else if shell == "fish" {
				options = []string{"--no-config", "-c"}
			}
			cmd := exec.CommandContext(t.Context(), bin, append(options, script.String())...)
			cmd.Dir = dir
			cmd.Env = []string{"PATH=" + os.Getenv("PATH"), "TERM=xterm", "LC_ALL=C"}
			for _, key := range []string{"HOME", "ZDOTDIR", "XDG_CONFIG_HOME", "XDG_CACHE_HOME", "XDG_DATA_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR"} {
				path := filepath.Join(dir, key)
				if err := os.Mkdir(path, 0o700); err != nil {
					t.Fatal(err)
				}
				cmd.Env = append(cmd.Env, key+"="+path)
			}
			var stderr bytes.Buffer
			cmd.Stderr = &stderr
			output, err := cmd.Output()
			if err != nil || stderr.Len() != 0 {
				t.Fatalf("shell probe: %v; stderr: %s; stdout: %s", err, &stderr, output)
			}
			sections := strings.Split(string(output), "CASE:")[1:]
			if len(sections) != len(probes) {
				t.Fatalf("got %d probe sections, want %d", len(sections), len(probes))
			}
			for i, section := range sections {
				_, body, _ := strings.Cut(section, "\n")
				got := strings.Fields(body)
				slices.Sort(got)
				if strings.Join(got, " ") != probes[i].want {
					t.Errorf("ts %q: got %q, want %q", probes[i].args, got, probes[i].want)
				}
			}
			t.Logf("%s: %d real-shell probes executed", bin, len(probes))
		})
	}
}
