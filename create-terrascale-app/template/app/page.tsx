"use client";

import { useEffect, useState } from "react";
import { TerraScale } from "@terrascale/sdk";

type Post = { id?: string; title: string; authorId: string };

const ts = new TerraScale({ apiKey: process.env.NEXT_PUBLIC_TERRASCALE_API_KEY! });

export default function Page() {
  const [rows, setRows] = useState<Post[]>([]);

  useEffect(() => {
    const sub = ts
      .from("posts")
      .where((c) => c.authorId.eq("mario"))
      .orderBy((c) => c.publishedAt.desc())
      .limit(20)
      .subscribe({
        onRows: (next) => setRows(next as unknown as Post[]),
        onError: (err) => console.error(err.message, err.fix),
      });
    return () => sub.unsubscribe();
  }, []);

  return (
    <main>
      <h1>Hello TerraScale</h1>
      <button
        onClick={() =>
          ts.from("posts").create({ title: "Hello TerraScale", authorId: "mario" })
        }
      >
        Add post
      </button>
      <ul>
        {rows.map((r, i) => (
          <li key={r.id ?? i}>{r.title}</li>
        ))}
      </ul>
    </main>
  );
}
