// Command ts is the TerraScale CLI: device sign-in, projects, databases,
// SQL, functions and API keys against the production API, as the signed-in
// user. Run ts help for every command.
//
// Project commands act in the user's only project; -project <name or id>
// picks one when the user reaches several, and -org <name or id> restricts
// project selection to one unambiguous organization.
package main

import (
	"cmp"
	"context"
	"encoding/base64"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"iter"
	"os"
	"os/signal"
	"slices"
	"strings"
	"text/tabwriter"
	"time"

	"terrascale.tech/sdk/internal/entityid"
	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

var version = "dev" // Set by release builds with -ldflags.

const usage = `usage: ts [--json] [-project <name or id>] [-org <name or id>] [-yes] <command>

Options go before or after the command and its operands; -- ends options.

  version                                 print the CLI version
  help                                    print this help
  auth login | logout                     sign in with your browser, or sign out
  whoami                                  print the signed-in account
  billing                                 print the dashboard billing URL (offline)
  context show | clear                    inspect or clear saved selectors
  context set <org> <project> [<database>] save verified selectors; @ uses saved DB
  orgs                                    list every accessible organization
  orgs create <name> | info <org>          create or inspect an organization
  orgs rename <org> <name>                rename an organization (admin)
  orgs delete <org>                       delete an empty organization (owner)
  projects                                list the projects you can reach
  projects info <project>                 inspect a project; -org restricts lookup
  projects rename <project> <name>        rename a project (admin)
  projects delete <project>               delete identity project after all DBs;
                                          requires typed ID or explicit -yes
  projects create <name>                  create a project (and an organization
                                          when you have none)
  usage [<db>]                            print the project's or a database's
                                          usage as JSON
  completion bash | zsh | fish            print a shell completion script

  db list | create <db> | delete <db>     manage the project's databases
                                          create placement: -home-region <region>,
                                          -replica-regions <a,b> | -all-regions
                                          delete requires typing the database
                                          name; -yes before db delete consents
                                          explicitly for unattended use
  db branch create <name> --from <db> [--ttl 7d]
  db branch list                         list database branches
  db branch delete <name>                 delete a branch (--yes for automation)
  db branch url <name>                    issue a connection URL for the Postgres
                                          wire endpoint (SQLite SQL dialect)
  db branch <source> <db>                 copy a database into a new one
  databases rename <database> <new-name>  rename by name or immutable ULID
                                          alias: db rename
  db info <db>                            print a database's metadata as JSON
  db url <db>                             issue a connection URL for the Postgres
                                          wire endpoint (SQLite SQL dialect)
  db export <db> [-o file]               download the database as a SQL dump
  db import <db> <file|->                load a SQL dump; not atomic: each batch
                                          commits on its own; resume with -from n
  db placement <db> (-replica-regions <a,b> | -all-regions)
                                          set the database's replica regions
  db move-home <db> <region>              move the home to a replica region
  db regions <db>                         print the home region and each
                                          replica's pull progress
  backup list | create <db>               list or take a database's backups
  backup policy <db>                      read the snapshot schedule and retention
  backup policy-set <db> <cron|none> <days> set five-field UTC cron and 1..35 days
  backup destination get|set|delete      project backup bucket; set reads credential
                                          JSON from stdin and requires -yes
  backup window <db> [platform|customer]  inspect the proven restore interval
  backup plan <db> <time> [platform|customer] preview restore metadata
  backup restore <db> <backup id> <new db> restore a backup into a new database
  backup restore-at <db> <time> <new db> [platform|customer]
                                          restore an RFC3339 instant (default platform)
  backup rewind <db> <time> [platform|customer]
                                          restore the database in place to an RFC3339
                                          instant; later changes are lost; requires -yes
  shell <db>                              multiline SQL; .run submits one statement;
                                          session history
  functions deploy <db> <file.js | dir>   deploy or replace a JavaScript function
                                          (a dir deploys its index.js); the file
                                          is a plain script, not an ES module
                                          (no import or export), that defines
                                          function handler(event); flags:
                                          -name <name> (default: file or dir
                                          name), -cron <expr>
  functions list <db>                     list the database's functions
  functions invoke <db> <name>            run a function and print its response;
                                          -data <json> sends a body, - stdin
  functions logs <db> <name>              print recent invocations as JSON
                                          lines, newest first; -limit 1..100
  functions delete <db> <name>            delete a function and its logs
  sql <db> <statement>                    run one SQL statement in its own
                                          transaction; rows print as JSON lines
  schema pull                             write schema.sql from --database <db>
  schema diff <schema.sql>                print DDL for --database <db>
  schema apply <schema.sql>               apply atomically; --dry-run previews;
                                          --allow-drop consents to DROP operations
  migrate new <name>                      add the next migrations/NNNN_<name>.sql
  migrate up | status <db>                apply pending migrations in order, each
                                          in one transaction recorded in the
                                          ts_migrations table, or list them
  migrate diff <db> [<base>]              print the tables, indexes, views and
                                          triggers a database adds, drops or changes
                                          against base, by default the database
                                          it branched from
  keys list                               list the project's API keys
  keys create <name> [<database-ULID>]     create an API key, shown once;
                                          -scope read requests read-only grants;
                                          default scope is read-write;
                                          set its lifetime and networks with
                                          -expires 30d|90d|365d|never (default
                                          never) and -allow-cidr <cidr>,... (both
                                          go before the command)
  keys rotate <id> <grace>                rotate once; required whole-second grace 0s..24h
  keys revoke <id>                        revoke an API key; requires typing
                                          the ID or explicit -yes

Sign-in is stored in the user config directory (terrascale/cli.json, mode 0600).
With TERRASCALE_API_KEY set, database commands act as that API key instead,
in its project or the -project <id> of an organization key, such as in CI.
Exit status: 0 success, 1 failure, 2 invalid arguments.
`

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	if err := run(ctx, os.Args[1:], os.Stdout); err != nil {
		var reported *jsonFailure
		if !errors.As(err, &reported) {
			fmt.Fprintln(os.Stderr, "ts:", err)
		}
		os.Exit(exitStatus(err))
	}
}

