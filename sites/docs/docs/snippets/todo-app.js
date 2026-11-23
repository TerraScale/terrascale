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
