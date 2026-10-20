// Copia las librerías de la vista previa (ya compiladas para el navegador) a src/MdCodex/Vista,
// desde donde la app las sirve sin internet. Uso: npm run vista
import { cpSync, mkdirSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const raiz = join(dirname(fileURLToPath(import.meta.url)), '..');
const destino = join(raiz, '..', 'MdCodex', 'Vista');
const modulos = join(raiz, 'node_modules');

const archivos = [
  // Resaltado de código: lenguajes comunes + algunos frecuentes en Windows.
  ['@highlightjs/cdn-assets/highlight.min.js', 'highlight.min.js'],
  ['@highlightjs/cdn-assets/languages/powershell.min.js', 'highlight-powershell.min.js'],
  ['@highlightjs/cdn-assets/languages/dockerfile.min.js', 'highlight-dockerfile.min.js'],
  ['@highlightjs/cdn-assets/styles/github.min.css', 'highlight-claro.min.css'],
  ['@highlightjs/cdn-assets/styles/github-dark.min.css', 'highlight-oscuro.min.css'],
];

rmSync(destino, { recursive: true, force: true });
mkdirSync(destino, { recursive: true });
for (const [origen, nombre] of archivos) cpSync(join(modulos, origen), join(destino, nombre));
console.log(`Vista: ${archivos.length} archivos en ${destino}`);