var errUsage = errors.New("invalid arguments: run ts help")

// parseOptions accepts the global options before, between and after the
// command words and operands. After the command only defined option names are
// options, so - (stdin) and other dash operands stay operands; -- ends options.
func parseOptions(fs *flag.FlagSet, args []string) ([]string, error) {
	var operands []string
	for {
		if err := fs.Parse(args); err != nil {
			return nil, parserError(err)
		}
		if n := len(args) - fs.NArg(); n > 0 && args[n-1] == "--" {
			return append(operands, fs.Args()...), nil
		}
		args = fs.Args()
		i := slices.IndexFunc(args, func(a string) bool {
			name, _, _ := strings.Cut(strings.TrimPrefix(strings.TrimPrefix(a, "-"), "-"), "=")
			return a == "--" || len(a) > 1 && a[0] == '-' && fs.Lookup(name) != nil
		})
		if i < 0 {
			return append(operands, args...), nil
		}
		operands, args = append(operands, args[:i]...), args[i:]
	}
}

// commands maps each command to its accepted operand counts.
var commands = map[string][]int{
	"auth login":         {0},
	"auth logout":        {0},
	"whoami":             {0},
	"billing":            {0},
	"projects":           {0},
	"orgs":               {0},
	"orgs create":        {1},
	"orgs info":          {1},
	"orgs rename":        {2},
	"orgs delete":        {1},
	"projects info":      {1},
	"projects rename":    {2},
	"projects delete":    {1},
	"context show":       {0},
	"context clear":      {0},
	"context set":        {2, 3},
	"projects create":    {1},
	"usage":              {0, 1},
	"completion":         {1},
	"shell":              {1},
	"backup restore-at":  {3, 4},
	"backup rewind":      {2, 3},
	"backup policy":      {1},
	"backup policy-set":  {3},
	"backup window":      {1, 2},
	"backup plan":        {2, 3},
	"backup destination": {1},
	"db rename":          {2},
	"databases rename":   {2},
	"db list":            {0},
	"db create":          {1},
	"db delete":          {1},
	"db branch":          {2},
	"db branch create":   {1},
	"db branch list":     {0},
	"db branch delete":   {1},
	"db branch url":      {1},
	"db info":            {1},
	"db export":          {1},
	"db url":             {1},
	"db import":          {2},
	"db placement":       {1},
	"db move-home":       {2},
	"db regions":         {1},
	"backup list":        {1},
	"backup create":      {1},
	"backup restore":     {3},
	"sql":                {2},
	"functions deploy":   {2},
	"functions list":     {1},
	"functions invoke":   {2},
	"functions logs":     {2},
	"functions delete":   {2},
	"schema pull":        {0},
	"schema diff":        {1},
	"schema apply":       {1},
	"migrate new":        {1},
	"migrate up":         {1},
	"migrate status":     {1},
	"migrate diff":       {1, 2},
	"keys list":          {0},
	"keys create":        {1, 2},
	"keys revoke":        {1},
	"keys rotate":        {2},
}

func run(ctx context.Context, args []string, out io.Writer) error {
	return runWithInput(ctx, args, out, os.Stdin, os.Stderr, stdinTerminal())
}

func runWithInput(ctx context.Context, args []string, out io.Writer, in io.Reader, prompt io.Writer, tty bool) error {
	args, structured, err := jsonArguments(args)
	if !structured {
		return runCommand(ctx, args, out, in, prompt, tty)
	}
	w := &jsonOutput{Writer: out, progress: prompt}
	if err == nil {
		err = runCommand(ctx, args, w, in, prompt, tty)
	}
	if err == nil {
		err = finishJSONStream(w)
	}
	if err != nil {
		if !w.failed {
			err = errors.Join(err, emitJSONError(w, err))
		}
		return &jsonFailure{err}
	}
	return nil
}

