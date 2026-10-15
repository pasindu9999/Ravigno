import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// /api/x is forwarded to the API as /x, so the browser sees one origin and the API needs no CORS.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5053',
        changeOrigin: true,
        rewrite: (path) => path.replace(/^\/api/, ''),
      },
    },
  },
})
