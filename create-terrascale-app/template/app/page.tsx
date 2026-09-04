"use client";

import { useEffect, useMemo, useState } from "react";
import { createTerraScaleClient, Direction, select, TerraScaleSyncV2Client } from "@terrascale/sdk";
import { authorId, posts, publishedAt, title } from "../schema";
import type { FormEvent } from "react";

type Post = { id: string; title: string; authorId: string; publishedAt: string };

const databaseId = process.env.NEXT_PUBLIC_TERRASCALE_DATABASE_ID?.trim() ?? "";
const apiBaseUrl = process.env.NEXT_PUBLIC_TERRASCALE_API_URL;
const client = createTerraScaleClient({
  apiBaseUrl,
  projectId: process.env.NEXT_PUBLIC_TERRASCALE_PROJECT_ID,
  accessToken: process.env.NEXT_PUBLIC_TERRASCALE_API_KEY,
});
const sync = new TerraScaleSyncV2Client({
  apiBaseUrl,
  accessToken: process.env.NEXT_PUBLIC_TERRASCALE_API_KEY,
});

// The SQL builder validates fields against the schema and keeps LIMIT as a
// parameter. The attach contract names positional bindings "1", "2", … and
// carries them as canonical JSON bytes.
const postsQuery = select(posts)
  .columns([title, authorId, publishedAt])
  .orderByField(publishedAt, Direction.Desc)
  .limit(20)
  .subscribe();
const queryParameters = new TextEncoder().encode(JSON.stringify(
  Object.fromEntries(postsQuery.parameters.map((value, index) => [String(index + 1), value])),
));

function decodeJson(bytes: Uint8Array | undefined): unknown {
  if (bytes === undefined) return undefined;
  try {
    return JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    return undefined;
  }
}

function projectPost(item: { identity: string; value: Readonly<Record<string, Uint8Array>> }): Post {
  return {
    id: item.identity,
    title: String(decodeJson(item.value.title) ?? "Untitled post"),
    authorId: String(decodeJson(item.value.authorId) ?? ""),
    publishedAt: String(decodeJson(item.value.publishedAt) ?? ""),
  };
}

export default function Page() {
  const [rows, setRows] = useState<Post[]>([]);
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [titleInput, setTitleInput] = useState("");
  const [saving, setSaving] = useState(false);
  const configured = databaseId.length > 0;
  const signInUrl = useMemo(() => "https://app.terrascale.tech/sign-in", []);

  useEffect(() => {
    if (!configured) return;
    let closed = false;
    let stop: (() => void) | undefined;
    let live: Awaited<ReturnType<TerraScaleSyncV2Client["subscribe"]>> | undefined;
    setError(undefined);

    void sync.subscribe({
      databaseId,
      subscription: "starter-posts",
      query: postsQuery.sql,
      parameters: queryParameters,
    }).then((next) => {
      live = next;
      if (closed) {
        next.close();
        return;
      }
      const render = () => setRows(next.view.getVisibleItems().map(projectPost));
      stop = next.view.subscribe(render);
      setConnected(true);
      render();
    }).catch((cause: unknown) => {
      if (!closed) setError(cause instanceof Error ? cause.message : String(cause));
    });

    return () => {
      closed = true;
      stop?.();
      live?.close();
      setConnected(false);
    };
  }, [configured]);

  async function addPost(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const value = titleInput.trim();
    if (!value || !configured) return;
    setSaving(true);
    setError(undefined);
    try {
      const result = await client.documents.put(databaseId, "posts", crypto.randomUUID(), {
        title: value,
        authorId: "starter",
        publishedAt: new Date().toISOString(),
      }, { durability: "regional" });
      if (!result.ok) throw new Error(result.error.message);
      setTitleInput("");
    } catch (cause: unknown) {
      setError(cause instanceof Error ? cause.message : String(cause));
    } finally {
      setSaving(false);
    }
  }

  return (
    <main style={{ maxWidth: 720, margin: "0 auto", padding: "4rem 1.5rem", fontFamily: "system-ui, sans-serif" }}>
      <p style={{ color: "#5b6472", letterSpacing: "0.08em", textTransform: "uppercase", fontSize: 12 }}>TerraScale starter</p>
      <h1>Posts that stay in sync.</h1>
      <p>Open this page in two tabs. Add a post in one tab and the cloud-backed live query updates both.</p>
      {!configured ? (
        <p><a href={signInUrl}>Sign in to TerraScale</a> to provision this project&apos;s production binding, then restart the dev server.</p>
      ) : (
        <>
          <p aria-live="polite">{connected ? "Live over CDN-backed HTTP" : "Connecting to the cloud branch…"}</p>
          <form onSubmit={addPost} style={{ display: "flex", gap: 8, margin: "2rem 0" }}>
            <label htmlFor="post-title" style={{ flex: 1 }}>
              <span style={{ display: "block", fontSize: 13, marginBottom: 4 }}>Post title</span>
              <input id="post-title" value={titleInput} onChange={(event) => setTitleInput(event.target.value)} placeholder="A thought worth sharing" required style={{ width: "100%", padding: 10 }} />
            </label>
            <button type="submit" disabled={saving || !connected} style={{ alignSelf: "end", padding: "10px 14px" }}>{saving ? "Saving…" : "Add post"}</button>
          </form>
          {rows.length === 0 ? <p>No posts yet. Add one to see the live query in action.</p> : (
            <ul>
              {rows.map((post) => <li key={post.id} style={{ margin: "0.75rem 0" }}>{post.title}</li>)}
            </ul>
          )}
        </>
      )}
      {error ? <p role="alert" style={{ color: "#a12622" }}>{error}</p> : null}
    </main>
  );
}
