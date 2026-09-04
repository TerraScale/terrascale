import { defineConfig } from 'vitepress'

// TerraScale product docs — docs.terrascale.tech.
// Static build (npm run build), default theme, dark default. No design-system
// work here; product UI styling lives in ts-dashboard (D-80).
export default defineConfig({
  title: 'TerraScale',
  description:
    'Multi-region document database: leaderless CRDT replication, CDN-backed live queries, SQL you already know. Pay for writes and storage — reads, queries, egress, and delivery are free.',
  appearance: 'dark',
  lastUpdated: true,
  srcDir: '.',
  head: [['link', { rel: 'icon', href: '/favicon.svg', type: 'image/svg+xml' }]],
  themeConfig: {
    siteTitle: 'TerraScale',
    nav: [
      { text: 'Getting started', link: '/getting-started/' },
      {
        text: 'Concepts',
        items: [
          { text: 'The two engines', link: '/concepts/engines' },
          { text: 'Replication model', link: '/concepts/replication' },
          { text: 'Sync model', link: '/concepts/sync' },
          { text: 'SQL', link: '/concepts/sql' },
          { text: 'Pricing philosophy', link: '/concepts/pricing-philosophy' },
        ],
      },
      { text: 'Guides', link: '/guides/' },
      { text: 'Pricing & limits', link: '/pricing/' },
      { text: 'Snippets', link: '/snippets' },
      {
        text: 'Blog',
        items: [
          { text: 'Why TerraScale runs on SQLite', link: '/blog/why-sqlite' },
          { text: 'Reads are free: how', link: '/blog/reads-are-free' },
          { text: 'Benchmarks', link: '/blog/benchmarks' },
          { text: 'Replication design', link: '/blog/replication' },
          { text: 'A database per customer', link: '/blog/database-per-customer' },
        ],
      },
      {
        text: 'Legal',
        items: [
          { text: 'Terms of Service', link: '/legal/terms' },
          { text: 'Privacy Policy', link: '/legal/privacy' },
          { text: 'DPA', link: '/legal/dpa' },
          { text: 'Security', link: '/legal/security' },
          { text: 'Support', link: '/legal/support' },
        ],
      },
      { text: 'Changelog', link: '/changelog' },
    ],
    sidebar: {
      '/getting-started/': [
        {
          text: 'Getting started',
          items: [{ text: 'Quickstart', link: '/getting-started/' }],
        },
      ],
      '/concepts/': [
        {
          text: 'Concepts',
          items: [
            { text: 'The two engines', link: '/concepts/engines' },
            { text: 'Replication model', link: '/concepts/replication' },
            { text: 'Sync model', link: '/concepts/sync' },
            { text: 'SQL', link: '/concepts/sql' },
            { text: 'Pricing philosophy', link: '/concepts/pricing-philosophy' },
          ],
        },
      ],
      '/guides/': [
        { text: 'Guides', items: [{ text: 'Overview', link: '/guides/' }] },
      ],
      '/pricing/': [
        {
          text: 'Pricing & limits',
          items: [{ text: 'Plans, quotas, and guards', link: '/pricing/' }],
        },
      ],
      '/pricing': [
        {
          text: 'Pricing (L-1 draft, generated)',
          items: [{ text: 'Generated pricing sheet', link: '/pricing' }],
        },
      ],
      '/limits': [
        {
          text: 'Limits (generated)',
          items: [{ text: 'Generated limits page', link: '/limits' }],
        },
      ],
      '/snippets': [
        {
          text: 'Snippets',
          items: [{ text: 'Runnable snippets', link: '/snippets' }],
        },
      ],
      '/blog/': [
        {
          text: 'Launch posts (L-5 stubs)',
          items: [
            { text: 'Why TerraScale runs on SQLite', link: '/blog/why-sqlite' },
            { text: 'Reads are free: how', link: '/blog/reads-are-free' },
            { text: 'Benchmarks', link: '/blog/benchmarks' },
            { text: 'Replication design', link: '/blog/replication' },
            { text: 'A database per customer', link: '/blog/database-per-customer' },
          ],
        },
      ],
      '/legal/': [
        {
          text: 'Legal',
          items: [
            { text: 'Overview', link: '/legal/' },
            { text: 'Terms of Service', link: '/legal/terms' },
            { text: 'Privacy Policy', link: '/legal/privacy' },
            { text: 'DPA', link: '/legal/dpa' },
            { text: 'Security', link: '/legal/security' },
            { text: 'Support', link: '/legal/support' },
          ],
        },
      ],
    },
    outline: { level: [2, 3] },
    socialLinks: [
      { icon: 'github', link: 'https://git.terrascale.tech/TerraScale' },
      { icon: 'discord', link: 'https://discord.gg/terrascale' },
    ],
    footer: {
      message: 'Pre-launch — waitlist beta. Docs describe the product as it ships.',
      copyright: 'TerraScale',
    },
  },
})
