import { revalidatePath } from "next/cache";
import { generateId } from "@terrascale/sdk";
import { database } from "../terrascale";

export const dynamic = "force-dynamic";

async function addPost(form: FormData) {
  "use server";
  const title = String(form.get("title") ?? "").trim();
  if (!title) return;
  await (await database()).execute(
    "INSERT INTO posts (id, title, published_at) VALUES (?, ?, ?)",
    [generateId(), title, new Date().toISOString()],
  );
  revalidatePath("/");
}

export default async function Page() {
  // Ids are ULIDs, so sorting by id descending lists the newest first.
  const posts = await (await database()).query<{ id: string; title: string }>(
    "SELECT id, title FROM posts ORDER BY id DESC LIMIT 20",
  );
  return (
    <main style={{ maxWidth: 720, margin: "0 auto", padding: "4rem 1.5rem", fontFamily: "system-ui, sans-serif" }}>
      <h1>TerraScale starter</h1>
      <form action={addPost}>
        <label htmlFor="title">Post title</label>{" "}
        <input id="title" name="title" maxLength={200} required />{" "}
        <button type="submit">Add post</button>
      </form>
      {posts.length === 0 ? <p>No posts yet.</p> : (
        <ul>{posts.map((post) => <li key={post.id}>{post.title}</li>)}</ul>
      )}
    </main>
  );
}
