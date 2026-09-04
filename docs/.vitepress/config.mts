import { defineConfig } from "vitepress";

export default defineConfig({
  title: "TerraScale",
  description: "Multi-region document database. Pay for writes and storage; reads are free.",
  lang: "en-US",
  themeConfig: {
    nav: [
      { text: "Guide", link: "/getting-started" },
      { text: "Pricing", link: "/pricing" },
      { text: "Limits", link: "/limits" },
      { text: "Blog", link: "/blog/why-sqlite" },
    ],
    sidebar: [
      {
        text: "Docs",
        items: [
          { text: "Home", link: "/" },
          { text: "Getting started", link: "/getting-started" },
          { text: "Concepts", link: "/concepts" },
          { text: "Runnable snippets", link: "/snippets" },
          { text: "Pricing", link: "/pricing" },
          { text: "Limits", link: "/limits" },
        ],
      },
      {
        text: "Launch posts (L-5 stubs)",
        items: [
          { text: "Why TerraScale runs on SQLite", link: "/blog/why-sqlite" },
          { text: "Reads are free: how", link: "/blog/reads-are-free" },
          { text: "Benchmarks", link: "/blog/benchmarks" },
          { text: "Replication design", link: "/blog/replication" },
          { text: "A database per customer", link: "/blog/database-per-customer" },
        ],
      },
    ],
    socialLinks: [{ icon: "github", link: "https://terrascale.tech" }],
  },
});
