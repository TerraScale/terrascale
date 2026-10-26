// Command ts is the TerraScale CLI: device sign-in, projects, databases,
// collections, documents, SQL, functions and API keys against the production API, as
// the signed-in user. Run ts help for every command.
//
// Project commands act in the user's only project; -project <name or id>
// picks one when the user reaches several, and -org <name or id> restricts
// project selection to one unambiguous organization.
package main

import (
	"bufio"
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

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

var version = "dev" // Set by release builds with -ldflags.

const usage = `usage: ts [--json] [-project <name or id>] [-org <name or id>] [-yes] <command>

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
  projects                                list the projects you can reach
  projects info <project>                 inspect a project; -org restricts lookup
  projects rename <project> <name>        rename a project (admin)
  projects create <name>                  create a project (and an organization
                                          when you have none)
  usage [<db>]                            print the project's or a database's
                                          usage as JSON
  completion bash | zsh | fish            print a shell completion script

  db list | create <db> | delete <db>     manage the project's databases
                                          delete requires typing the database
                                          name; -yes before db delete consents
                                          explicitly for unattended use
  db branch <source> <db>                 copy a database into a new one
  db info <db>                            print a database's metadata as JSON
  db regions <db> <region>... | none      set the replica regions (gru, sjc,
                                          fra) besides the home region, or
                                          remove every replica
  backup list | create <db>               list or take a database's backups
  backup policy <db>                      read the snapshot schedule and retention
  backup policy-set <db> <cron|none> <days> set five-field UTC cron and 1..35 days
  backup window <db> [platform|customer]  inspect the proven restore interval
  backup plan <db> <time> [platform|customer] preview restore metadata
  backup restore <db> <backup id> <new db> restore a backup into a new database
  backup restore-at <db> <time> <new db> [platform|customer]
                                          restore an RFC3339 instant (default platform)
  export <db>                             stream all live documents (NDJSON);
                                          excludes relational tables and metadata
  db changes <db> [<collection>...]       print committed changes as JSON lines
                                          until interrupted
  collection list <db>                    list a database's collections
  collection create | drop <db> <name>    create or drop a collection
  doc list <db> <collection>              print every document as JSON lines
  doc get <db> <collection> <id>          print one document
  doc put <db> <collection> [<id>] <json> write a document; - reads stdin,
                                          no id creates one with a new id
  doc delete <db> <collection> <id>       delete a document
  doc import <db> <collection>            put the JSON lines doc list prints,
                                          read from stdin, creating the collection
  schema get | delete <db> <collection>   print or remove a collection's schema
  schema set <db> <collection> <json>     declare a JSON Schema; - reads stdin
  ttl get | delete <db> <collection>      print or remove a collection's TTL
  ttl set <db> <collection> <pointer>     expire documents whose member at the
                                          JSON Pointer (/expiresAt) is past
  index list <db> [<collection>]          list secondary indexes
  index create <db> <collection> <field>... index 1 to 4 dotted member paths
  index drop <db> <name>                  drop an index
  import <db> <format> <file>             import a postgres_plain, sqlite or
                                          ndjson file into a new database;
                                          for plain document NDJSON, pass
                                          -collection <name> before import
  import status | cancel <db>             print or cancel the database's import
  functions deploy <db> <file.js | dir>   deploy or replace a JavaScript function
                                          (a dir deploys its index.js); flags:
                                          -name <name> (default: file or dir
                                          name), -trigger-collection <name>,
                                          -events upsert,delete, -cron <expr>
  functions list <db>                     list the database's functions
  functions invoke <db> <name>            run a function and print its response;
                                          -data <json> sends a body, - stdin
  functions logs <db> <name>              print recent invocations as JSON
                                          lines, newest first; -limit 1..100
  functions delete <db> <name>            delete a function and its logs
  sql <db> <query>                        run a read-only SQL query on collections
  exec <db> <statement>                   run one relational SQL statement in its
                                          own transaction; rows print as JSON lines
  migrate new <name>                      add the next migrations/NNNN_<name>.sql
  migrate up | status <db>                apply pending migrations in order, each
                                          in one transaction recorded in the
                                          ts_migrations table, or list them
  migrate diff <db> [<base>]              print the tables, collections and indexes
                                          a database adds, drops or changes
                                          against base, by default the database
                                          it branched from
  keys list                               list the project's API keys
  keys create <name> [<db>]               create an API key, shown once;
                                          -scope read requests read-only grants;
                                          default scope is read-write;
                                          set its lifetime and networks with
                                          -expires 30d|90d|365d|never (default
                                          never) and -allow-cidr <cidr>,... (both
                                          go before the command)
  keys revoke <id>                        revoke an API key

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

// commands maps each command to its accepted operand counts.
var commands = map[string][]int{
	"auth login":        {0},
	"auth logout":       {0},
	"whoami":            {0},
	"billing":           {0},
	"projects":          {0},
	"orgs":              {0},
	"orgs create":       {1},
	"orgs info":         {1},
	"orgs rename":       {2},
	"projects info":     {1},
	"projects rename":   {2},
	"context show":      {0},
	"context clear":     {0},
	"context set":       {2, 3},
	"projects create":   {1},
	"usage":             {0, 1},
	"completion":        {1},
	"db changes":        {1, 2, 3, 4, 5},
	"export":            {1},
	"backup restore-at": {3, 4},
	"backup policy":     {1},
	"backup policy-set": {3},
	"backup window":     {1, 2},
	"backup plan":       {2, 3},
	"schema get":        {2},
	"schema set":        {3},
	"schema delete":     {2},
	"ttl get":           {2},
	"ttl set":           {3},
	"ttl delete":        {2},
	"index list":        {1, 2},
	"index create":      {3, 4, 5, 6},
	"index drop":        {2},
	"import":            {3},
	"import status":     {1},
	"import cancel":     {1},
	"db list":           {0},
	"db create":         {1},
	"db delete":         {1},
	"db branch":         {2},
	"db info":           {1},
	"db regions":        {2, 3, 4},
	"backup list":       {1},
	"backup create":     {1},
	"backup restore":    {3},
	"collection list":   {1},
	"collection create": {2},
	"collection drop":   {2},
	"doc list":          {2},
	"doc get":           {3},
	"doc put":           {3, 4},
	"doc delete":        {3},
	"doc import":        {2},
	"sql":               {2},
	"functions deploy":  {2},
	"functions list":    {1},
	"functions invoke":  {2},
	"functions logs":    {2},
	"functions delete":  {2},
	"exec":              {2},
	"migrate new":       {1},
	"migrate up":        {1},
	"migrate status":    {1},
	"migrate diff":      {1, 2},
	"keys list":         {0},
	"keys create":       {1, 2},
	"keys revoke":       {1},
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
	yes := fs.Bool("yes", false, "db delete: consent to permanent deletion")
	org := fs.String("org", "", "organization name or id")
	expires := fs.String("expires", "", "keys create: 30d, 90d, 365d or never")
	allowCIDR := fs.String("allow-cidr", "", "keys create: comma-separated CIDRs")
	scope := fs.String("scope", "", "keys create: read or read-write")
	collection := fs.String("collection", "", "ndjson import: destination collection")
	if err := fs.Parse(args); err != nil {
		return parserError(err)
	}
	args = fs.Args()
	name, operands := "", args
	for n := min(2, len(args)); n > 0 && name == ""; n-- {
		if _, ok := commands[strings.Join(args[:n], " ")]; ok {
			name, operands = strings.Join(args[:n], " "), args[n:]
		}
	}
	if slices.Contains([]string{"db changes", "doc list", "export"}, name) {
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
			return fmt.Errorf("unknown command; run ts help: %w", errUsage)
		}
		return fmt.Errorf("expected %s: %w", helpSyntax(name), errUsage)
	}
	var collectionSet, yesSet, scopeSet, keyOptionsSet bool
	fs.Visit(func(f *flag.Flag) {
		if f.Name == "expires" || f.Name == "allow-cidr" {
			keyOptionsSet = true
		}
		if f.Name == "scope" {
			scopeSet = true
		}
		if f.Name == "yes" {
			yesSet = true
		}
		if f.Name == "collection" {
			collectionSet = true
		}
	})
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
	if collectionSet && (name != "import" || operands[1] != "ndjson" || strings.TrimSpace(*collection) == "") {
		return fmt.Errorf("-collection needs a nonempty name and an ndjson import: %w", errUsage)
	}
	if yesSet && name != "db delete" {
		return fmt.Errorf("-yes is only accepted for db delete: %w", errUsage)
	}
	if name == "db delete" && !tty && !*yes {
		if err := confirmDelete(operands[0], *project, *yes, in, prompt, tty); err != nil {
			return err
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
	if key := os.Getenv("TERRASCALE_API_KEY"); key != "" {
		if strings.HasPrefix(name, "auth ") || strings.HasPrefix(name, "keys ") || strings.HasPrefix(name, "projects") || name == "whoami" || strings.HasPrefix(name, "orgs") || name == "context set" {
			return fmt.Errorf("%s needs ts auth login without TERRASCALE_API_KEY", name)
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
		return s.dispatch(ctx, name, fn, *collection, operands, out)
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
	case "projects":
		return listProjects(ctx, u, *org, out)
	case "projects info", "projects rename":
		return projectLifecycle(ctx, u, *org, name, operands, out)
	case "projects create":
		return createProject(ctx, u, *org, operands[0], out)
	}
	s, err := open(ctx, u, token, *org, *project)
	if err != nil {
		return err
	}
	if name == "keys create" {
		return createKey(ctx, s, keyReq, out)
	}
	if name == "db delete" {
		if err := confirmDelete(operands[0], s.project, *yes, in, prompt, tty); err != nil {
			return err
		}
	}
	return s.dispatch(ctx, name, fn, *collection, operands, out)
}

// dispatch runs a project command.
func (s *session) dispatch(ctx context.Context, name string, fn functionFlags, collection string, operands []string, out io.Writer) error {
	if strings.HasPrefix(name, "functions ") {
		return functions(ctx, s.tb.Database(s.project, operands[0]), name, fn, operands[1:], out)
	}
	if name == "import" {
		return importFile(ctx, s.tb.Database(s.project, operands[0]), operands[1], operands[2], collection, out)
	}
	return s.run(ctx, name, operands, out)
}

// keyLifetimeDays maps the -expires values that set a key's lifetime.
var keyLifetimeDays = map[string]int{"30d": 30, "90d": 90, "365d": 365}

// newKeyRequest reads keys create's operands, <name> and an optional <db>,
// and its -expires and -allow-cidr flags. A key expires never unless asked.
func newKeyRequest(operands []string, expires, allowCIDR string) (tsauth.APIKeyRequest, error) {
	req := tsauth.APIKeyRequest{Name: operands[0], Scopes: []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDatabasesWrite,
		tsauth.ScopeDocumentsRead, tsauth.ScopeDocumentsWrite}}
	if len(operands) == 2 {
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
func createKey(ctx context.Context, s *session, req tsauth.APIKeyRequest, out io.Writer) error {
	k, err := s.user.CreateAPIKey(ctx, s.org, s.project, req)
	if err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, k) // This explicit create command reveals the secret once.
	}
	expires := "never"
	if k.APIKey.ExpiresAt != nil {
		expires = k.APIKey.ExpiresAt.Format(time.RFC3339)
	}
	networks := "any address"
	if len(k.APIKey.AllowedCIDRs) > 0 {
		networks = strings.Join(k.APIKey.AllowedCIDRs, ",")
	}
	fmt.Fprintf(os.Stderr, "Created key %s (%s). Expires: %s. Allowed networks: %s.\nStore it now, it is not shown again:\n", k.APIKey.Name, k.APIKey.ID, expires, networks)
	fmt.Fprintln(out, k.Key)
	return nil
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
		return "", errors.New("not signed in: run ts auth login")
	}
	var c credentials
	if err != nil || json.Unmarshal(data, &c) != nil {
		return "", fmt.Errorf("unreadable %s: run ts auth login", path)
	}
	if time.Until(c.Expiry) > time.Minute {
		return c.AccessToken, nil
	}
	t, err := d.Refresh(ctx, c.RefreshToken)
	if err != nil {
		return "", fmt.Errorf("sign-in expired, run ts auth login: %w", err)
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
	tb, err := v1.New(v1.Config{TokenSource: tokens})
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

func (s *session) run(ctx context.Context, name string, args []string, out io.Writer) error {
	var db *v1.Database
	if len(args) > 0 {
		db = s.tb.Database(s.project, args[0])
	}
	switch name {
	case "sql":
		res, err := db.SQL(ctx, args[1])
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, res)
		}
		for _, row := range res.Rows {
			fmt.Fprintf(out, "%s\n", row.Value)
		}
		if res.Truncated {
			fmt.Fprintf(os.Stderr, "ts: truncated: %d rows matched\n", res.Matched)
		}
		return nil
	case "db list":
		items := []v1.ManagedDatabase{}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		if !jsonMode(out) {
			fmt.Fprintln(w, "NAME\tID\tHOME REGION\tREPLICAS")
		}
		for db, err := range pages(func(after string) (*v1.Page[v1.ManagedDatabase], error) {
			return s.tb.Databases(ctx, s.project, v1.PageOptions{Limit: 100, After: after})
		}) {
			// TerraBase learns a project at its first database; until then it has none.
			if errors.Is(err, v1.ErrUnknownDatabase) {
				break
			}
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
			replicas := strings.Join(db.ReplicaRegions, ",")
			if db.AllRegions {
				replicas = "all"
			}
			fmt.Fprintf(w, "%s\t%s\t%s\t%s\n", db.Name, db.ID, home, replicas)
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
		// TerraBase deletes only unreplicated databases, and new ones replicate.
		if _, err := db.SetRegions(ctx, []string{}); err != nil {
			return err
		}
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
		regions := args[1:]
		if slices.Equal(regions, []string{"none"}) {
			regions = nil
		}
		m, err := db.SetRegions(ctx, regions)
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, m)
		}
		fmt.Fprintf(out, "%s replicates to [%s]\n", args[0], strings.Join(m.ReplicaRegions, " "))
		return nil
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
	case "backup policy", "backup policy-set", "backup window", "backup plan", "backup restore-at":
		return backupSettings(ctx, db, name, args[1:], out)
	case "export":
		return exportDatabase(ctx, db, out)
	case "db changes":
		return tailChanges(ctx, db, args[1:], out)
	case "exec":
		res, err := db.ExecuteSQL(ctx, args[1])
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, res)
		}
		if len(res.Columns) == 0 {
			fmt.Fprintf(out, "%d rows affected\n", res.RowsAffected)
			return nil
		}
		enc := json.NewEncoder(out)
		for _, row := range res.Maps() {
			if err := enc.Encode(row); err != nil {
				return err
			}
		}
		return nil
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
	case "schema get", "schema set", "schema delete":
		col := db.Collection(args[1])
		var schema *v1.DeclaredCollectionSchema
		var err error
		switch name {
		case "schema get":
			schema, err = col.Schema(ctx)
		case "schema delete":
			schema, err = col.DeleteSchema(ctx)
		default:
			var body []byte
			if body, err = jsonOperand(args[2]); err == nil {
				schema, err = col.SetSchema(ctx, json.RawMessage(body))
			}
		}
		if err != nil {
			return err
		}
		return emitJSON(out, schema)
	case "ttl get":
		path, err := db.Collection(args[1]).TTL(ctx)
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				Path string `json:"path"`
			}{path})
		}
		_, err = fmt.Fprintln(out, cmp.Or(path, "none"))
		return err
	case "ttl set":
		if err := db.Collection(args[1]).SetTTL(ctx, args[2]); err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				Collection string `json:"collection"`
				Path       string `json:"path"`
			}{args[1], args[2]})
		}
		fmt.Fprintf(out, "%s expires documents at %s\n", args[1], args[2])
		return nil
	case "ttl delete":
		if err := db.Collection(args[1]).DeleteTTL(ctx); err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				Collection string `json:"collection"`
				Path       string `json:"path"`
			}{args[1], ""})
		}
		fmt.Fprintf(out, "%s no longer expires documents\n", args[1])
		return nil
	case "index list":
		var opts []v1.IndexListOptions
		if len(args) == 2 {
			opts = append(opts, v1.IndexListOptions{Collection: args[1]})
		}
		indexes, err := db.Indexes(ctx, opts...)
		if err != nil {
			return err
		}
		if jsonMode(out) {
			if indexes == nil {
				indexes = []v1.Index{}
			}
			return emitJSON(out, indexes)
		}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "NAME\tCOLLECTION\tFIELDS\tSTATE")
		for _, ix := range indexes {
			fmt.Fprintf(w, "%s\t%s\t%s\t%s\n", ix.Name, ix.Collection, strings.Join(ix.Fields, ","), ix.State)
		}
		return w.Flush()
	case "index create":
		ix, err := db.CreateIndex(ctx, v1.IndexRequest{Collection: args[1], Fields: args[2:]})
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, ix)
		}
		fmt.Fprintf(out, "Created index %s\n", ix.Name)
		return nil
	case "index drop":
		if err := db.DropIndex(ctx, args[1]); err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, struct {
				Name    string `json:"name"`
				Dropped bool   `json:"dropped"`
			}{args[1], true})
		}
		fmt.Fprintf(out, "Dropped index %s\n", args[1])
		return nil
	case "import status", "import cancel":
		read := db.Import
		if name == "import cancel" {
			read = db.CancelImport
		}
		job, err := read(ctx)
		if err != nil {
			return err
		}
		return emitJSON(out, job)
	case "collection list":
		items := []v1.CollectionInfo{}
		for cursor := ""; ; {
			page, err := db.Collections(ctx, v1.ListOptions{Limit: 200, Cursor: cursor})
			if err != nil {
				return err
			}
			for _, c := range page.Items {
				if jsonMode(out) {
					items = append(items, c)
					continue
				}
				fmt.Fprintln(out, c.Name)
			}
			if cursor = page.NextCursor; cursor == "" {
				if jsonMode(out) {
					return emitJSON(out, items)
				}
				return nil
			}
		}
	case "collection create":
		c, err := db.CreateCollection(ctx, args[1])
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, c)
		}
		fmt.Fprintf(out, "Created collection %s\n", args[1])
		return nil
	case "collection drop":
		c, err := db.Collection(args[1]).Drop(ctx)
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, c)
		}
		fmt.Fprintf(out, "Dropped collection %s\n", args[1])
		return nil
	case "doc list":
		beginJSONStream(out)
		for cursor := ""; ; {
			page, err := db.Collection(args[1]).List(ctx, v1.ListOptions{Limit: 1000, Cursor: cursor})
			if err != nil {
				return err
			}
			for _, d := range page.Items {
				if err := emitJSONRecord(out, d); err != nil {
					return err
				}
			}
			if cursor = page.NextCursor; cursor == "" {
				if jsonMode(out) && ctx.Err() != nil {
					return ctx.Err()
				}
				return finishJSONStream(out)
			}
		}
	case "doc get":
		d, err := db.Collection(args[1]).Get(ctx, args[2])
		if err != nil {
			return err
		}
		return emitJSON(out, d)
	case "doc put":
		body, err := jsonOperand(args[len(args)-1])
		if err != nil {
			return err
		}
		col := db.Collection(args[1])
		var res *v1.WriteResult
		if len(args) == 4 {
			res, err = col.Put(ctx, args[2], json.RawMessage(body))
		} else {
			res, err = col.Create(ctx, json.RawMessage(body))
		}
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, res)
		}
		fmt.Fprintf(out, "Wrote %s (version %s)\n", res.ID, res.Version)
		return nil
	case "doc delete":
		res, err := db.Collection(args[1]).Delete(ctx, args[2])
		if err != nil {
			return err
		}
		if jsonMode(out) {
			return emitJSON(out, res)
		}
		fmt.Fprintf(out, "Deleted %s\n", args[2])
		return nil
	case "doc import":
		if _, err := db.CreateCollection(ctx, args[1]); err != nil && !errors.Is(err, v1.ErrNameTaken) {
			return err
		}
		return importDocuments(ctx, db.Collection(args[1]), os.Stdin, out)
	case "keys list":
		items := []tsauth.APIKey{}
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		if !jsonMode(out) {
			fmt.Fprintln(w, "NAME\tID\tDATABASE\tSCOPES\tEXPIRES\tALLOWED CIDRS")
		}
		for k, err := range pages(func(after string) (*v1.Page[tsauth.APIKey], error) {
			p, err := s.user.APIKeys(ctx, s.org, s.project, tsauth.PageOptions{Limit: 100, After: after})
			return (*v1.Page[tsauth.APIKey])(p), err
		}) {
			if err != nil {
				return err
			}
			if jsonMode(out) {
				items = append(items, k)
				continue
			}
			expires := "never"
			if k.ExpiresAt != nil {
				expires = k.ExpiresAt.Format(time.RFC3339)
			}
			fmt.Fprintf(w, "%s\t%s\t%s\t%s\t%s\t%s\n", k.Name, k.ID, cmp.Or(k.Database, "*"), strings.Join(k.Scopes, ","), expires, cmp.Or(strings.Join(k.AllowedCIDRs, ","), "*"))
		}
		if jsonMode(out) {
			return emitJSON(out, items)
		}
		return w.Flush()
	case "keys revoke":
		if err := s.user.RevokeAPIKey(ctx, s.org, s.project, args[0]); err != nil {
			return err
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

// jsonOperand returns a JSON operand, read from stdin when it is -.
func jsonOperand(arg string) ([]byte, error) {
	body := []byte(arg)
	if arg == "-" {
		var err error
		if body, err = io.ReadAll(os.Stdin); err != nil {
			return nil, err
		}
	}
	if !json.Valid(body) {
		return nil, errors.New("operand is not valid JSON")
	}
	return body, nil
}

// importDocuments puts each JSON line of in, in the shape doc list prints:
// an object with the document's id and body.
func importDocuments(ctx context.Context, col *v1.Collection, in io.Reader, out io.Writer) error {
	lines := bufio.NewScanner(in)
	lines.Buffer(nil, 8<<20)
	n := 0
	for line := 1; lines.Scan(); line++ {
		if strings.TrimSpace(lines.Text()) == "" {
			continue
		}
		var d struct {
			ID       string          `json:"id"`
			Document json.RawMessage `json:"document"`
		}
		if err := json.Unmarshal(lines.Bytes(), &d); err != nil || d.ID == "" || len(d.Document) == 0 {
			return fmt.Errorf("line %d: want {\"id\": ..., \"document\": {...}}", line)
		}
		if _, err := col.Put(ctx, d.ID, d.Document); err != nil {
			return fmt.Errorf("line %d: %w", line, err)
		}
		n++
	}
	if err := lines.Err(); err != nil {
		return err
	}
	if jsonMode(out) {
		return emitJSON(out, struct {
			Imported int `json:"imported"`
		}{n})
	}
	fmt.Fprintf(out, "Imported %d documents\n", n)
	return nil
}
