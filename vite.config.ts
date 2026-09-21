import { sveltekit } from '@sveltejs/kit/vite'
import tailwindcss from '@tailwindcss/vite'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  // Dev and preview servers must stay reachable over the tailnet with HMR (see
  // AGENTS.md): bind every interface and accept Tailscale MagicDNS hostnames.
  server: {
    host: true,
    allowedHosts: ['.ts.net'],
  },
  preview: {
    host: true,
    allowedHosts: ['.ts.net'],
  },
})
