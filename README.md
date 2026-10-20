# MD Codex

Visor y editor de archivos Markdown para Windows, hecho con WPF (.NET 10), WebView2, Markdig y AvalonEdit.

## Qué hace

- Abre archivos `.md` con **Ctrl+O**, arrastrándolos a la ventana o pasándolos como argumento (sirve para "Abrir con…").
- Cuatro modos: **Vista** (Ctrl+1), **Dividido** con editor y vista previa en vivo (Ctrl+2), **Editor** (Ctrl+3) y **Pro** (Ctrl+4). En modo dividido la vista sigue el scroll del editor.
- **Modo Pro**, al estilo de Notion: se edita directamente el documento formateado, con menú `/` para insertar bloques, asa para arrastrar bloques, barra flotante al seleccionar texto y atajos de Markdown (`# `, `- `, `> `…). Usa [Milkdown Crepe](https://milkdown.dev) y el archivo sigue siendo Markdown. Al editar en este modo, algunos detalles de formato pueden normalizarse (por ejemplo, la fila separadora de las tablas).
- Editor con resaltado de Markdown y números de línea. Nuevo (Ctrl+N), Guardar (Ctrl+S), Guardar como (Ctrl+Mayús+S), negrita (Ctrl+B) y cursiva (Ctrl+I).
- Avisa antes de cerrar o abrir otro archivo si hay cambios sin guardar, y conserva la codificación original al guardar.
- Si el archivo cambia fuera de la app, se recarga solo; si tienes cambios pendientes, pregunta primero. **F5** recarga desde el disco.
- Interfaz al estilo de VS Code: menús y controles en la barra de título, barra de actividad con **Explorador** (Ctrl+Mayús+E) y **Esquema** de títulos (Ctrl+Mayús+O), y barra de estado con posición, palabras, codificación y fin de línea. Ctrl+Mayús+B muestra u oculta la barra lateral.
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

Para generar un ejecutable:

```powershell
dotnet publish src\MdCodex -c Release -r win-x64 --self-contained false -o publicar
```

Requiere el runtime de WebView2, que ya viene incluido en Windows 10 y 11.

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
