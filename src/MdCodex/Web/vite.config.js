import { defineConfig } from 'vite';

// El resultado se guarda en el proyecto de la app y se publica junto al .exe (carpeta Pro).
export default defineConfig({
  base: './',
  build: {
    outDir: '../Pro',
    emptyOutDir: true,
    assetsDir: 'recursos',
    target: 'es2022',
    chunkSizeWarningLimit: 4000,
  },
});
