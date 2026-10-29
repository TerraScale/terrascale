import { revalidatePath } from "next/cache";
import { posts } from "../terrascale";

export const dynamic = "force-dynamic";

async function addPost(form: FormData) {
  "use server";
  const title = String(form.get("title") ?? "").trim();
  if (!title) return;
  await (await posts()).create({ title, publishedAt: new Date().toISOString() });
  revalidatePath("/");
}

export default async function Page() {
  // Document ids are ULIDs, so sorting by $id descending lists the newest first.
  const page = await (await posts()).query({ sort: [{ path: "$id", order: "desc" }], limit: 20 });
  return (
    <main style={{ maxWidth: 720, margin: "0 auto", padding: "4rem 1.5rem", fontFamily: "system-ui, sans-serif" }}>
      <h1>TerraScale starter</h1>
      <form action={addPost}>
        <label htmlFor="title">Post title</label>{" "}
        <input id="title" name="title" maxLength={200} required />{" "}
        <button type="submit">Add post</button>
      </form>
      {page.items.length === 0 ? <p>No posts yet.</p> : (
        <ul>{page.items.map((post) => <li key={post.id}>{String(post.document.title ?? "Untitled")}</li>)}</ul>
      )}
    </main>
  );
}
