"use client";

import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { createTerraScaleClient, Direction, select, type TerraScaleSyncV1Client } from "@terrascale/sdk";
import { authorId, posts, publishedAt, title } from "../schema";

type Post = { id: string; title: string };
const databaseId = process.env.NEXT_PUBLIC_TERRASCALE_DATABASE_ID?.trim() ?? "";
const apiBaseUrl = process.env.NEXT_PUBLIC_TERRASCALE_API_URL;
const postsQuery = select(posts)
  .columns([title, authorId, publishedAt])
  .orderByField(publishedAt, Direction.Desc)
  .limit(20)
  .subscribe();
const queryParameters = new TextEncoder().encode(JSON.stringify(postsQuery.parameters));

function projectPost(item: { identity: string; value: Readonly<Record<string, Uint8Array>> }): Post {
  const bytes = item.value.title;
  const value: unknown = bytes === undefined ? undefined : JSON.parse(new TextDecoder().decode(bytes));
  return { id: item.identity, title: typeof value === "string" ? value : "Untitled post" };
}

export default function Page() {
  const [credential, setCredential] = useState("");
  function connect(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = event.currentTarget;
    const key = String(new FormData(form).get("credential") ?? "").trim();
    if (key) setCredential(key);
    form.reset();
  }
  return (
    <main style={{ maxWidth: 720, margin: "0 auto", padding: "4rem 1.5rem", fontFamily: "system-ui, sans-serif" }}>
      <p>TerraScale development starter</p>
      <h1>Posts that stay in sync.</h1>
      <p>Connect two browser tabs to the same cloud branch, then add a post in either tab.</p>
      {!databaseId ? <p role="alert">Set NEXT_PUBLIC_TERRASCALE_DATABASE_ID in .env.local and restart the development server.</p> : credential ? (
        <>
          <button onClick={() => setCredential("")}>Disconnect and clear credential</button>
          <LivePosts key={credential} credential={credential} />
        </>
      ) : (
        <>
          <p><a href="https://app.terrascale.tech/sign-in">Sign in to TerraScale</a> to obtain a credential restricted to your development branch, with posts read/write and reactive access.</p>
          <p>This development connection keeps your credential in this tab’s memory until disconnect or reload. Application sign-in integration is not available in this template yet.</p>
          <form onSubmit={connect}>
            <label htmlFor="credential">Development branch credential</label>{" "}
            <input id="credential" name="credential" type="password" autoComplete="off" required />{" "}
            <button type="submit">Connect</button>
          </form>
        </>
      )}
    </main>
  );
}

function LivePosts({ credential }: { credential: string }) {
  const client = useMemo(() => createTerraScaleClient({
    apiBaseUrl,
    projectId: process.env.NEXT_PUBLIC_TERRASCALE_PROJECT_ID,
    accessToken: credential,
  }), [credential]);
  const [rows, setRows] = useState<Post[]>([]);
  const [attached, setAttached] = useState(false);
  const [error, setError] = useState<string>();
  const [writing, setWriting] = useState(false);
  const [writeStatus, setWriteStatus] = useState("");
  const writeController = useRef<AbortController | undefined>(undefined);
  // Keep the same operation identity and value when retrying an uncertain write.
  const pending = useRef<{ id: string; title: string; publishedAt: string } | undefined>(undefined);

  useEffect(() => {
    const controller = new AbortController();
    let stop: (() => void) | undefined;
    let live: Awaited<ReturnType<TerraScaleSyncV1Client["subscribe"]>> | undefined;
    void client.reactive.subscribe({
      databaseId,
      subscription: "starter-posts",
      query: postsQuery.sql,
      parameters: queryParameters,
      signal: controller.signal,
    }).then((next) => {
      if (controller.signal.aborted) { next.close(); return; }
      live = next;
      const render = () => {
        try { setRows(next.view.getVisibleItems().map(projectPost)); }
        catch { setError("The live result contains an invalid post. Check the collection schema."); }
      };
      stop = next.view.subscribe(render);
      setAttached(true);
      render();
    }).catch((cause: unknown) => {
      if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : String(cause));
    });
    return () => {
      controller.abort();
      writeController.current?.abort();
      stop?.();
      live?.close();
    };
  }, [client]);

  async function addPost(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (writeController.current) return;
    const form = event.currentTarget;
    const postTitle = String(new FormData(form).get("title") ?? "").trim();
    if (!postTitle) return;
    if (pending.current && pending.current.title !== postTitle) {
      setError("Retry the unchanged post to resolve the previous write before creating another.");
      return;
    }
    const operation = pending.current ?? { id: crypto.randomUUID(), title: postTitle, publishedAt: new Date().toISOString() };
    pending.current = operation;
    const controller = new AbortController();
    writeController.current = controller;
    setWriting(true);
    setError(undefined);
    setWriteStatus("");
    try {
      const result = await client.documents.put(databaseId, "posts", operation.id, {
        title: operation.title,
        authorId: "development-starter",
        publishedAt: operation.publishedAt,
      }, { durability: "regional", idempotencyKey: operation.id, signal: controller.signal });
      if (!result.ok) throw new Error(result.error.message);
      pending.current = undefined;
      if (!controller.signal.aborted) {
        form.reset();
        setWriteStatus("Post saved. The live query will show it in both tabs.");
      }
    } catch (cause: unknown) {
      if (!controller.signal.aborted) setError(`${cause instanceof Error ? cause.message : String(cause)} Retry the same post to resolve this write.`);
    } finally {
      writeController.current = undefined;
      if (!controller.signal.aborted) setWriting(false);
    }
  }

  return (
    <>
      <p aria-live="polite">{attached ? "Live query attached over CDN-backed HTTP" : "Connecting to the cloud branch…"}</p>
      <form onSubmit={addPost}>
        <label htmlFor="post-title">Post title</label>{" "}
        <input id="post-title" name="title" maxLength={200} required disabled={writing} />{" "}
        <button type="submit" disabled={!attached || writing}>{writing ? "Saving…" : "Add post"}</button>
      </form>
      <p aria-live="polite">{writeStatus}</p>
      {rows.length === 0 ? <p>No posts yet.</p> : <ul>{rows.map((post) => <li key={post.id}>{post.title}</li>)}</ul>}
      {error ? <p role="alert">{error}</p> : null}
    </>
  );
}
