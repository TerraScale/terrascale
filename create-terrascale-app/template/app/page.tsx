"use client";

import { useEffect, useState } from "react";
import { createTerraScaleClient, TerraScaleSyncV2Client } from "@terrascale/sdk";

type Post = { id: string; title?: string; authorId?: string };

const databaseId = process.env.NEXT_PUBLIC_TERRASCALE_DATABASE_ID!;
const api = createTerraScaleClient({
  apiBaseUrl: process.env.NEXT_PUBLIC_TERRASCALE_API_URL,
  projectId: process.env.NEXT_PUBLIC_TERRASCALE_PROJECT_ID,
  accessToken: process.env.NEXT_PUBLIC_TERRASCALE_API_KEY,
});
const sync = new TerraScaleSyncV2Client({
  apiBaseUrl: process.env.NEXT_PUBLIC_TERRASCALE_API_URL,
  accessToken: process.env.NEXT_PUBLIC_TERRASCALE_API_KEY,
});

function decodeJson(bytes: Uint8Array | undefined): unknown {
  if (bytes === undefined) return undefined;
  return JSON.parse(new TextDecoder().decode(bytes));
}

export default function Page() {
  const [rows, setRows] = useState<Post[]>([]);
  const [databaseReady, setDatabaseReady] = useState(false);

  useEffect(() => {
    void api.databases.get(databaseId).then((result) => setDatabaseReady(result.ok)).catch((error) => console.error(error));
    let closed = false;
    let stop: (() => void) | undefined;
    let live: Awaited<ReturnType<TerraScaleSyncV2Client["subscribe"]>> | undefined;

    void sync.subscribe({
      databaseId,
      subscription: "starter-posts",
      query: "SELECT * FROM posts ORDER BY publishedAt DESC LIMIT 20",
      parameters: new Uint8Array(),
    }).then((next) => {
      live = next;
      if (closed) {
        next.close();
        return;
      }
      const render = () => {
        setRows(next.view.getVisibleItems().map((item) => ({
          id: item.identity,
          title: decodeJson(item.value.title) as string | undefined,
          authorId: decodeJson(item.value.authorId) as string | undefined,
        })));
      };
      stop = next.view.subscribe(render);
      render();
    }).catch((error) => console.error(error));

    return () => {
      closed = true;
      stop?.();
      live?.close();
    };
  }, []);

  return (
    <main>
      <h1>Hello TerraScale</h1>
      <p>{databaseReady ? "Connected" : "Connecting"} to database {databaseId}.</p>
      <ul>
        {rows.map((r, i) => (
          <li key={r.id ?? i}>{r.title}</li>
        ))}
      </ul>
    </main>
  );
}