func runCommand(ctx context.Context, args []string, out io.Writer, in io.Reader, prompt io.Writer, tty bool) error {
	if len(args) == 1 && (args[0] == "version" || args[0] == "--version") {
		if jsonMode(out) {
			return emitJSON(out, struct {
				Version string `json:"version"`
			}{cliVersion()})
		}
		_, err := fmt.Fprintln(out, "ts", cliVersion())
		return err
	}
	if handled, err := requestedHelp(args, out); handled {
		return err
	}
	fs := flag.NewFlagSet("ts", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	project := fs.String("project", "", "project name or id")
	yes := fs.Bool("yes", false, "consent to destructive operations or import preview")
	org := fs.String("org", "", "organization name or id")
	expires := fs.String("expires", "", "keys create: 30d, 90d, 365d or never")
	allowCIDR := fs.String("allow-cidr", "", "keys create: comma-separated CIDRs")
	scope := fs.String("scope", "", "keys create: read or read-write")
	homeRegion := fs.String("home-region", "", "db create: requested home region")
	replicaRegions := fs.String("replica-regions", "", "db create, db placement: comma-separated replica regions")
	allRegions := fs.Bool("all-regions", false, "db create, db placement: a replica in every serving region")
	expectedID := fs.String("expected-id", "", "db branch/delete: require the server to match this immutable database ULID")
	from := fs.String("from", "", "db branch create: source database")
	ttl := fs.String("ttl", "", "db branch create: lifetime, default 7d")
	schemaDB := fs.String("database", "", "schema: database name or ID; @ uses saved context")
	dryRun := fs.Bool("dry-run", false, "schema apply: print DDL without executing")
	exportFile := fs.String("o", "", "db export: write the SQL dump to a file")
	force := fs.Bool("force", false, "db export: overwrite the output file")
	allowDrop := fs.Bool("allow-drop", false, "schema apply: consent to DROP operations")
	args, err := parseOptions(fs, args)
	if err != nil {
		return err
	}
	name, operands := "", args
	for n := min(3, len(args)); n > 0 && name == ""; n-- {
		if _, ok := commands[strings.Join(args[:n], " ")]; ok {
			name, operands = strings.Join(args[:n], " "), args[n:]
		}
	}
	if name == "shell" {
		beginJSONStream(out)
	}
	var fn functionFlags
	if strings.HasPrefix(name, "functions ") {
		var err error
		if operands, err = fn.parse(name, operands); err != nil {
			return err
		}
	}
	if !slices.Contains(commands[name], len(operands)) {
		if name == "" {
			input := strings.Join(args, " ")
			if suggestion := closestCommand(input); suggestion != "" {
				return fmt.Errorf("unknown command %q, did you mean %q?: %w", input, suggestion, errUsage)
			}
			return fmt.Errorf("unknown command %q: %w", input, errUsage)
		}
		return fmt.Errorf("expected %s: %w", helpSyntax(name), errUsage)
	}
	var schemaFlagsSet, schemaApplyFlagsSet bool
	fs.Visit(func(f *flag.Flag) {
		if f.Name == "database" {
			schemaFlagsSet = true
		}
		if f.Name == "dry-run" || f.Name == "allow-drop" {
			schemaApplyFlagsSet = true
		}
	})
	if schemaFlagsSet && !strings.HasPrefix(name, "schema ") || schemaApplyFlagsSet && name != "schema apply" {
		return fmt.Errorf("schema options require their schema command: %w", errUsage)
	}
	if strings.HasPrefix(name, "schema ") && *schemaDB == "" {
		*schemaDB = "@"
	}
	var rotationID string
	var rotationGrace time.Duration
	if name == "keys rotate" {
		var err error
		rotationID, rotationGrace, err = parseKeyRotation(operands)
		if err != nil {
			return err
		}
	}
	var yesSet, scopeSet, keyOptionsSet, placementSet, replicasSet, allRegionsSet, expectedIDSet bool
	fs.Visit(func(f *flag.Flag) {
		switch f.Name {
		case "expected-id":
			expectedIDSet = true
		case "expires", "allow-cidr":
			keyOptionsSet = true
		case "home-region":
			placementSet = true
		case "replica-regions":
			replicasSet = true
		case "all-regions":
			allRegionsSet = true
		}
		if f.Name == "scope" {
			scopeSet = true
		}
		if f.Name == "yes" {
			yesSet = true
		}
	})
	if expectedIDSet && ((name != "db delete" && name != "db branch" && name != "db branch create" && name != "db branch delete" && name != "db branch url") || entityid.Check(*expectedID) != nil) {
		return fmt.Errorf("-expected-id requires branch creation, URL, or deletion and a canonical database ULID: %w", errUsage)
	}
	branchTTL, err := validateBranchOptions(name, operands, *from, *ttl, fs)
	if err != nil {
		return err
	}
	if name == "db branch" && operands[0] == operands[1] {
		return fmt.Errorf("branch source and target names must differ: %w", errUsage)
	}
	if placementSet && (name != "db create" || !validRegion(*homeRegion)) {
		return fmt.Errorf("-home-region needs db create and a region name: %w", errUsage)
	}
	placement, err := placementOptions(name, operands, replicasSet, *replicaRegions, allRegionsSet && *allRegions, allRegionsSet)
	if err != nil {
		return err
	}
	if scopeSet && name != "keys create" {
		return fmt.Errorf("-scope is only accepted for keys create: %w", errUsage)
	}
	if keyOptionsSet && name != "keys create" {
		return fmt.Errorf("key expiry and network options are only accepted for keys create: %w", errUsage)
	}
	if strings.HasPrefix(name, "backup ") {
		if err := validateBackupSettings(name, operands[1:]); err != nil {
			return err
		}
	}
	if name == "backup destination" {
		if !slices.Contains([]string{"get", "set", "delete"}, operands[0]) {
			return fmt.Errorf("backup destination operation must be get, set or delete: %w", errUsage)
		}
		if operands[0] == "get" && yesSet {
			return fmt.Errorf("backup destination get does not accept -yes: %w", errUsage)
		}
		if operands[0] == "set" && !*yes || operands[0] == "delete" && !*yes && !tty {
			return consentRequired("backup destination changes require explicit consent: pass -yes; set reads its JSON only from stdin")
		}
	}
	if yesSet && !slices.Contains([]string{"db delete", "db branch delete", "orgs delete", "projects delete", "backup destination", "backup rewind", "functions delete", "keys revoke"}, name) {
		return fmt.Errorf("-yes is only accepted for destructive commands: %w", errUsage)
	}
	if name == "backup rewind" && !*yes {
		return consentRequired("backup rewind discards every change after its time: pass -yes to explicitly consent")
	}
	if (name == "orgs delete" || name == "projects delete") && !tty && !*yes {
		return consentRequired("resource deletion requires a terminal: pass -yes to explicitly consent")
	}
	if (name == "db delete" || name == "db branch delete") && !tty && !*yes {
		if err := confirmDelete(operands[0], *project, *yes, in, prompt, tty); err != nil {
			return err
		}
	}
	if name == "functions delete" && !tty && !*yes {
		return consentRequired("resource deletion requires a terminal: pass -yes to explicitly consent")
	}
	if name == "keys revoke" {
		if entityid.Check(operands[0]) != nil {
			return &cliError{code: "invalid_arguments", message: "key id must be a canonical ULID; run ts keys list", err: errUsage}
		}
		if !tty && !*yes {
			return consentRequired("key revocation requires a terminal: pass -yes to explicitly consent")
		}
	}
	switch name {
	case "billing":
		return showBilling(out)
	case "context show", "context clear":
		path, err := contextPath()
		if err != nil {
			return err
		}
		if name == "context show" {
			return showCLIContext(path, out)
		}
		return clearCLIContext(path, out)
	case "completion":
		return completion(operands[0], out)
	case "migrate new":
		return newMigration(operands[0], out)
	}
	dispatch := func(s *session) error {
		if strings.HasPrefix(name, "db branch ") || name == "db url" {
			if name == "db branch delete" {
				if err := confirmDelete(operands[0], s.project, *yes, in, prompt, tty); err != nil {
					return err
				}
			}
			return runBranch(ctx, s, name, operands, *from, branchTTL, *expectedID, out)
		}
		if strings.HasPrefix(name, "schema ") {
			path := ""
			if len(operands) > 0 {
				path = operands[0]
			}
			return runSchema(ctx, s.tb.Database(s.project, *schemaDB), name, path, schemaOptions{dryRun: *dryRun, allowDrop: *allowDrop}, out)
		}
		if expectedIDSet {
			database := s.tb.Database(s.project, operands[0])
			if name == "db branch" {
				return branchExpectedDatabase(ctx, database, operands[1], *expectedID, out)
			}
			return deleteExpectedDatabase(ctx, database, *expectedID, out)
		}
		if name == "backup destination" {
			action := "Replace backup destination; start a new generation"
			if operands[0] == "delete" {
				action = "Remove backup destination; keep customer objects and resume platform shipping"
			}
			return backupDestination(ctx, s.tb, s.project, operands[0], in, func(_, label, id string) error {
				return confirmManagementTarget(action, label, id, *yes, in, prompt, tty)
			}, out)
		}
		switch {
		case name == "db create" && (placementSet || replicasSet || allRegionsSet):
			return createPlaced(ctx, s.tb, s.project, operands[0], *homeRegion, placement, out)
		case name == "db placement":
			return setPlacement(ctx, s.tb, s.project, operands[0], placement, out)
		case name == "db move-home":
			return moveHome(ctx, s.tb, s.project, operands[0], operands[1], out, prompt)
		}
		return s.dispatch(ctx, name, fn, operands, commandInput{in: in, prompt: prompt, tty: tty, yes: *yes}, out)
	}
	if key := os.Getenv("TERRASCALE_API_KEY"); key != "" {
		if strings.HasPrefix(name, "auth ") || strings.HasPrefix(name, "keys ") || strings.HasPrefix(name, "projects") || name == "whoami" || strings.HasPrefix(name, "orgs") || name == "context set" {
			return fmt.Errorf("%s needs ts auth login first: unset TERRASCALE_API_KEY and run ts auth login", name)
		}
		if strings.HasPrefix(name, "schema ") && *schemaDB == "@" {
			return fmt.Errorf("API key schema commands need --database <name|id>: %w", errUsage)
		}
		if _, err := contextDatabase(operands, ""); err != nil {
			return err
		}
		s, err := openKey(ctx, key, *project)
		if err != nil {
			return err
		}
		if name == "db delete" {
			if err := confirmDelete(operands[0], s.project, *yes, in, prompt, tty); err != nil {
				return err
			}
		}
		return dispatch(s)
	}
	var keyReq tsauth.APIKeyRequest
	if name == "keys create" {
		var err error
		if keyReq, err = scopedKeyRequest(operands, *expires, *allowCIDR, *scope); err != nil {
			return err
		}
	} else if *expires != "" || *allowCIDR != "" {
		return errUsage
	}
	device, err := tsauth.NewDevice(tsauth.DeviceConfig{})
	if err != nil {
		return err
	}
	switch name {
	case "auth login":
		return login(ctx, device, out, prompt)
	case "auth logout":
		return logout(ctx, device, out)
	}
	token, err := accessToken(ctx, device)
	if err != nil {
		return err
	}
	auth, err := tsauth.New(tsauth.Config{})
	if err != nil {
		return err
	}
	u := auth.WithToken(token)
	consent := func(kind, label, id string) error {
		return confirmManagementTarget("Delete "+kind, label, id, *yes, in, prompt, tty)
	}
	if name == "context set" {
		return selectCLIContext(ctx, u, token, operands, out)
	}
	if name != "whoami" && !strings.HasPrefix(name, "orgs") && !strings.HasPrefix(name, "projects") {
		path, err := contextPath()
		if err != nil {
			return err
		}
		c, err := readCLIContext(path)
		if err != nil {
			return err
		}
		var claims struct {
			Sub string `json:"sub"`
		}
		decodeClaims(token, &claims)
		var database string
		*org, *project, database, err = contextDefaults(c, claims.Sub, *org, *project)
		if err != nil {
			return err
		}
		if strings.HasPrefix(name, "schema ") && *schemaDB == "@" {
			selected, err := contextDatabase([]string{"@"}, database)
			if err != nil {
				return err
			}
			*schemaDB = selected[0]
		}
		if usesDatabaseOperand(name) {
			operands, err = contextDatabase(operands, database)
			if err != nil {
				return err
			}
		}
	}
	switch name {
	case "whoami":
		return whoami(ctx, u, token, out)
	case "orgs":
		return listOrganizations(ctx, u, out)
	case "orgs create", "orgs info", "orgs rename":
		return organizationLifecycle(ctx, u, name, operands, out)
	case "orgs delete":
		return deleteOrganizationLifecycle(ctx, u, operands[0], consent, out)
	case "projects":
		return listProjects(ctx, u, *org, out)
	case "projects info", "projects rename":
		return projectLifecycle(ctx, u, *org, name, operands, out)
	case "projects delete":
		p, err := selectProject(ctx, u, *org, operands[0])
		if err != nil {
			return err
		}
		selected, err := open(ctx, u, token, p.OrganizationID, p.ID)
		if err != nil {
			return err
		}
		return deleteProjectLifecycle(ctx, u, selected.tb, p, consent, out)
	case "projects create":
		return createProject(ctx, u, *org, operands[0], out)
	}
	s, err := open(ctx, u, token, *org, *project)
	if err != nil {
		return err
	}
	if name == "keys create" {
		return createKey(ctx, s, keyReq, out, prompt)
	}
	if name == "keys rotate" {
		return rotateKey(ctx, s, rotationID, rotationGrace, out, prompt)
	}
	if name == "db export" {
		return dbExport(ctx, s, operands[0], *exportFile, *force, out)
	}
	if name == "db import" {
		start, err := parseImportFrom(*from)
		if err != nil {
			return err
		}
		return dbImport(ctx, s.tb.Database(s.project, operands[0]), operands[1], start, in, out, prompt)
	}
	if name == "db delete" {
		if err := confirmDelete(operands[0], s.project, *yes, in, prompt, tty); err != nil {
			return err
		}
	}
	return dispatch(s)
}

// dispatch runs a project command.
func (s *session) dispatch(ctx context.Context, name string, fn functionFlags, operands []string, input commandInput, out io.Writer) error {
	if name == "shell" {
		return sqlShell(ctx, s.tb.Database(s.project, operands[0]), input, out)
	}
	if name == "sql" {
		return runSQL(ctx, s.tb.Database(s.project, operands[0]), operands[1], input.in, out)
	}
	if name == "functions delete" {
		if err := confirmResourceDelete("function", operands[0], operands[1], s.project, input.yes, input.in, input.prompt, input.tty); err != nil {
			return err
		}
	}
	if strings.HasPrefix(name, "functions ") {
		return functions(ctx, s.tb.Database(s.project, operands[0]), name, fn, operands[1:], out)
	}
	if name == "keys revoke" {
		message := fmt.Sprintf("Revoke API key %s in project %s; clients using it stop authenticating.", operands[0], s.project)
		if err := confirmNamedOperation(operands[0], message, name, input.yes, input.in, input.prompt, input.tty); err != nil {
			return err
		}
	}
	return s.run(ctx, name, operands, out)
}

// keyLifetimeDays maps the -expires values that set a key's lifetime.
var keyLifetimeDays = map[string]int{"30d": 30, "90d": 90, "365d": 365}

// newKeyRequest reads keys create's operands, <name> and an optional database ULID,
// and its -expires and -allow-cidr flags. A key expires never unless asked.
func newKeyRequest(operands []string, expires, allowCIDR string) (tsauth.APIKeyRequest, error) {
	req := tsauth.APIKeyRequest{Name: operands[0], Scopes: []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDatabasesWrite,
		tsauth.ScopeSQLRead, tsauth.ScopeSQLWrite}}
	if len(operands) == 2 {
		if entityid.Check(operands[1]) != nil {
			return req, fmt.Errorf("database restriction must be a canonical ULID: %w", errUsage)
		}
		req.Database = operands[1]
	}
	if days, ok := keyLifetimeDays[expires]; ok {
		at := time.Now().Add(time.Duration(days) * 24 * time.Hour)
		req.ExpiresAt = &at
	} else if expires != "" && expires != "never" {
		return req, fmt.Errorf("-expires must be 30d, 90d, 365d or never: %w", errUsage)
	}
	if allowCIDR != "" {
		for _, c := range strings.Split(allowCIDR, ",") {
			if c = strings.TrimSpace(c); c != "" {
				req.AllowedCIDRs = append(req.AllowedCIDRs, c)
			}
		}
		if req.AllowedCIDRs == nil {
			return req, fmt.Errorf("-allow-cidr needs at least one CIDR: %w", errUsage)
		}
	}
	return req, nil
}

