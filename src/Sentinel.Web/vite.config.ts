import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// В разработке API проксируется на сервер; в проде SPA лежит в wwwroot сервера.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
  build: { outDir: 'dist', sourcemap: false },
})
