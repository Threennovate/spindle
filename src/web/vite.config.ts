/// <reference types="vitest/config" />
import tailwindcss from '@tailwindcss/vite'
import { tanstackRouter } from '@tanstack/router-plugin/vite'
import react from '@vitejs/plugin-react'
import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'

const api = 'http://localhost:5258'

export default defineConfig({
  // The router plugin generates src/routeTree.gen.ts from src/routes and must run before React.
  plugins: [tanstackRouter({ target: 'react', autoCodeSplitting: true }), react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    strictPort: true,
    // Same origin as the API, as in production, so the SameSite=Strict session cookie works.
    // WebSockets too, for live sync.
    // And the MCP server with its OAuth endpoints, so agents can connect to the dev server's address.
    proxy: {
      '/api': { target: api, ws: true },
      '/health': api,
      '/mcp': api,
      '/oauth/authorize': api,
      '/oauth/register': api,
      '/oauth/token': api,
      '/oauth/revoke': api,
      '/.well-known/oauth-protected-resource': api,
      '/.well-known/oauth-authorization-server': api,
    },
  },
  test: {
    include: ['src/**/*.test.ts'],
  },
})