// createKey creates a key and prints its secret once, with its expiry and
// allowed networks on stderr.
func createKey(ctx context.Context, s *session, req tsauth.APIKeyRequest, out, prompt io.Writer) error {
	k, err := stepUp(ctx, s, tsauth.StepUpCreateAPIKey, "Creating API keys", func(u *tsauth.User) (*tsauth.CreatedAPIKey, error) {
		return u.CreateAPIKey(ctx, s.org, s.project, req)
	}, prompt)
	if err != nil {
		return err
	}
	if jsonMode(out) {
		return keyOutputError(emitJSON(out, k)) // This explicit create command reveals the secret once.
	}
	expires := "never"
	if k.APIKey.ExpiresAt != nil {
		expires = k.APIKey.ExpiresAt.Format(time.RFC3339)
	}
	networks := "any address"
	if len(k.APIKey.AllowedCIDRs) > 0 {
		networks = strings.Join(k.APIKey.AllowedCIDRs, ",")
	}
	if _, err := fmt.Fprintf(os.Stderr, "Created key %s (%s). Expires: %s. Allowed networks: %s.\nStore it now, it is not shown again:\n", k.APIKey.Name, k.APIKey.ID, expires, networks); err != nil {
		return keyOutputError(err)
	}
	_, err = fmt.Fprintln(out, k.Key)
	return keyOutputError(err)
}

