"use client";

import { useEffect, useMemo, useState } from "react";
import { createTerraScaleClient, Direction, select, TerraScaleSyncV2Client } from "@terrascale/sdk";
import { authorId, posts, publishedAt, title } from "../schema";

type Post = { id: string; title: string; authorId: string; publishedAt: string };

const databaseId = process.env.NEXT_PUBLIC_TERRASCALE_DATABASE_ID?.trim() ?? "";
const apiBaseUrl = process.env.NEXT_PUBLIC_TERRASCALE_API_URL;
const client = createTerraScaleClient({
  apiBaseUrl,
  projectId: process.env.NEXT_PUBLIC_TERRASCALE_PROJECT_ID,
  accessToken: process.env.NEXT_PUBLIC_TERRASCALE_API_KEY,
});
const sync = client.reactive;

// The SQL builder validates fields against the schema and keeps LIMIT as a
// positional parameter. The attach contract carries that parameter list as
// canonical JSON bytes.
const postsQuery = select(posts)
  .columns([title, authorId, publishedAt])
  .orderByField(publishedAt, Direction.Desc)
  .limit(20)
  .subscribe();
const queryParameters = new TextEncoder().encode(JSON.stringify(postsQuery.parameters));

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
  const configured = databaseId.length > 0 && Boolean(process.env.NEXT_PUBLIC_TERRASCALE_API_KEY);
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

  return (
    <main style={{ maxWidth: 720, margin: "0 auto", padding: "4rem 1.5rem", fontFamily: "system-ui, sans-serif" }}>
      <p style={{ color: "#5b6472", letterSpacing: "0.08em", textTransform: "uppercase", fontSize: 12 }}>TerraScale starter</p>
      <h1>Posts that stay in sync.</h1>
      <p>Open this page in two tabs. Both attach the same cloud-backed live query.</p>
      {!configured ? (
        <p><a href={signInUrl}>Sign in to TerraScale</a> to get an API key and cloud dev branch, then add them to <code>.env.local</code> and restart the dev server.</p>
      ) : (
        <>
          <p aria-live="polite">{connected ? "Live over CDN-backed HTTP" : "Connecting to the cloud branch…"}</p>
          {rows.length === 0 ? <p>No posts yet. Seed the cloud branch when the published CLI seed contract is available.</p> : (
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
