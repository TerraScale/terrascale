"use client";

import { useEffect, useMemo, useRef, useState, type FormEvent } from "react";
import { createTerraScaleClient, Direction, select, type TerraScaleSyncV1Client } from "@terrascale/sdk";
import { authorId, posts, publishedAt, title } from "../schema";

type Post = { id: string; title: string };
const databaseId = process.env.NEXT_PUBLIC_TERRASCALE_DATABASE_ID?.trim() ?? "";
const apiBaseUrl = process.env.NEXT_PUBLIC_TERRASCALE_API_URL ?? "https://api.terrascale.tech";
const projectId = process.env.NEXT_PUBLIC_TERRASCALE_PROJECT_ID?.trim() ?? "";
const authBaseUrl = process.env.NEXT_PUBLIC_TERRASCALE_AUTH_URL ?? "https://app.terrascale.tech";
const redirectUri = process.env.NEXT_PUBLIC_TERRASCALE_REDIRECT_URI;
const oauthClientId = "terrascale-starter-web";
const oauthScopes = ["openid", "database:read", "database:write"] as const;
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
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string>();
  const callbackHandled = useRef(false);

  useEffect(() => {
    // React StrictMode replays effects in development; a code is single-use.
    if (callbackHandled.current) return;
    const query = new URLSearchParams(window.location.search);
    const code = query.get("code");
    const state = query.get("state");
    const oauthError = query.get("error");
    if (!code && !oauthError) return;
    callbackHandled.current = true;
    window.history.replaceState({}, document.title, window.location.pathname);
    const expectedState = sessionStorage.getItem("terrascale.oauth.state");
    const verifier = sessionStorage.getItem("terrascale.oauth.verifier");
    sessionStorage.removeItem("terrascale.oauth.state");
    sessionStorage.removeItem("terrascale.oauth.verifier");
    if (!expectedState || state !== expectedState || !verifier) {
      setAuthError("The sign-in response could not be verified. Please try again.");
      return;
    }
    if (oauthError) {
      setAuthError(query.get("error_description") ?? "Sign-in was declined. Please try again.");
      return;
    }
    if (!code) {
      setAuthError("Sign-in did not return an authorization code. Please try again.");
      return;
    }
    setAuthenticating(true);
    void exchangeAuthorizationCode(code, verifier).then((token) => {
      setCredential(token);
    }).catch((cause: unknown) => {
      setAuthError(cause instanceof Error ? cause.message : "Sign-in failed.");
    }).finally(() => setAuthenticating(false));
  }, []);

  async function signIn() {
    setAuthError(undefined);
    setAuthenticating(true);
    try {
      const verifier = randomString(32);
      const state = randomString(24);
      sessionStorage.setItem("terrascale.oauth.verifier", verifier);
      sessionStorage.setItem("terrascale.oauth.state", state);
      const challenge = await pkceChallenge(verifier);
      const callback = redirectUri ?? `${window.location.origin}${window.location.pathname}`;
      const authorize = new URL("/api/auth/oauth2/authorize", authBaseUrl);
      authorize.search = new URLSearchParams({
        client_id: oauthClientId,
        response_type: "code",
        redirect_uri: callback,
        scope: oauthScopes.join(" "),
        resource: apiBaseUrl,
        state,
        code_challenge: challenge,
        code_challenge_method: "S256",
      }).toString();
      window.location.assign(authorize);
    } catch (cause: unknown) {
      setAuthError(cause instanceof Error ? cause.message : "Sign-in could not be started.");
      setAuthenticating(false);
    }
  }
  return (
    <main style={{ maxWidth: 720, margin: "0 auto", padding: "4rem 1.5rem", fontFamily: "system-ui, sans-serif" }}>
      <p>TerraScale development starter</p>
      <h1>Posts that stay in sync.</h1>
      <p>Connect two browser tabs to the same cloud branch, then add a post in either tab.</p>
      {!databaseId || !projectId ? <p role="alert">Set NEXT_PUBLIC_TERRASCALE_DATABASE_ID and NEXT_PUBLIC_TERRASCALE_PROJECT_ID in .env.local and restart the development server.</p> : credential ? (
        <>
          <button onClick={() => setCredential("")}>Disconnect and clear credential</button>
          <LivePosts key={credential} credential={credential} />
        </>
      ) : (
        <>
          <p>Sign in to TerraScale to authorize this starter to read and write posts in your development database.</p>
          <button onClick={() => void signIn()} disabled={authenticating}>{authenticating ? "Signing in…" : "Sign in with TerraScale"}</button>
          {authError ? <p role="alert">{authError}</p> : null}
        </>
      )}
    </main>
  );
}

function randomString(bytes: number): string {
  const value = new Uint8Array(bytes);
  crypto.getRandomValues(value);
  return base64url(value);
}

async function pkceChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64url(new Uint8Array(digest));
}

function base64url(value: Uint8Array): string {
  let binary = "";
  for (const byte of value) binary += String.fromCharCode(byte);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/u, "");
}

async function exchangeAuthorizationCode(code: string, verifier: string): Promise<string> {
  const callback = redirectUri ?? `${window.location.origin}${window.location.pathname}`;
  const response = await fetch(new URL("/api/auth/oauth2/token", authBaseUrl), {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({
      grant_type: "authorization_code",
      client_id: oauthClientId,
      code,
      redirect_uri: callback,
      code_verifier: verifier,
      resource: apiBaseUrl,
    }),
  });
  const body = await response.json() as { access_token?: unknown; error_description?: unknown };
  if (!response.ok || typeof body.access_token !== "string") {
    throw new Error(typeof body.error_description === "string" ? body.error_description : "The authorization code could not be exchanged.");
  }
  return body.access_token;
}

// Renew the short-lived, branch-scoped credential without storing either token.
// Concurrent reads and writes share one exchange; a failed exchange is retryable.
function starterAccessToken(credential: string): () => Promise<string> {
  let cached: { token: string; renewAt: number } | undefined;
  let pending: Promise<string> | undefined;
  return async () => {
    if (cached && Date.now() < cached.renewAt) return cached.token;
    if (pending) return pending;
    pending = (async () => {
      const response = await fetch(new URL("/api/auth/web-starter/delegation", authBaseUrl), {
        method: "POST",
        headers: { "content-type": "application/json", authorization: `Bearer ${credential}` },
        body: JSON.stringify({ projectId, databaseId, collection: "posts", actions: ["read", "write"] }),
      });
      const body = await response.json() as { accessToken?: unknown; expiresIn?: unknown; message?: unknown };
      if (!response.ok || typeof body.accessToken !== "string" || !body.accessToken ||
          typeof body.expiresIn !== "number" || !Number.isFinite(body.expiresIn) || body.expiresIn <= 0) {
        throw new Error(typeof body.message === "string" ? body.message : "Database authorization failed. Verify access to this project and branch, or sign in again.");
      }
      cached = { token: body.accessToken, renewAt: Date.now() + Math.max(0, body.expiresIn - 10) * 1000 };
      return cached.token;
    })();
    try { return await pending; }
    finally { pending = undefined; }
  };
}

function LivePosts({ credential }: { credential: string }) {
  const client = useMemo(() => createTerraScaleClient({
    apiBaseUrl,
    projectId,
    accessToken: starterAccessToken(credential),
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
      // Each effect owns its attach, including StrictMode effect replays.
      subscription: `starter-posts-${crypto.randomUUID()}`,
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