func login(ctx context.Context, d *tsauth.Device, out, prompt io.Writer) error {
	a, err := d.Start(ctx)
	if err != nil {
		return err
	}
	progress := out
	if jsonMode(out) {
		progress = prompt
	}
	if _, err := fmt.Fprintf(progress, "Open %s\nand confirm the code %s\n", a.VerificationURIComplete, a.UserCode); err != nil {
		return err
	}
	t, err := d.Wait(ctx, a)
	if err != nil {
		return err
	}
	if err := save(t); err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			SignedIn bool `json:"signedIn"`
		}{true})
	}
	fmt.Fprintln(out, "Signed in.")
	return nil
}

func logout(ctx context.Context, d *tsauth.Device, out io.Writer) error {
	path, err := credentialsPath()
	if err != nil {
		return err
	}
	var c credentials
	if data, err := os.ReadFile(path); err == nil && json.Unmarshal(data, &c) == nil && c.RefreshToken != "" {
		if err := d.Revoke(ctx, c.RefreshToken); err != nil {
			return err
		}
	}
	if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			SignedIn bool `json:"signedIn"`
		}{false})
	}
	fmt.Fprintln(out, "Signed out.")
	return nil
}

// accessToken returns a current access token, refreshing the stored sign-in
// when it expires within a minute.
func accessToken(ctx context.Context, d *tsauth.Device) (string, error) {
	path, err := credentialsPath()
	if err != nil {
		return "", err
	}
	data, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return "", &cliError{code: "not_signed_in", message: "not signed in: run ts auth login"}
	}
	var c credentials
	if err != nil || json.Unmarshal(data, &c) != nil {
		return "", &cliError{code: "not_signed_in", message: "unreadable " + path + ": run ts auth login"}
	}
	if time.Until(c.Expiry) > time.Minute {
		return c.AccessToken, nil
	}
	t, err := d.Refresh(ctx, c.RefreshToken)
	var refused *tsauth.Error
	if errors.As(err, &refused) && refused.Code == "invalid_grant" {
		return "", &cliError{code: "sign_in_expired", message: "sign-in expired, run ts auth login", err: err}
	}
	if err != nil {
		// The sign-in is kept: an outage or lost connection is not an expiry.
		return "", fmt.Errorf("could not refresh the sign-in, try again: %w", err)
	}
	t.RefreshToken = cmp.Or(t.RefreshToken, c.RefreshToken) // ts-auth may keep the old one
	return t.AccessToken, save(t)
}

