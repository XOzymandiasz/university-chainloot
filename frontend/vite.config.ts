import { fileURLToPath, URL } from 'node:url'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

export default defineConfig({
  plugins: [vue()],
  build: {
    rolldownOptions: {
      input: {
        main: fileURLToPath(new URL('./index.html', import.meta.url)),
        cluster: fileURLToPath(new URL('./cluster.html', import.meta.url)),
      },
    },
  },
  server: {
    proxy: {
      '/api': 'http://127.0.0.1:5000',
      '/hubs': { target: 'http://127.0.0.1:5000', ws: true },
      '/nodes/a': {
        target: 'http://127.0.0.1:5101',
        rewrite: (path) => path.replace(/^\/nodes\/a/, ''),
      },
      '/nodes/b': {
        target: 'http://127.0.0.1:5102',
        rewrite: (path) => path.replace(/^\/nodes\/b/, ''),
      },
      '/nodes/c': {
        target: 'http://127.0.0.1:5103',
        rewrite: (path) => path.replace(/^\/nodes\/c/, ''),
      },
    },
  },
})
