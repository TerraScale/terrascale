import path from 'node:path'
import adapter from '@sveltejs/adapter-static'
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte'
import { sveltekit } from '@sveltejs/kit/vite'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vite'

// Dev and preview servers must stay reachable over the tailnet with HMR (see
// AGENTS.md). Set TAILNET_HOST to the machine's 100.x.y.z tailnet address to
// bind that interface only; when unset the server binds every interface, which
// still keeps it reachable over the tailnet without pinning an address here.
const host = process.env.TAILNET_HOST ?? true
const allowedHosts = ['.ts.net']

export default defineConfig({
  plugins: [tailwindcss(), sveltekit({
    adapter: adapter(),
    preprocess: vitePreprocess(),
  })],
  resolve: { alias: { '@': path.resolve('./src') } },
  server: { host, allowedHosts },
  preview: { host, allowedHosts },
})
