import react from '@vitejs/plugin-react'
import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [react()],
  server: {
    host: '127.0.0.1',
    port: 5178,
    strictPort: true,
    fs: { allow: [fileURLToPath(new URL('../..', import.meta.url))] },
    proxy: {
      '/api': 'http://127.0.0.1:8088',
      '/health': 'http://127.0.0.1:8088',
    },
  },
})
