---
title: Build a todo app with no backend
---

# Build a todo app with no backend

Sign in a guest, read live todos and create, update and delete them from the browser. TerraBase Rules keep each user's todos private. You need no application server.

## Create a project and database

1. [Create a TerraScale account](https://dash.terrascale.tech/register) and name your project during setup. For an existing account, select or create a project in the dashboard.
2. Open **Databases**, select **New database**, name it `todos` and select **Create database**.
3. Copy the project ID and database ID. Both are public identifiers, not credentials.
4. Open the database's SQL editor and run:

```sql
CREATE TABLE todos (
  id TEXT PRIMARY KEY NOT NULL,
  owner_id TEXT NOT NULL,
  title TEXT NOT NULL,
  done INTEGER NOT NULL DEFAULT 0
);
```

Schema changes use your administrator access. Browser users will use only the row and shape APIs.

## Register the browser client

Open **Authentication** for your project and permit public sign-up (`sign_up_mode: public`). Register a public OAuth client through the project clients API, with guest admission and your app's exact origin.

Send this body to `POST https://auth.terrascale.tech/v1/{project}/clients`:

```json
{
  "name": "Todo app",
  "type": "public",
  "redirect_uris": ["http://localhost:5173/callback"],
  "allowed_origins": ["http://localhost:5173"],
  "allow_anonymous": true
}
```

Registration needs `Authorization: Bearer <TerraScale sign-in token>`, `Content-Type: application/json`, project admin access and `X-TerraScale-Step-Up-Proof: <create_oauth_client proof>`. See [end-user authentication](/guides/end-user-authentication#set-up-sign-in-in-the-dashboard) and [sensitive actions](/guides/sensitive-actions). Copy the returned `client_id` for your app.

Origins are exact: scheme, host and port must match, with no path or trailing slash. `localhost` and `127.0.0.1` are different origins. Use HTTPS for a deployed app and register that origin before serving it. An allowed origin permits browser requests; rules still decide data access.

## Deploy the rules

Save this as `terrascale.rules.json`:

```json
{
  "tables": {
    "todos": {
      "read": "row.owner_id = auth.uid",
      "create": "new.owner_id = auth.uid",
      "update": "row.owner_id = auth.uid",
      "delete": "row.owner_id = auth.uid",
      "columns": ["id", "owner_id", "title", "done"],
      "writable": ["title", "done"],
      "validate": ["length(new.title) BETWEEN 1 AND 200", "new.done IN (0, 1)"],
      "set": {
        "create": {"id": "request.ulid", "owner_id": "auth.uid"}
      }
    }
  }
}
```

The server sets `id` and `owner_id`. The browser can send only `title` and `done`. The four conditions allow users to read and change only their own todos; validation rejects empty titles and invalid completion values. [Rules](/concepts/rules) explains each field.

Build the CLI from the [TerraScale checkout](https://github.com/TerraScale/terrascale) if your installed release lacks `rules` commands:

```sh
(cd /path/to/terrascale/sdk/go && go build -o "$HOME/.local/bin/ts" ./cmd/ts)
```

With the [CLI](/cli), sign in and deploy to the database. Replace `YOUR_PROJECT_ID` with your project ID:

```sh
ts auth login
ts -project YOUR_PROJECT_ID rules deploy todos
```

Review the diff and type `todos` when prompted. The command uses the current rules version to prevent overwriting a concurrent deployment. If it reports a version mismatch, pull with `ts -project YOUR_PROJECT_ID rules pull todos`, reapply your edits, then deploy again. Pull replaces the local file.

You can deploy the same JSON in the dashboard: open the database, select **Rules**, paste it into **Rules JSON**, and select **Deploy rules**. Fix any reported problems before continuing.

## Install the browser SDK

Use Node.js 24 and a checkout of [TerraScale](https://github.com/TerraScale/terrascale). This example uses `BrowserAuth` and row operations from the current source; the previous published JavaScript package does not include them.

Create the browser app in a separate directory, with an absolute path to your SDK checkout:

```sh
mkdir todo-app
cd todo-app
npm init -y
npm install /path/to/terrascale/sdk/js
npm install --save-dev vite@6.4.5
```

Save this as `index.html`:

```html
<!doctype html>
<html lang="en">
  <head>
    <meta charset="utf-8">
    <meta name="viewport" content="width=device-width, initial-scale=1">
    <title>Todos</title>
  </head>
  <body>
    <h1>Todos</h1>
    <form id="add">
      <label for="title">Todo</label>
      <input id="title" name="title" required maxlength="200">
      <button>Add</button>
    </form>
    <ul id="todos"></ul>
    <p id="status" role="status"></p>
    <script type="module" src="/main.js"></script>
  </body>
</html>
```

## Sign in and read live todos

Save this as `main.js`. Replace the three public identifiers with yours:

```js
import { BrowserAuth, createTerraBaseClient } from "@terrascale/sdk";

const project = "YOUR_PROJECT_ID";
const database = "YOUR_DATABASE_ID";
const clientId = "YOUR_CLIENT_ID";
const authConfig = {
  project,
  clientId,
  authBaseUrl: "https://auth.terrascale.tech",
  redirectUri: location.origin + "/callback",
};
const auth = new BrowserAuth(authConfig);
const db = createTerraBaseClient({
  project,
  token: () => auth.accessToken(),
}).database(database);

const status = document.querySelector("#status");
const list = document.querySelector("#todos");
const form = document.querySelector("#add");
const title = document.querySelector("#title");

function unwrap(result) {
  if (!result.ok) throw new Error(result.error.code);
  return result.value;
}

async function run(action) {
  try {
    await action();
    status.textContent = "";
  } catch (error) {
    status.textContent = error.message;
  }
}

unwrap(await auth.signInGuest());

const subscription = db.shape("todos", {
  columns: ["id", "owner_id", "title", "done"],
}).subscribe(state => {
  list.replaceChildren();
  for (const [key, todo] of state.rows) {
    const item = document.createElement("li");
    const label = document.createElement("label");
    const checkbox = document.createElement("input");
    checkbox.type = "checkbox";
    checkbox.checked = Number(todo.done) === 1;
    checkbox.addEventListener("change", () => run(async () => {
      unwrap(await db.rows("todos").update(key, {
        done: checkbox.checked ? 1 : 0,
      }));
    }));
    label.append(checkbox, document.createTextNode(String(todo.title)));
    const remove = document.createElement("button");
    remove.textContent = "Delete";
    remove.addEventListener("click", () => run(async () => {
      unwrap(await db.rows("todos").delete(key));
    }));
    item.append(label, remove);
    list.append(item);
  }
});
subscription.done.catch(error => { status.textContent = error.message; });

form.addEventListener("submit", event => {
  event.preventDefault();
  run(async () => {
    unwrap(await db.rows("todos").create({ title: title.value, done: 0 }));
    form.reset();
  });
});

window.addEventListener("pagehide", () => subscription.close(), { once: true });
```

Start the app:

```sh
npx vite --host localhost --port 5173 --strictPort
```

Open `http://localhost:5173`. Add a todo, select its checkbox, then delete it. The live shape updates the list after each write. It contains only rows allowed by `read`; the browser does not supply an owner filter.

The subscription's map keys are decimal row IDs. Use these keys for `get`, `update` and `delete`; the generated ULID in the `id` column is a separate value.

For a single row, `unwrap(await db.rows("todos").get(key)).row` returns the readable columns. Mutation results contain `{ key, row }`. Each row operation returns a result: check `ok` before using `value`, or handle `error.code`, `error.status` and `error.details`.

Guest tokens stay in memory and refresh while the page is open. Reloading creates a new guest with a different ID, so earlier todos are no longer visible. For returning users, use hosted sign-in with `await auth.signIn()` and call `await auth.completeSignIn()` on the registered callback instead of `signInGuest()`. Choose **Email a sign-in link** when your project offers it.

Close subscriptions and clear displayed data when signing out or changing users. `await auth.signOut()` clears local credentials and revokes the refresh family; issued access tokens can remain valid until expiry.

## Check a denied write

Temporarily append this to `main.js` and reload once. It creates a todo as the first guest, signs in a second guest and tries to change the first guest's todo:

```js
const original = unwrap(await db.rows("todos").create({
  title: "Private todo",
  done: 0,
}));
const otherAuth = new BrowserAuth(authConfig);
unwrap(await otherAuth.signInGuest());
const otherDb = createTerraBaseClient({
  project,
  token: () => otherAuth.accessToken(),
}).database(database);

const denied = await otherDb.rows("todos").update(original.key, {
  title: "Changed by someone else",
});
if (denied.ok || denied.error.code !== "row_not_found") {
  throw new Error("Expected the other user's write to be denied");
}
console.log(denied.error.status, denied.error.code);
console.log(unwrap(await db.rows("todos").get(original.key)).row.title);
unwrap(await db.rows("todos").delete(original.key));
unwrap(await otherAuth.signOut());
```

The console shows `404 row_not_found` and `Private todo`. A hidden row looks like a missing row; the second guest learns nothing about its contents. Remove this appended section after checking it.

## Test the rule

The simulator evaluates supplied values without reading or writing application rows. Run an allowed update:

```sh
ts -project YOUR_PROJECT_ID rules test todos --operation update --table todos \
  --auth '{"uid":"alice","is_anonymous":false}' \
  --row '{"id":"sample","owner_id":"alice","title":"Private todo","done":0}' \
  --new '{"done":1}'
```

The result has `allowed: true`. Change the owner to test a denial:

```sh
ts -project YOUR_PROJECT_ID rules test todos --operation update --table todos \
  --auth '{"uid":"alice","is_anonymous":false}' \
  --row '{"id":"sample","owner_id":"bob","title":"Private todo","done":0}' \
  --new '{"done":1}'
```

This reports `allowed: false` and exits with status 1. Add `--draft` to test the local `terrascale.rules.json` before deployment.

In the dashboard's **Rules** → **Playground**, use **Operation** `update`, **Table** `todos` and the same **Auth**, **Row** and **New** objects. Select **Run test** to evaluate the editor's draft and see which condition denied it.

## Handle a refused request

An empty title returns `400 validation_failed`. Sending `owner_id` or `id` returns `403 column_not_writable`. A missing or false operation condition returns `403 rule_denied`; updates and deletes of unreadable rows return `404 row_not_found`.

A timeout or `503 write_outcome_unknown` can mean a mutation committed. Do not automatically replay it. Chrome and Edge can resend a buffered request once after `421 wrong_region`; that status means nothing was applied. See [Rules errors](/concepts/rules#handle-errors) before adding write recovery.
