import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

// The dashboard is a static SPA that talks only to the mock server's /__admin/* REST API.
// In dev, proxy those calls to a running Mockifyr host (default :8080). In prod the host always
// serves it under the reserved /__mockifyr prefix, so the base is pinned to that prefix. This is
// load-bearing: the SPA router derives its basename from import.meta.env.BASE_URL, and a relative
// base ('./') collapses that basename to '.', which matches no route and blanks the page under the
// prefix. A fixed base makes both the asset URLs and the router basename resolve correctly.
export default defineConfig({
  base: '/__mockifyr/',
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  server: {
    port: 5173,
    proxy: {
      '/__admin': { target: 'http://localhost:8080', changeOrigin: true },
      // Test-request runner (#203): in dev the SPA and the mock host are different origins, so mock
      // traffic tunnels through this prefix. Embedded builds fetch the path directly (same origin).
      // Regex key with a mandatory trailing slash — a bare '/__mock' prefix would also swallow the
      // SPA's own /__mockifyr/* routes and blank the app.
      '^/__mock/': { target: 'http://localhost:8080', changeOrigin: true, rewrite: (p) => p.replace(/^\/__mock/, '') },
    },
  },
})
