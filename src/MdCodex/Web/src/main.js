// Editor "Pro" de MD Codex: edición por bloques sobre Milkdown Crepe.
// La app es la dueña del documento: le envía el Markdown a cargar y recibe los cambios.
import { Crepe } from '@milkdown/crepe';
import { commandsCtx, editorViewCtx, remarkStringifyOptionsCtx, serializerCtx } from '@milkdown/kit/core';
import { undoCommand, redoCommand } from '@milkdown/kit/plugin/history';
import { clearTextInCurrentBlockCommand, toggleEmphasisCommand, toggleStrongCommand } from '@milkdown/kit/preset/commonmark';
import { remarkGFMPlugin } from '@milkdown/kit/preset/gfm';
import { TextSelection } from '@milkdown/kit/prose/state';
import { callCommand, insert, replaceAll } from '@milkdown/kit/utils';
// Las partes del tema que se usan; latex.css se omite porque arrastra ~1 MB de fuentes de KaTeX.
import '@milkdown/crepe/theme/common/prosemirror.css';
import '@milkdown/crepe/theme/common/reset.css';
import '@milkdown/crepe/theme/common/block-edit.css';
import '@milkdown/crepe/theme/common/code-mirror.css';
import '@milkdown/crepe/theme/common/cursor.css';
import '@milkdown/crepe/theme/common/image-block.css';
import '@milkdown/crepe/theme/common/link-tooltip.css';
import '@milkdown/crepe/theme/common/list-item.css';
import '@milkdown/crepe/theme/common/placeholder.css';
import '@milkdown/crepe/theme/common/toolbar.css';
import '@milkdown/crepe/theme/common/table.css';
import '@milkdown/crepe/theme/frame.css';
import './estilo.css';

const raiz = document.getElementById('editor');
const anfitrion = window.chrome?.webview;

let pestana = 0;
// Markdown que ya conoce la app; solo se avisa cuando el editor tiene algo distinto.
let ultimo = '';
let demora;

// Las rutas relativas (imágenes) se resuelven contra la carpeta del documento, que la app sirve
// en una URL propia. El <base> se crea después de cargar los scripts, así no los afecta.
const baseDocumento = document.head.appendChild(document.createElement('base'));
function fijarBase(url) {
  if (url) baseDocumento.setAttribute('href', url);
  else baseDocumento.removeAttribute('href');
}

const iconoImagen = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><rect x="3.5" y="4.5" width="17" height="15" rx="2.5"/><circle cx="9" cy="10" r="1.6"/><path d="m20 16-4.5-4.5L7 19.5"/></svg>';
const iconoEnlace = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><path d="M10 14a4 4 0 0 0 5.66 0l3-3a4 4 0 0 0-5.66-5.66l-1 1"/><path d="M14 10a4 4 0 0 0-5.66 0l-3 3a4 4 0 0 0 5.66 5.66l1-1"/></svg>';

// Imágenes en el menú «/». Los archivos los copia la app junto al documento (la página no puede
// escribir en disco) y luego le pide al editor que inserte la imagen con «insertarImagen».
function agregarImagenesAlMenu(builder) {
  const avanzado = builder.getGroup('advanced');
  avanzado.addItem('imagen', {
    label: 'Imagen',
    icon: iconoImagen,
    onRun: ctx => {
      ctx.get(commandsCtx).call(clearTextInCurrentBlockCommand.key);
      anfitrion?.postMessage({ tipo: 'elegirImagen', id: pestana });
    },
  });
  avanzado.addItem('imagen-url', {
    label: 'Imagen desde URL',
    icon: iconoEnlace,
    onRun: ctx => {
      ctx.get(commandsCtx).call(clearTextInCurrentBlockCommand.key);
      const url = prompt('Dirección o ruta de la imagen:');
      if (url?.trim()) insertarImagen({ ruta: url.trim(), alt: prompt('Texto alternativo (describe la imagen):') ?? '' });
    },
  });
}

// Inserta una imagen normal de Markdown en el cursor o, si viene una posición (arrastrar y soltar), ahí.
function insertarImagen({ ruta, alt = '', x, y }) {
  crepe.editor.action(ctx => {
    const vista = ctx.get(editorViewCtx);
    let tr = vista.state.tr;
    if (x != null && y != null) {
      const destino = vista.posAtCoords({ left: x, top: y });
      if (destino) tr = tr.setSelection(TextSelection.near(tr.doc.resolve(destino.pos)));
    }
    const imagen = vista.state.schema.nodes.image.create({ src: ruta, alt, title: '' });
    vista.dispatch(tr.replaceSelectionWith(imagen, false).scrollIntoView());
    vista.focus();
  });
}