// projects yields every project the user reaches, in every organization.
func projects(ctx context.Context, u *tsauth.User, organization string) iter.Seq2[tsauth.Project, error] {
	return func(yield func(tsauth.Project, error) bool) {
		if organization != "" {
			selected, err := selectOrganization(ctx, u, organization)
			if err != nil {
				yield(tsauth.Project{}, err)
				return
			}
			organization = selected.ID
		}
		for org, err := range pages(func(after string) (*v1.Page[tsauth.Organization], error) {
			p, err := u.Organizations(ctx, tsauth.PageOptions{Limit: 100, After: after})
			return (*v1.Page[tsauth.Organization])(p), err
		}) {
			if err != nil {
				yield(tsauth.Project{}, err)
				return
			}
			if organization != "" && org.ID != organization {
				continue
			}
			for p, err := range pages(func(after string) (*v1.Page[tsauth.Project], error) {
				p, err := u.Projects(ctx, org.ID, tsauth.PageOptions{Limit: 100, After: after})
				return (*v1.Page[tsauth.Project])(p), err
			}) {
				if !yield(p, err) || err != nil {
					return
				}
			}
		}
	}
}

func listProjects(ctx context.Context, u *tsauth.User, organization string, out io.Writer) error {
	items := []tsauth.Project{}
	w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
	if !jsonMode(out) {
		fmt.Fprintln(w, "NAME\tID\tORGANIZATION\tROLE")
	}
	for p, err := range projects(ctx, u, organization) {
		if err != nil {
			return err
		}
		if jsonMode(out) {
			items = append(items, p)
			continue
		}
		fmt.Fprintf(w, "%s\t%s\t%s\t%s\n", p.Name, p.ID, p.OrganizationID, p.Role)
	}
	if jsonMode(out) {
		return emitJSON(out, items)
	}
	return w.Flush()
}

