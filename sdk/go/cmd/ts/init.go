package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"time"

	"terrascale.tech/sdk/tsauth"
)

type initSnippet struct {
	install string
	code    string
}

var initSnippets = map[string]initSnippet{
	"js": {
		install: "npm install @terrascale/sdk",
		code: `import { createTerraBaseV1Client } from "@terrascale/sdk";
import { createTsAuthClient } from "@terrascale/sdk/ts-auth";
const apiKey = process.env.TERRASCALE_API_KEY;
const auth = createTsAuthClient({ apiBaseUrl: "https://auth.terrascale.tech", instanceSlug: "terrascale" });
const db = createTerraBaseV1Client({ project: process.env.TERRASCALE_PROJECT_ID,
  token: async () => (await auth.oidc.token(apiKey.slice(0, apiKey.indexOf("_")), apiKey,
    { grant_type: "client_credentials", project: process.env.TERRASCALE_PROJECT_ID, scope: "sql:read sql:write" })).value.access_token,
}).database(process.env.TERRASCALE_DATABASE_NAME);
const rows = await db.query("SELECT 1");`,
	},
	"go": {
		install: "go get terrascale.tech/sdk",
		code: `tokens, err := tsauth.NewAPIKeySource(tsauth.APIKeyConfig{
	Key: os.Getenv("TERRASCALE_API_KEY"), Project: os.Getenv("TERRASCALE_PROJECT_ID"),
})
client, err := v1.New(v1.Config{TokenSource: tokens})
db := client.Database(os.Getenv("TERRASCALE_PROJECT_ID"), os.Getenv("TERRASCALE_DATABASE_NAME"))
rows, err := db.Query(ctx, "SELECT 1")`,
	},
	"python": {
		install: `pip install "terrascale @ git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"`,
		code: `import os
from terrascale import TerraScale

db = TerraScale(project=os.environ["TERRASCALE_PROJECT_ID"]).database(os.environ["TERRASCALE_DATABASE_NAME"])
rows = db.query("SELECT 1")`,
	},
	"dotnet": {
		install: "dotnet add package TerraScale.DotNet --source ~/.nuget/packages-terrascale",
		code: `using var http = new HttpClient(TerraBaseHttp.CreateHandler());
var sql = new TerraBaseSqlClient(http, new TerraBaseSqlClientOptions {
    ApiKey = Environment.GetEnvironmentVariable("TERRASCALE_API_KEY"), Region = Environment.GetEnvironmentVariable("TERRASCALE_REGION") });
var db = sql.Database(Environment.GetEnvironmentVariable("TERRASCALE_PROJECT_ID"), Environment.GetEnvironmentVariable("TERRASCALE_DATABASE_NAME"));
var rows = await db.QueryAsync("SELECT 1");`,
	},
	"rust": {
		install: "cargo add terrascale --git https://github.com/TerraScale/terrascale",
		code: `let client = terrascale::v1::Client::for_project_api_key(
    &std::env::var("TERRASCALE_PROJECT_ID")?, &std::env::var("TERRASCALE_API_KEY")?).build()?;
let db = client.database(&std::env::var("TERRASCALE_DATABASE_NAME")?).sql();
let rows = db.query("SELECT 1", &[]).await?;`,
	},
}

var initLanguageNames = map[string]string{
	"js": "JavaScript", "go": "Go", "python": "Python", "dotnet": ".NET", "rust": "Rust",
}

func detectInitLanguages(dir string) []string {
	entries, err := os.ReadDir(dir)
	if err != nil {
		return nil
	}
	var found []string
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		switch e.Name() {
		case "package.json":
			found = append(found, "js")
		case "go.mod":
			found = append(found, "go")
		case "pyproject.toml", "requirements.txt":
			found = append(found, "python")
		case "Cargo.toml":
			found = append(found, "rust")
		}
		if strings.HasSuffix(e.Name(), ".csproj") {
			found = append(found, "dotnet")
		}
	}
	return found
}

