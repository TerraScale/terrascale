// Command ts is the TerraScale CLI: device sign-in, projects, databases,
// collections, documents, SQL and API keys against the production API, as
// the signed-in user. Run ts help for every command.
//
// Project commands act in the user's only project; -project <name or id>
// picks one when the user reaches several, and -org <name or id> picks the
// organization of projects create.
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
	"path/filepath"
	"slices"
	"strings"
	"text/tabwriter"
	"time"

	"terrascale.tech/sdk/tsauth"
	v1 "terrascale.tech/sdk/v1"
)

var version = "dev" // Set by release builds with -ldflags.

const usage = `usage: ts [-project <name or id>] [-org <name or id>] <command>

  version                                 print the CLI version
  help                                    print this help
  auth login | logout                     sign in with your browser, or sign out
  whoami                                  print the signed-in account
  projects                                list the projects you can reach
  projects create <name>                  create a project (and an organization
                                          when you have none)
  usage [<db>]                            print the project's or a database's
                                          usage as JSON
  completion bash | zsh | fish            print a shell completion script

  db list | create <db> | delete <db>     manage the project's databases
  db branch <source> <db>                 copy a database into a new one
  db info <db>                            print a database's metadata as JSON
  db regions <db> <region>... | none      set the replica regions (gru, sjc,
                                          fra) besides the home region, or
                                          remove every replica
  backup list | create <db>               list or take a database's backups
  backup restore <db> <backup id> <new db> restore a backup into a new database
  backup restore-at <db> <time> <new db>  restore the database as of an RFC 3339
                                          time into a new database
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
                                          ndjson file into a new database
  import status | cancel <db>             print or cancel the database's import
  sql <db> <query>                        run a read-only SQL query
  keys list                               list the project's API keys
  keys create <name> [<db>]               create a read/write API key, shown once
  keys revoke <id>                        revoke an API key

Sign-in is stored in the user config directory (terrascale/cli.json, mode 0600).
Exit status: 0 success, 1 failure, 2 invalid arguments.
`

func main() {
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt)
	defer stop()
	if err := run(ctx, os.Args[1:], os.Stdout); err != nil {
		fmt.Fprintln(os.Stderr, "ts:", err)
		if errors.Is(err, errUsage) {
			os.Exit(2)
		}
		os.Exit(1)
	}
}

var errUsage = errors.New("invalid arguments: run ts help")

// commands maps each command to its accepted operand counts.
var commands = map[string][]int{
	"auth login":        {0},
	"auth logout":       {0},
	"whoami":            {0},
	"projects":          {0},
	"projects create":   {1},
	"usage":             {0, 1},
	"completion":        {1},
	"db changes":        {1, 2, 3, 4, 5},
	"backup restore-at": {3},
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
	"keys list":         {0},
	"keys create":       {1, 2},
	"keys revoke":       {1},
}

func run(ctx context.Context, args []string, out io.Writer) error {
	if len(args) == 1 && (args[0] == "version" || args[0] == "--version") {
		_, err := fmt.Fprintln(out, "ts", version)
		return err
	}
	if len(args) == 1 && args[0] == "help" || slices.ContainsFunc(args, func(a string) bool {
		return a == "-h" || a == "-help" || a == "--help"
	}) {
		_, err := io.WriteString(out, usage)
		return err
	}
	fs := flag.NewFlagSet("ts", flag.ContinueOnError)
	fs.SetOutput(io.Discard)
	project := fs.String("project", "", "project name or id")
	org := fs.String("org", "", "organization name or id")
	if fs.Parse(args) != nil {
		return errUsage
	}
	args = fs.Args()
	name, operands := "", args
	for n := min(2, len(args)); n > 0 && name == ""; n-- {
		if _, ok := commands[strings.Join(args[:n], " ")]; ok {
			name, operands = strings.Join(args[:n], " "), args[n:]
		}
	}
	if !slices.Contains(commands[name], len(operands)) {
		return errUsage
	}
	if name == "completion" {
		return completion(operands[0], out)
	}
	device, err := tsauth.NewDevice(tsauth.DeviceConfig{})
	if err != nil {
		return err
	}
	switch name {
	case "auth login":
		return login(ctx, device, out)
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
	switch name {
	case "whoami":
		return whoami(ctx, u, token, out)
	case "projects":
		return listProjects(ctx, u, out)
	case "projects create":
		return createProject(ctx, u, *org, operands[0], out)
	}
	s, err := open(ctx, u, token, *project)
	if err != nil {
		return err
	}
	return s.run(ctx, name, operands, out)
}

// credentials are the CLI's sign-in, kept in the user's config directory.
type credentials struct {
	AccessToken  string    `json:"accessToken"`
	RefreshToken string    `json:"refreshToken"`
	Expiry       time.Time `json:"expiry"`
}

func credentialsPath() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "terrascale", "cli.json"), nil
}