// whoami prints the account the stored sign-in names, after ts-auth accepts
// the token.
func whoami(ctx context.Context, u *tsauth.User, token string, out io.Writer) error {
	if _, err := u.Organizations(ctx, tsauth.PageOptions{Limit: 1}); err != nil {
		return err
	}
	var claims struct {
		Sub   string `json:"sub"`
		Email string `json:"email"`
	}
	decodeClaims(token, &claims)
	if jsonMode(out) {
		return emitJSON(out, claims)
	}
	_, err := fmt.Fprintln(out, cmp.Or(claims.Email, claims.Sub, "signed in"))
	return err
}

// decodeClaims reads a JWT's payload into v without verifying it: callers
// only label output or route requests with it; the server checks the token.
func decodeClaims(token string, v any) {
	if parts := strings.Split(token, "."); len(parts) == 3 {
		if payload, err := base64.RawURLEncoding.DecodeString(parts[1]); err == nil {
			_ = json.Unmarshal(payload, v)
		}
	}
}

// openKey opens the project an API key acts in: its own, or project for an
// organization key.
func openKey(ctx context.Context, key, project string) (*session, error) {
	tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{Key: key, Project: project})
	if err != nil {
		return nil, fmt.Errorf("TERRASCALE_API_KEY: %w", err)
	}
	token, err := tokens.Token(ctx)
	if err != nil {
		return nil, fmt.Errorf("TERRASCALE_API_KEY: %w", err)
	}
	var claims struct {
		ProjectID string `json:"project_id"`
	}
	decodeClaims(token, &claims)
	if project = cmp.Or(project, claims.ProjectID); project == "" {
		return nil, errors.New("TERRASCALE_API_KEY is an organization key: pass -project <id>")
	}
	tb, err := v1.New(v1.Config{APIKey: key})
	if err != nil {
		return nil, err
	}
	return &session{tb: tb, project: project}, nil
}

// session is the signed-in user's TerraBase client for one project.
type session struct {
	tb      *v1.Client
	user    *tsauth.User
	org     string
	project string
}

func open(ctx context.Context, u *tsauth.User, token, organization, project string) (*session, error) {
	var matches []tsauth.Project
	for p, err := range projects(ctx, u, organization) {
		if err != nil {
			return nil, err
		}
		if project == "" || p.Name == project || p.ID == project {
			matches = append(matches, p)
		}
	}
	switch {
	case len(matches) == 0 && project != "":
		return nil, fmt.Errorf("no project %q: run ts projects", project)
	case len(matches) == 0:
		return nil, errors.New("no project: run ts projects create <name> (ts orgs lists organizations)")
	case len(matches) > 1:
		return nil, fmt.Errorf("%d projects match: pick one with -project <id> (ts projects lists them)", len(matches))
	}
	p := matches[0]
	// Empty Scopes: ts-auth grants what the user's project role allows.
	tokens, err := tsauth.NewExchangeSource(tsauth.ExchangeConfig{ClientID: tsauth.CLIClientID,
		SubjectToken: func(context.Context) (string, error) { return token, nil }, Organization: p.OrganizationID, Project: p.ID})
	if err != nil {
		return nil, err
	}
	tb, err := v1.New(v1.Config{TokenSource: tokens})
	if err != nil {
		return nil, err
	}
	return &session{tb, u, p.OrganizationID, p.ID}, nil
}

// pages yields every item of a paged list; list gets the previous Next.
func pages[T any](list func(after string) (*v1.Page[T], error)) iter.Seq2[T, error] {
	return func(yield func(T, error) bool) {
		after := ""
		for {
			p, err := list(after)
			if err != nil {
				var zero T
				yield(zero, err)
				return
			}
			for _, item := range p.Items {
				if !yield(item, nil) {
					return
				}
			}
			if after = p.Next; after == "" {
				return
			}
		}
	}
}

// projectDatabases yields a project's databases page by page. TerraBase learns
// a project at its first database, so an unknown database means none exist yet.
func projectDatabases(ctx context.Context, tb *v1.Client, project string, limit int) iter.Seq2[v1.ManagedDatabase, error] {
	return func(yield func(v1.ManagedDatabase, error) bool) {
		var empty bool
		for db, err := range pages(func(after string) (*v1.Page[v1.ManagedDatabase], error) {
			page, err := tb.Databases(ctx, project, v1.PageOptions{Limit: limit, After: after})
			if errors.Is(err, v1.ErrUnknownDatabase) {
				empty = true
			}
			return page, err
		}) {
			if empty {
				return
			}
			if err != nil {
				yield(db, err)
				return
			}
			if !yield(db, nil) {
				return
			}
		}
	}
}

