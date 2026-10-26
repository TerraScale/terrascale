package main

import (
	"fmt"
	"io"
	"slices"
	"strings"
)

var commandSyntax = map[string]string{
	"billing":    "billing",
	"auth login": "auth login", "auth logout": "auth logout", "whoami": "whoami",
	"orgs": "orgs", "projects": "projects", "projects create": "projects create <name>",
	"orgs create": "orgs create <name>", "orgs info": "orgs info <name|id>", "orgs rename": "orgs rename <name|id> <new-name>",
	"projects info": "projects info <name|id>", "projects rename": "projects rename <name|id> <new-name>",
	"orgs delete": "orgs delete <name|id>", "projects delete": "projects delete <name|id>",
	"usage": "usage [<database>]", "completion": "completion bash|zsh|fish",
	"db list": "db list", "db create": "db create <database>", "db delete": "db delete <database>",
	"db branch": "db branch <source> <new-database>", "db info": "db info <database>",
	"db regions": "db regions <database> <region>...|none", "db changes": "db changes <database> [<collection>...]",
	"export": "export <database>", "shell": "shell <database>",
	"backup list": "backup list <database>", "backup create": "backup create <database>",
	"backup restore":    "backup restore <database> <backup-id> <new-database>",
	"backup restore-at": "backup restore-at <database> <RFC3339-time> <new-database> [platform|customer]",
	"backup policy":     "backup policy <database>", "backup policy-set": "backup policy-set <database> <five-field-UTC-cron|none> <days:1..35>",
	"backup window": "backup window <database> [platform|customer]", "backup plan": "backup plan <database> <RFC3339-time> [platform|customer]",
	"collection list": "collection list <database>", "collection create": "collection create <database> <name>",
	"collection drop": "collection drop <database> <name>",
	"doc list":        "doc list <database> <collection>", "doc get": "doc get <database> <collection> <id>",
	"doc put": "doc put <database> <collection> [<id>] <json|->", "doc delete": "doc delete <database> <collection> <id>",
	"doc import": "doc import <database> <collection> < input.jsonl",
	"schema get": "schema get <database> <collection>", "schema set": "schema set <database> <collection> <json|->",
	"schema delete": "schema delete <database> <collection>", "ttl get": "ttl get <database> <collection>",
	"ttl set": "ttl set <database> <collection> <JSON-pointer>", "ttl delete": "ttl delete <database> <collection>",
	"index list": "index list <database> [<collection>]", "index create": "index create <database> <collection> <field>...",
	"index drop": "index drop <database> <name>", "import": "import <database> postgres_plain|sqlite|ndjson <file>",
	"import status": "import status <database>", "import cancel": "import cancel <database>",
	"functions deploy": "functions deploy <database> <file.js|directory>", "functions list": "functions list <database>",
	"functions invoke": "functions invoke <database> <name>", "functions logs": "functions logs <database> <name>",
	"functions delete": "functions delete <database> <name>", "sql": "sql <database> <query|->",
	"exec": "exec <database> <statement|->", "migrate new": "migrate new <name>",
	"migrate up": "migrate up <database>", "migrate status": "migrate status <database>",
	"migrate diff": "migrate diff <database> [<base-database>]", "keys list": "keys list",
	"keys create": "keys create <name> [<database>]", "keys revoke": "keys revoke <id>",
	"context show": "context show", "context clear": "context clear", "context set": "context set <org> <project> [<database>]",
}

var helpFlags = map[string]string{
	"--json":             "typed JSON output; streams use NDJSON record/complete/error events",
	"-project <name|id>": "select a project (before the command)",
	"-org <name|id>":     "select an organization (before the command)",
}

func helpSyntax(name string) string {
	if syntax := commandSyntax[name]; syntax != "" {
		return "ts " + syntax
	}
	return fmt.Sprintf("ts %s (accepted operand counts: %v)", name, commands[name])
}

