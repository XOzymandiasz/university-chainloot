import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      '/nodes/a': { target: 'http://127.0.0.1:5101', rewrite: (path) => path.replace(/^\/nodes\/a/, '') },
      '/nodes/b': { target: 'http://127.0.0.1:5102', rewrite: (path) => path.replace(/^\/nodes\/b/, '') },
      '/nodes/c': { target: 'http://127.0.0.1:5103', rewrite: (path) => path.replace(/^\/nodes\/c/, '') },
    },
  },
})