func (s *session) run(ctx context.Context, name string, args []string, out io.Writer) error {
	var db *v1.Database
	if len(args) > 0 {
		db = s.tb.Database(s.project, args[0])
	}
	switch name {
	case "db list":
		items := []v1.ManagedDatabase{}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		if !jsonMode(out) {
			fmt.Fprintln(w, "NAME\tID\tREGION")
		}
		for db, err := range projectDatabases(ctx, s.tb, s.project, 100) {
			if err != nil {
				return err
			}
			if jsonMode(out) {
				items = append(items, db)
				continue
			}
			home := ""
			if db.HomeRegion != nil {
				home = *db.HomeRegion
			}
			fmt.Fprintf(w, "%s\t%s\t%s\n", db.Name, db.ID, home)
		}
		if jsonMode(out) {
			return emitJSON(out, items)
		}
		return w.Flush()
	case "db create":
		m, err := db.Create(ctx)
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, m)
		}
		fmt.Fprintf(out, "Created %s (%s)\n", args[0], m.ID)
		return nil
	case "db delete":
		if err := db.Delete(ctx); err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				Name    string `json:"name"`
				Deleted bool   `json:"deleted"`
			}{args[0], true})
		}
		fmt.Fprintf(out, "Deleted %s\n", args[0])
		return nil
	case "db branch":
		m, err := db.Branch(ctx, args[1])
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, m)
		}
		fmt.Fprintf(out, "Branched %s into %s (%s)\n", args[0], m.Name, m.ID)
		return nil
	case "db info":
		m, err := db.Info(ctx)
		if err != nil {
			return err
		}
		return emitJSON(out, m)
	case "db regions":
		r, err := db.Regions(ctx)
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, r)
		}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "REGION\tROLE\tLAG MS\tLAST PULL\tLAST VERIFIED\tREVISION")
		fmt.Fprintf(w, "%s\thome\t\t\t\t\n", r.HomeRegion)
		for _, rep := range r.Replicas {
			lag, pull, verified, revision := "-", "-", "-", "-"
			if rep.LagMs != nil {
				lag = fmt.Sprintf("%.0f", *rep.LagMs)
			}
			if rep.LastPulledAt != nil {
				pull = rep.LastPulledAt.Format(time.RFC3339)
			}
			if rep.LastVerified != nil {
				verified = rep.LastVerified.Format(time.RFC3339)
			}
			if rep.Revision != nil {
				revision = *rep.Revision
			}
			fmt.Fprintf(w, "%s\treplica\t%s\t%s\t%s\t%s\n", rep.Region, lag, pull, verified, revision)
		}
		return w.Flush()
	case "db rename", "databases rename":
		id := args[0]
		if v1.ValidateID(id) != nil {
			m, err := db.Info(ctx)
			if err != nil {
				return err
			}
			id = m.ID
		}
		m, err := s.tb.RenameDatabase(ctx, s.project, id, args[1])
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, m)
		}
		_, err = fmt.Fprintf(out, "Renamed %s to %s (%s)\n", args[0], m.Name, m.ID)
		return err
	case "backup list":
		items := []v1.DatabaseBackup{}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		if !jsonMode(out) {
			fmt.Fprintln(w, "ID\tSTATUS\tTRIGGER\tBYTES\tCREATED")
		}
		for b, err := range pages(func(after string) (*v1.Page[v1.DatabaseBackup], error) {
			return db.Backups(ctx, v1.PageOptions{Limit: 100, After: after})
		}) {
			if err != nil {
				return err
			}
			if jsonMode(out) {
				items = append(items, b)
				continue
			}
			fmt.Fprintf(w, "%s\t%s\t%s\t%d\t%s\n", b.ID, b.Status, b.Trigger, b.SizeBytes, b.CreatedAt.Format(time.RFC3339))
		}
		if jsonMode(out) {
			return emitJSON(out, items)
		}
		return w.Flush()
	case "backup create":
		b, err := db.CreateBackup(ctx)
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, b)
		}
		fmt.Fprintf(out, "Backup %s %s\n", b.ID, b.Status)
		return nil
	case "backup restore":
		m, err := db.RestoreBackup(ctx, args[1], args[2])
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, m)
		}
		fmt.Fprintf(out, "Restored %s into %s (%s)\n", args[1], m.Name, m.ID)
		return nil
	case "backup policy", "backup policy-set", "backup window", "backup plan", "backup restore-at", "backup rewind":
		return backupSettings(ctx, db, name, args[1:], out)
	case "migrate up":
		return migrateUp(ctx, db, out)
	case "migrate status":
		return migrateStatus(ctx, db, out)
	case "migrate diff":
		return s.migrateDiff(ctx, args[0], strings.Join(args[1:], ""), out)
	case "usage":
		var u *v1.Usage
		var err error
		if db != nil {
			u, err = db.Usage(ctx, v1.UsageOptions{})
		} else {
			u, err = s.tb.ProjectUsage(ctx, s.project, v1.UsageOptions{})
		}
		if err != nil {
			return err
		}
		return emitJSON(out, u)
	case "keys list":
		return listKeyMetadata(ctx, s, out)
	case "keys revoke":
		if err := s.user.RevokeAPIKey(ctx, s.org, s.project, args[0]); err != nil {
			return keyOperationError(err, "Revoking API keys", s)
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				ID      string `json:"id"`
				Revoked bool   `json:"revoked"`
			}{args[0], true})
		}
		fmt.Fprintf(out, "Revoked %s\n", args[0])
		return nil
	}
	return errUsage
}
