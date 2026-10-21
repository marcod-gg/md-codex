# MD Codex

Visor y editor de archivos Markdown para Windows, hecho con WPF (.NET 10), WebView2, Markdig y AvalonEdit.

## Qué hace

- Abre archivos `.md` con **Ctrl+O**, arrastrándolos a la ventana o pasándolos como argumento (sirve para "Abrir con…").
- Cuatro modos: **Vista** (Ctrl+1), **Dividido** con editor y vista previa en vivo (Ctrl+2), **Editor** (Ctrl+3) y **Pro** (Ctrl+4). En modo dividido la vista sigue el scroll del editor.
- **Modo Pro**, al estilo de Notion: se edita directamente el documento formateado, con menú `/` para insertar bloques, asa para arrastrar bloques, barra flotante al seleccionar texto y atajos de Markdown (`# `, `- `, `> `…). Usa [Milkdown Crepe](https://milkdown.dev) y el archivo sigue siendo Markdown. Al editar en este modo, algunos detalles de formato pueden normalizarse (por ejemplo, la fila separadora de las tablas).
- **Imágenes en el modo Pro**: con `/` → *Imagen* (elegir archivo) o *Imagen desde URL*, pegando con **Ctrl+V** (por ejemplo, una captura) o arrastrándolas al editor. Se copian a `imagenes/` junto al documento (PNG, JPG, GIF, WebP o SVG, hasta 20 MB) y se enlazan con una ruta relativa; las que ya están dentro de la carpeta del documento no se copian.
- **Vista previa**: resaltado de sintaxis en los bloques de código y diagramas [Mermaid](https://mermaid.js.org) en los bloques ` ```mermaid `.
- Editor con resaltado de Markdown y números de línea. Nuevo (Ctrl+N), Guardar (Ctrl+S), Guardar como (Ctrl+Mayús+S), negrita (Ctrl+B) y cursiva (Ctrl+I).
- Avisa antes de cerrar o abrir otro archivo si hay cambios sin guardar, y conserva la codificación original al guardar.
- Si el archivo cambia fuera de la app, se recarga solo; si tienes cambios pendientes, pregunta primero. **F5** recarga desde el disco.
- Interfaz al estilo de VS Code: menús y controles en la barra de título, barra de actividad con **Explorador** (Ctrl+Mayús+E) y **Esquema** de títulos (Ctrl+Mayús+O), y barra de estado con posición, palabras, codificación y fin de línea. El botón al pie de la barra de actividad (o Ctrl+Mayús+B) deja la barra lateral **fija** o **automática**: oculta hasta tocar un ícono, se despliega sobre el documento y se vuelve a ocultar al hacer clic fuera.
- **Pestañas**: cada documento en su pestaña, con su propio deshacer, cursor y scroll. Un clic en el Explorador abre una pestaña temporal (en cursiva) que se fija al editar o con doble clic. Ctrl+W cierra, Ctrl+Tab / Ctrl+Mayús+Tab cambian de pestaña, clic central cierra y el clic derecho ofrece más opciones.
- **Ctrl+F** busca dentro del editor. Arrastrar una carpeta a la ventana la abre en el Explorador.
- Sigue el tema claro u oscuro de Windows.
- Muestra imágenes y enlaces relativos; los enlaces a otros `.md` se abren en el mismo visor y el resto, en la aplicación predeterminada.
- Soporta tablas, listas de tareas, notas al pie y emojis. Para hacer zoom, usa **Ctrl + rueda del mouse**.

## Uso

```powershell
dotnet run --project src\MdCodex -- docs\ejemplo.md
```

O, después de compilar con `dotnet build`, ejecutar directamente `src\MdCodex\bin\Debug\net10.0-windows10.0.19041.0\MdCodex.exe` (también se le pueden arrastrar archivos `.md` encima). Hay que cerrar la app antes de volver a compilar, porque Windows bloquea el `.exe` mientras está abierto.

### Instalador

```powershell
.\instalador\construir.ps1   # genera publicar\MDCodex-<versión>-instalador.exe
```

Publica la app con el runtime de .NET incluido y la empaqueta con [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`). El instalador:

- Instala por usuario, sin pedir administrador (o para todos, si se elige en el asistente), con acceso en el menú Inicio y, opcionalmente, en el escritorio.
- Registra MD Codex en **Abrir con…** para `.md` y `.markdown`. Windows no permite que una app se ponga sola como predeterminada; al terminar ofrece abrir la configuración para elegirla.
- Avisa si falta el runtime de WebView2 (viene con Windows 11 y con Edge en Windows 10).
- Requiere Windows 10 versión 2004 o posterior, de 64 bits.

El ícono de la app se genera con `marca\generar-icono-app.ps1`.

### Editor del modo Pro

El editor web vive en `src\MdCodex.Pro` y su compilación ya está incluida en `src\MdCodex\Pro`, así que para compilar la app no hace falta Node.js. Solo si se modifica el editor:

```powershell
cd src\MdCodex.Pro
npm install
npm run build   # regenera src\MdCodex\Pro
```

## Repositorio

- Código: [github.com/marcod-gg/md-codex](https://github.com/marcod-gg/md-codex)
- Problemas y sugerencias: [issues](https://github.com/marcod-gg/md-codex/issues)

---

Desarrollado por **Marco González G.** · skolldev