func initEnvLines(project, database, key string) string {
	return fmt.Sprintf("TERRASCALE_PROJECT_ID=%s\nTERRASCALE_DATABASE_NAME=%s\nTERRASCALE_API_KEY=%s\n", project, database, key)
}

func writeInitEnv(dir, lines string, out io.Writer) error {
	path := filepath.Join(dir, ".env")
	if _, err := os.Stat(path); err == nil {
		_, err := fmt.Fprintf(out, ".env already exists; add these lines yourself:\n%s", lines)
		return err
	}
	if err := os.WriteFile(path, []byte(lines), 0o600); err != nil {
		return err
	}
	return os.Chmod(path, 0o600)
}

func warnUnignoredEnv(dir string, prompt io.Writer) {
	data, err := os.ReadFile(filepath.Join(dir, ".gitignore"))
	if err != nil || strings.Contains(string(data), ".env") {
		return
	}
	fmt.Fprintln(prompt, "warning: .gitignore does not list .env; the file holds a secret, add it before committing")
}

func runInit(ctx context.Context, s *session, database, lang string, input commandInput, out, prompt io.Writer) error {
	dir, err := os.Getwd()
	if err != nil {
		return err
	}
	var existing []string
	ids := map[string]string{}
	for db, err := range projectDatabases(ctx, s.tb, s.project, 100) {
		if err != nil {
			return err
		}
		existing = append(existing, db.Name)
		ids[db.Name] = db.ID
	}
	if database == "" {
		switch len(existing) {
		case 0:
			database = "main"
		case 1:
			database = existing[0]
		default:
			return fmt.Errorf("%d databases in this project: pass one to ts init: %w", len(existing), errUsage)
		}
	}
	id := ids[database]
	keyName := fmt.Sprintf("ts-init-%s-%s", database, time.Now().UTC().Format("20060102"))
	message := fmt.Sprintf("Create API key %q scoped to database %q with sql:read and sql:write", keyName, database)
	if id == "" {
		message = fmt.Sprintf("Create database %q with the project's default placement, then %s", database, strings.ToLower(message[:1])+message[1:])
		if err := confirmNamedOperation(database, message, "ts init", input.yes, input.in, prompt, input.tty); err != nil {
			return err
		}
		created, err := s.tb.Database(s.project, database).Create(ctx)
		if err != nil {
			return err
		}
		id = created.ID
		if _, err := fmt.Fprintf(out, "Created database %s (%s)\n", database, id); err != nil {
			return err
		}
	} else if err := confirmNamedOperation(database, message, "ts init", input.yes, input.in, prompt, input.tty); err != nil {
		return err
	}
	k, err := stepUp(ctx, s, tsauth.StepUpCreateAPIKey, "Creating API keys", func(u *tsauth.User) (*tsauth.CreatedAPIKey, error) {
		return u.CreateAPIKey(ctx, s.org, s.project, tsauth.APIKeyRequest{Name: keyName,
			Scopes: []string{tsauth.ScopeSQLRead, tsauth.ScopeSQLWrite}, Database: id})
	}, prompt)
	if err != nil {
		return err
	}
	if k == nil || k.APIKey.ProjectID != s.project || k.APIKey.Database != id || k.Key == "" {
		return errors.New("API key response did not match the request; inspect key state before retrying")
	}
	lines := initEnvLines(s.project, database, k.Key)
	if err := writeInitEnv(dir, lines, out); err != nil {
		return keyOutputError(err)
	}
	warnUnignoredEnv(dir, prompt)
	if _, err := fmt.Fprintf(out, "API key %s, shown once:\n%s\n", keyName, k.Key); err != nil {
		return keyOutputError(err)
	}
	languages := []string{lang}
	if lang == "" {
		if detected := detectInitLanguages(dir); len(detected) > 0 {
			languages = detected
		} else {
			languages = []string{"go", "js", "python", "dotnet", "rust"}
		}
	}
	for _, name := range languages {
		snippet, ok := initSnippets[name]
		if !ok {
			continue
		}
		fmt.Fprintf(out, "\n%s\n%s\n\n%s\n", initLanguageNames[name], snippet.install, snippet.code)
	}
	return nil
}