// requestedHelp validates the command before rendering. Help never authenticates.
func requestedHelp(args []string, out io.Writer) (bool, error) {
	requested := len(args) == 0
	var words []string
	for i := 0; i < len(args); i++ {
		a := args[i]
		if a == "--" {
			break
		}
		if a == "help" && len(words) == 0 || a == "-h" || a == "-help" || a == "--help" {
			requested = true
			continue
		}
		if strings.HasPrefix(a, "-") {
			name, _, equal := strings.Cut(strings.TrimLeft(a, "-"), "=")
			switch name {
			case "project", "org", "scope", "expires", "allow-cidr", "collection", "name", "trigger-collection", "events", "cron", "data", "limit", "cursor":
				if !equal {
					i++
				}
			}
			continue
		}
		words = append(words, a)
	}
	if !requested {
		return false, nil
	}
	if len(words) == 0 {
		if jsonMode(out) {
			return true, emitJSON(out, struct {
				Commands map[string][]int  `json:"commands"`
				Usage    string            `json:"usage"`
				Flags    map[string]string `json:"flags"`
			}{commands, usage, helpFlags})
		}
		_, err := io.WriteString(out, usage)
		return true, err
	}
	selected := words[0]
	if len(words) > 1 {
		candidate := strings.Join(words[:2], " ")
		if _, ok := commands[candidate]; ok {
			selected = candidate
		} else if _, ok := commands[selected]; !ok {
			return true, fmt.Errorf("unknown command; run ts help: %w", errUsage)
		}
	}
	var matches []string
	for name := range commands {
		if name == selected || strings.HasPrefix(name, selected+" ") {
			matches = append(matches, name)
		}
	}
	if len(matches) == 0 {
		return true, fmt.Errorf("unknown command; run ts help: %w", errUsage)
	}
	slices.Sort(matches)
	entries := make([]string, 0, len(matches))
	for _, name := range matches {
		entries = append(entries, helpSyntax(name))
	}
	if jsonMode(out) {
		return true, emitJSON(out, struct {
			Command string            `json:"command"`
			Usage   []string          `json:"usage"`
			Flags   map[string]string `json:"flags"`
		}{selected, entries, helpOptions(selected)})
	}
	options := helpOptions(selected)
	var names []string
	for name := range options {
		names = append(names, name)
	}
	slices.Sort(names)
	for _, name := range names {
		entries = append(entries, "  "+name+"  "+options[name])
	}
	_, err := fmt.Fprintf(out, "%s\n\nGlobal options (before the command): -project <name|id>, -org <name|id>.\n--json selects typed output. Exit status: 0 success, 1 failure, 2 invalid arguments.\n", strings.Join(entries, "\n"))
	return true, err
}

// Parser diagnostics describe the failure without echoing possibly secret values.
func parserError(err error) error {
	message := "invalid option or value"
	switch {
	case strings.HasPrefix(err.Error(), "flag provided but not defined:"):
		message = "unknown option"
	case strings.HasPrefix(err.Error(), "flag needs an argument:"):
		message = "option requires a value"
	case strings.HasPrefix(err.Error(), "invalid value"):
		message = "option has an invalid value or type"
	}
	return fmt.Errorf("%s; run ts help <command>: %w", message, errUsage)
}

func helpOptions(name string) map[string]string {
	options := make(map[string]string, len(helpFlags)+4)
	for flag, text := range helpFlags {
		options[flag] = text
	}
	switch {
	case strings.HasPrefix(name, "keys create"):
		options["-scope read|read-write"] = "requested grants; default read-write, server permissions still apply"
		options["-expires 30d|90d|365d|never"] = "key expiry; default never"
		options["-allow-cidr <CIDR>,..."] = "restrict source networks; default any address"
	case name == "import":
		options["-collection <name>"] = "destination collection for plain document NDJSON"
		options["-yes"] = "consent to the inspected import preview for unattended execution"
	case name == "db delete" || name == "collection drop" || name == "functions delete" || name == "orgs delete" || name == "projects delete":
		options["-yes"] = "explicit consent to permanent deletion; otherwise type the target in a terminal"
	case name == "functions deploy":
		options["-name <name>"] = "function name, default file/directory name"
		options["-trigger-collection <name> -events upsert,delete"] = "collection event trigger"
		options["-cron <expression>"] = "function schedule"
	case name == "functions invoke":
		options["-data <json|->"] = "invocation body; - reads stdin"
	case name == "functions logs":
		options["-limit <1..100>"] = "maximum recent invocations"
	}
	return options
}