const crepe = new Crepe({
  root: raiz,
  defaultValue: '',
  features: {
    [Crepe.Feature.Latex]: false,
    [Crepe.Feature.AI]: false,
    [Crepe.Feature.TopBar]: false,
    // El bloque de imagen guarda su tamaño en el texto alternativo (![1.00](...)) y eso daña el
    // Markdown. Sin él, las imágenes son las normales de Markdown y conservan su texto.
    [Crepe.Feature.ImageBlock]: false,
  },
  featureConfigs: {
    [Crepe.Feature.Placeholder]: { text: 'Escribe, o usa «/» para insertar un bloque…', mode: 'block' },
    [Crepe.Feature.BlockEdit]: {
      textGroup: {
        label: 'Texto',
        text: { label: 'Texto' },
        h1: { label: 'Título 1' }, h2: { label: 'Título 2' }, h3: { label: 'Título 3' },
        h4: { label: 'Título 4' }, h5: { label: 'Título 5' }, h6: { label: 'Título 6' },
        quote: { label: 'Cita' },
        divider: { label: 'Separador' },
      },
      listGroup: {
        label: 'Listas',
        bulletList: { label: 'Lista con viñetas' },
        orderedList: { label: 'Lista numerada' },
        taskList: { label: 'Lista de tareas' },
      },
      advancedGroup: {
        label: 'Avanzado',
        image: null,
        codeBlock: { label: 'Código' },
        table: { label: 'Tabla' },
        math: null,
      },
      buildMenu: agregarImagenesAlMenu,
    },
    [Crepe.Feature.Toolbar]: {
      boldLabel: 'Negrita', italicLabel: 'Cursiva', strikethroughLabel: 'Tachado',
      codeLabel: 'Código', linkLabel: 'Enlace',
    },
  },
});

// Estilo de Markdown al guardar, lo más parecido a como se escribe a mano.
crepe.editor.config(ctx => {
  ctx.update(remarkStringifyOptionsCtx, previas => ({
    ...previas,
    bullet: '-',
    emphasis: '*',
    strong: '*',
    rule: '-',
    fences: true,
    listItemIndent: 'one',
  }));
  // Tablas sin columnas alineadas con espacios: cambiar una celda no reescribe toda la tabla.
  ctx.update(remarkGFMPlugin.options.key, previas => ({ ...previas, tablePipeAlign: false }));
});

crepe.on(api => api.markdownUpdated(() => {
  clearTimeout(demora);
  demora = setTimeout(avisar, 250);
}));

// Envía a la app el Markdown actual si cambió.
function avisar() {
  clearTimeout(demora);
  const cambio = pendiente();
  if (cambio) anfitrion?.postMessage({ tipo: 'cambio', ...cambio });
}

function pendiente() {
  const markdown = crepe.getMarkdown();
  if (markdown === ultimo) return null;
  ultimo = markdown;
  return { id: pestana, markdown };
}

// Lo seleccionado, como Markdown (para copiar y cortar desde el menú de la app).
function seleccionEnMarkdown(ctx) {
  const { state } = ctx.get(editorViewCtx);
  if (state.selection.empty) return '';
  const doc = state.schema.topNodeType.createAndFill(undefined, state.selection.content().content);
  return doc ? ctx.get(serializerCtx)(doc) : '';
}

// Acciones del menú Editar de la app, que en este modo no llegan como teclas. Copiar y cortar devuelven
// la selección en Markdown (la app la deja en el portapapeles); pegar inserta Markdown.
function accion(nombre, dato) {
  const editor = crepe.editor;
  let resultado = null;
  switch (nombre) {
    case 'negrita': editor.action(callCommand(toggleStrongCommand.key)); break;
    case 'cursiva': editor.action(callCommand(toggleEmphasisCommand.key)); break;
    case 'deshacer': editor.action(callCommand(undoCommand.key)); break;
    case 'rehacer': editor.action(callCommand(redoCommand.key)); break;
    case 'pegar': editor.action(insert(dato ?? '')); break;
    case 'copiar':
    case 'cortar':
      resultado = editor.action(ctx => {
        const texto = seleccionEnMarkdown(ctx);
        if (nombre === 'cortar' && texto) {
          const vista = ctx.get(editorViewCtx);
          vista.dispatch(vista.state.tr.deleteSelection());
        }
        return texto;
      });
      break;
  }
  raiz.querySelector('.ProseMirror')?.focus();
  return resultado;
}

// La app lo llama antes de guardar o cerrar, para no perder lo último que se escribió.
window.mdcodex = { pendiente: () => { clearTimeout(demora); return pendiente(); }, accion };

function cargar(m) {
  avisar(); // lo pendiente de la pestaña anterior
  pestana = m.id;
  fijarBase(m.base);
  crepe.editor.action(replaceAll(m.markdown, true));
  // Lo que el editor produce al cargar (ya normalizado) no cuenta como cambio del usuario.
  ultimo = crepe.getMarkdown();
  scrollTo(0, 0);
}

function irATitulo(indice) {
  const titulos = raiz.querySelectorAll('.ProseMirror :is(h1, h2, h3, h4, h5, h6)');
  titulos[indice]?.scrollIntoView({ block: 'start', behavior: 'smooth' });
}

anfitrion?.addEventListener('message', e => {
  const m = e.data;
  if (m.tipo === 'cargar') cargar(m);
  else if (m.tipo === 'titulo') irATitulo(m.indice);
  else if (m.tipo === 'enfocar') raiz.querySelector('.ProseMirror')?.focus();
  else if (m.tipo === 'insertarImagen' && m.id === pestana) insertarImagen(m);
  else if (m.tipo === 'ancho') {
    document.body.classList.toggle('completo', m.completo);
    // El primer ancho llega al cargar y se aplica de golpe; los cambios siguientes se animan.
    void document.body.offsetWidth;
    document.body.classList.add('animado');
  }
});

await crepe.create();
anfitrion?.postMessage({ tipo: 'listo' });