func save(t *tsauth.Tokens) error {
	path, err := credentialsPath()
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	data, err := json.Marshal(credentials{t.AccessToken, t.RefreshToken, t.Expiry})
	if err != nil {
		return err
	}
	return os.WriteFile(path, data, 0o600)
}

func login(ctx context.Context, d *tsauth.Device, out io.Writer) error {
	a, err := d.Start(ctx)
	if err != nil {
		return err
	}
	fmt.Fprintf(out, "Open %s\nand confirm the code %s\n", a.VerificationURIComplete, a.UserCode)
	t, err := d.Wait(ctx, a)
	if err != nil {
		return err
	}
	if err := save(t); err != nil {
		return err
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
func projects(ctx context.Context, u *tsauth.User) iter.Seq2[tsauth.Project, error] {
	return func(yield func(tsauth.Project, error) bool) {
		for org, err := range pages(func(after string) (*v1.Page[tsauth.Organization], error) {
			p, err := u.Organizations(ctx, tsauth.PageOptions{Limit: 100, After: after})
			return (*v1.Page[tsauth.Organization])(p), err
		}) {
			if err != nil {
				yield(tsauth.Project{}, err)
				return
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

func listProjects(ctx context.Context, u *tsauth.User, out io.Writer) error {
	w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
	fmt.Fprintln(w, "NAME\tID\tORGANIZATION\tROLE")
	for p, err := range projects(ctx, u) {
		if err != nil {
			return err
		}
		fmt.Fprintf(w, "%s\t%s\t%s\t%s\n", p.Name, p.ID, p.OrganizationID, p.Role)
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
	if parts := strings.Split(token, "."); len(parts) == 3 {
		if payload, err := base64.RawURLEncoding.DecodeString(parts[1]); err == nil {
			_ = json.Unmarshal(payload, &claims)
		}
	}
	_, err := fmt.Fprintln(out, cmp.Or(claims.Email, claims.Sub, "signed in"))
	return err
}

// session is the signed-in user's TerraBase client for one project.
type session struct {
	tb      *v1.Client
	user    *tsauth.User
	org     string
	project string
}

func open(ctx context.Context, u *tsauth.User, token, project string) (*session, error) {
	var matches []tsauth.Project
	for p, err := range projects(ctx, u) {
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
		return nil, errors.New("no project: create one at https://dash.terrascale.tech")
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
		for _, row := range res.Rows {
			fmt.Fprintf(out, "%s\n", row.Value)
		}
		if res.Truncated {
			fmt.Fprintf(os.Stderr, "ts: truncated: %d rows matched\n", res.Matched)
		}
		return nil
	case "db list":
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "NAME\tID\tHOME REGION\tREPLICAS")
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
		return w.Flush()
	case "db create":
		m, err := db.Create(ctx)
		if err != nil {
			return err
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
		fmt.Fprintf(out, "Deleted %s\n", args[0])
		return nil
	case "db branch":
		m, err := db.Branch(ctx, args[1])
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "Branched %s into %s (%s)\n", args[0], m.Name, m.ID)
		return nil
	case "db info":
		m, err := db.Info(ctx)
		if err != nil {
			return err
		}
		return json.NewEncoder(out).Encode(m)
	case "db regions":
		regions := args[1:]
		if slices.Equal(regions, []string{"none"}) {
			regions = nil
		}
		m, err := db.SetRegions(ctx, regions)
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "%s replicates to [%s]\n", args[0], strings.Join(m.ReplicaRegions, " "))
		return nil
	case "backup list":
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "ID\tSTATUS\tTRIGGER\tBYTES\tCREATED")
		for b, err := range pages(func(after string) (*v1.Page[v1.DatabaseBackup], error) {
			return db.Backups(ctx, v1.PageOptions{Limit: 100, After: after})
		}) {
			if err != nil {
				return err
			}
			fmt.Fprintf(w, "%s\t%s\t%s\t%d\t%s\n", b.ID, b.Status, b.Trigger, b.SizeBytes, b.CreatedAt.Format(time.RFC3339))
		}
		return w.Flush()
	case "backup create":
		b, err := db.CreateBackup(ctx)
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "Backup %s %s\n", b.ID, b.Status)
		return nil
	case "backup restore":
		m, err := db.RestoreBackup(ctx, args[1], args[2])
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "Restored %s into %s (%s)\n", args[1], m.Name, m.ID)
		return nil
	case "backup restore-at":
		at, err := time.Parse(time.RFC3339, args[1])
		if err != nil {
			return fmt.Errorf("time %q is not RFC 3339, such as 2026-10-23T12:00:00Z", args[1])
		}
		m, err := db.RestorePointInTime(ctx, v1.PointInTimeRequest{PointInTime: at, Name: args[2]})
		if err != nil {
			return err
		}
		fmt.Fprintf(out, "Restored %s as of %s into %s (%s)\n", args[0], at.Format(time.RFC3339), m.Name, m.ID)
		return nil
	case "db changes":
		return tailChanges(ctx, db, args[1:], out)
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
		return json.NewEncoder(out).Encode(u)
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
		return json.NewEncoder(out).Encode(schema)
	case "ttl get":
		path, err := db.Collection(args[1]).TTL(ctx)
		if err != nil {
			return err
		}
		_, err = fmt.Fprintln(out, cmp.Or(path, "none"))
		return err
	case "ttl set":
		if err := db.Collection(args[1]).SetTTL(ctx, args[2]); err != nil {
			return err
		}
		fmt.Fprintf(out, "%s expires documents at %s\n", args[1], args[2])
		return nil
	case "ttl delete":
		if err := db.Collection(args[1]).DeleteTTL(ctx); err != nil {
			return err
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
		fmt.Fprintf(out, "Created index %s\n", ix.Name)
		return nil
	case "index drop":
		if err := db.DropIndex(ctx, args[1]); err != nil {
			return err
		}
		fmt.Fprintf(out, "Dropped index %s\n", args[1])
		return nil
	case "import":
		return importFile(ctx, db, args[1], args[2], out)
	case "import status", "import cancel":
		read := db.Import
		if name == "import cancel" {
			read = db.CancelImport
		}
		job, err := read(ctx)
		if err != nil {
			return err
		}
		return json.NewEncoder(out).Encode(job)
	case "collection list":
		for cursor := ""; ; {
			page, err := db.Collections(ctx, v1.ListOptions{Limit: 200, Cursor: cursor})
			if err != nil {
				return err
			}
			for _, c := range page.Items {
				fmt.Fprintln(out, c.Name)
			}
			if cursor = page.NextCursor; cursor == "" {
				return nil
			}
		}
	case "collection create":
		if _, err := db.CreateCollection(ctx, args[1]); err != nil {
			return err
		}
		fmt.Fprintf(out, "Created collection %s\n", args[1])
		return nil
	case "collection drop":
		if _, err := db.Collection(args[1]).Drop(ctx); err != nil {
			return err
		}
		fmt.Fprintf(out, "Dropped collection %s\n", args[1])
		return nil
	case "doc list":
		enc := json.NewEncoder(out)
		for cursor := ""; ; {
			page, err := db.Collection(args[1]).List(ctx, v1.ListOptions{Limit: 1000, Cursor: cursor})
			if err != nil {
				return err
			}
			for _, d := range page.Items {
				if err := enc.Encode(d); err != nil {
					return err
				}
			}
			if cursor = page.NextCursor; cursor == "" {
				return nil
			}
		}
	case "doc get":
		d, err := db.Collection(args[1]).Get(ctx, args[2])
		if err != nil {
			return err
		}
		return json.NewEncoder(out).Encode(d)
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
		fmt.Fprintf(out, "Wrote %s (version %s)\n", res.ID, res.Version)
		return nil
	case "doc delete":
		if _, err := db.Collection(args[1]).Delete(ctx, args[2]); err != nil {
			return err
		}
		fmt.Fprintf(out, "Deleted %s\n", args[2])
		return nil
	case "doc import":
		if _, err := db.CreateCollection(ctx, args[1]); err != nil && !errors.Is(err, v1.ErrNameTaken) {
			return err
		}
		return importDocuments(ctx, db.Collection(args[1]), os.Stdin, out)
	case "keys list":
		w := tabwriter.NewWriter(out, 0, 0, 2, ' ', 0)
		fmt.Fprintln(w, "NAME\tID\tDATABASE\tSCOPES\tEXPIRES")
		for k, err := range pages(func(after string) (*v1.Page[tsauth.APIKey], error) {
			p, err := s.user.APIKeys(ctx, s.org, s.project, tsauth.PageOptions{Limit: 100, After: after})
			return (*v1.Page[tsauth.APIKey])(p), err
		}) {
			if err != nil {
				return err
			}
			expires := "never"
			if k.ExpiresAt != nil {
				expires = k.ExpiresAt.Format(time.RFC3339)
			}
			fmt.Fprintf(w, "%s\t%s\t%s\t%s\t%s\n", k.Name, k.ID, cmp.Or(k.Database, "*"), strings.Join(k.Scopes, ","), expires)
		}
		return w.Flush()
	case "keys create":
		req := tsauth.APIKeyRequest{Name: args[0], Scopes: []string{tsauth.ScopeDatabasesRead, tsauth.ScopeDatabasesWrite,
			tsauth.ScopeDocumentsRead, tsauth.ScopeDocumentsWrite}}
		if len(args) == 2 {
			req.Database = args[1]
		}
		k, err := s.user.CreateAPIKey(ctx, s.org, s.project, req)
		if err != nil {
			return err
		}
		fmt.Fprintf(os.Stderr, "Created key %s (%s). Store it now, it is not shown again:\n", k.APIKey.Name, k.APIKey.ID)
		fmt.Fprintln(out, k.Key)
		return nil
	case "keys revoke":
		if err := s.user.RevokeAPIKey(ctx, s.org, s.project, args[0]); err != nil {
			return err
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
	fmt.Fprintf(out, "Imported %d documents\n", n)
	return nil
}
