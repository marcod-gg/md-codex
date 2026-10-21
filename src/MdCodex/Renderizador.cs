using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdCodex;

public sealed record Titulo(string Texto, int Nivel, int Linea)
{
    public Thickness Sangria => new((Nivel - 1) * 12, 0, 0, 0);
}

public sealed record Resultado(string Html, IReadOnlyList<Titulo> Titulos, int Palabras);

// Convierte Markdown en HTML. La página se carga una sola vez (Plantilla) y luego se le envía
// solo el contenido, así la vista previa no parpadea ni pierde el scroll mientras se escribe.
public static partial class Renderizador
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // tablas, listas de tareas, notas al pie, ids en títulos, etc.
        .UseEmojiAndSmiley()
        .Build();

    // Una sola pasada da el HTML, los títulos para el esquema y el conteo de palabras.
    // Cada bloque lleva su línea de origen (data-line) para sincronizar el scroll con el editor.
    public static Resultado Procesar(string markdown)
    {
        var documento = Markdown.Parse(markdown, Pipeline);
        var titulos = new List<Titulo>();
        var palabras = 0;
        foreach (var bloque in documento.Descendants<Block>())
        {
            bloque.GetAttributes().AddProperty("data-line", bloque.Line.ToString(CultureInfo.InvariantCulture));
            if (bloque is not LeafBlock { Inline: { } inline })
                continue;

            var texto = TextoPlano(inline);
            palabras += Palabra().Count(texto);
            if (bloque is HeadingBlock titulo)
                titulos.Add(new Titulo(texto, titulo.Level, titulo.Line));
        }
        return new Resultado(documento.ToHtml(Pipeline), titulos, palabras);
    }

    private static string TextoPlano(ContainerInline inline)
    {
        var texto = new StringBuilder();
        foreach (var parte in inline.Descendants())
        {
            if (parte is LiteralInline literal)
                texto.Append(literal.Content);
            else if (parte is CodeInline codigo)
                texto.Append(codigo.Content);
        }
        return texto.ToString().Trim();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*")]
    private static partial Regex Palabra();

    public static string Bienvenida() => $"""
        <div class="vacio">
          <h1>MD Codex</h1>
          <p>Arrastra un archivo <code>.md</code> a esta ventana o presiona <kbd>Ctrl</kbd>+<kbd>O</kbd> para abrir uno.</p>
          <p class="nota"><kbd>Ctrl</kbd>+<kbd>N</kbd> crea un documento nuevo · <kbd>Ctrl</kbd>+<kbd>2</kbd> muestra editor y vista lado a lado</p>
        </div>
        <footer class="marca">
          <span class="lobo" style="--logo: url({Marca.LogoDataUri})"></span>
          <span>Desarrollado por <strong>{Marca.Autor}</strong> · <a href="{Marca.Repositorio}" title="Abrir el repositorio en GitHub">GitHub</a></span>
        </footer>
        """;

    public static string Plantilla() => $$"""
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <base>
        <title>MD Codex</title>
        <link rel="stylesheet" href="https://{{Documentos.HostRecursos}}/highlight-claro.min.css" media="(prefers-color-scheme: light)">
        <link rel="stylesheet" href="https://{{Documentos.HostRecursos}}/highlight-oscuro.min.css" media="(prefers-color-scheme: dark)">
        <style>{{Estilo}}</style>
        <script src="https://{{Documentos.HostRecursos}}/highlight.min.js"></script>
        <script src="https://{{Documentos.HostRecursos}}/highlight-powershell.min.js"></script>
        <script src="https://{{Documentos.HostRecursos}}/highlight-dockerfile.min.js"></script>
        </head>
        <body>
        <main></main>
        <script>
          const main = document.querySelector('main');
          const base = document.querySelector('base');
          let pestana = 0;
          let avisoPendiente = false;
          // Total de líneas del documento en el editor, para ubicar el final.
          let totalLineas = 0;
          // Mientras la app mueve la vista, el scroll no se devuelve al editor (no se rebota).
          let desdeApp = false;

          chrome.webview.addEventListener('message', e => {
            const m = e.data;
            if (m.tipo === 'html') {
              pestana = m.id;
              if (m.base) base.setAttribute('href', m.base); else base.removeAttribute('href');
              main.innerHTML = m.html;
              resaltarCodigo();
              dibujarDiagramas();
              if (m.scroll != null) moverDesdeApp(m.scroll);
            } else if (m.tipo === 'ancho') {
              document.body.classList.toggle('completo', m.completo);
            } else if (m.tipo === 'linea') {
              totalLineas = m.total;
              moverDesdeApp(m.final ? document.documentElement.scrollHeight : yDeLinea(m.linea));
            }
          });

          function moverDesdeApp(y) {
            desdeApp = true;
            scrollTo(0, y);
            setTimeout(() => desdeApp = false, 60);
          }

          // Puntos de referencia entre el texto y la página: la línea de origen de cada bloque y dónde
          // quedó dibujado, en orden. Entre dos puntos se interpola, así el scroll avanza parejo.
          function anclas() {
            const lista = [{ linea: 0, y: 0 }];
            for (const el of main.querySelectorAll('[data-line]')) {
              const linea = +el.dataset.line, y = el.getBoundingClientRect().top + scrollY;
              const ultima = lista[lista.length - 1];
              if (linea > ultima.linea && y >= ultima.y) lista.push({ linea, y });
            }
            const fin = document.documentElement.scrollHeight;
            if (totalLineas > lista[lista.length - 1].linea) lista.push({ linea: totalLineas, y: fin });
            return lista;
          }

          function yDeLinea(linea) {
            const a = anclas();
            for (let i = 1; i < a.length; i++)
              if (a[i].linea > linea) return a[i - 1].y + (a[i].y - a[i - 1].y) * (linea - a[i - 1].linea) / (a[i].linea - a[i - 1].linea);
            return a[a.length - 1].y;
          }

          function lineaDeY(y) {
            const a = anclas();
            for (let i = 1; i < a.length; i++)
              if (a[i].y > y) return a[i - 1].linea + (a[i].linea - a[i - 1].linea) * (y - a[i - 1].y) / ((a[i].y - a[i - 1].y) || 1);
            return a[a.length - 1].linea;
          }

          // Avisa a la app dónde quedó el scroll de cada pestaña, para restaurarlo al volver a ella.
          addEventListener('scroll', () => {
            if (avisoPendiente) return;
            avisoPendiente = true;
            setTimeout(() => {
              avisoPendiente = false;
              chrome.webview.postMessage({ tipo: 'scroll', id: pestana, y: scrollY });
            }, 150);
          });

          // Al desplazar la vista, el editor la sigue (la app lo usa solo en el modo dividido).
          let lineaPendiente = false;
          addEventListener('scroll', () => {
            if (desdeApp || lineaPendiente) return;
            lineaPendiente = true;
            requestAnimationFrame(() => {
              lineaPendiente = false;
              const final = scrollY + innerHeight >= document.documentElement.scrollHeight - 2;
              chrome.webview.postMessage({ tipo: 'lineaVista', linea: lineaDeY(scrollY), final });
            });
          });

          // Resaltado de los bloques con lenguaje (```csharp …). Sin lenguaje no se adivina: suele equivocarse.
          if (window.hljs) hljs.configure({ ignoreUnescapedHTML: true });
          function resaltarCodigo() {
            if (!window.hljs) return;
            for (const codigo of main.querySelectorAll('pre > code[class*="language-"]')) {
              try { hljs.highlightElement(codigo); } catch { /* lenguaje desconocido: queda sin color */ }
            }
          }

          // Diagramas Mermaid. La librería se carga recién cuando aparece el primer bloque ```mermaid.
          // Cada SVG se guarda por fuente y tema: al escribir solo se vuelve a dibujar el diagrama que cambió,
          // y el resto aparece al instante, sin saltos de scroll.
          const temaOscuro = matchMedia('(prefers-color-scheme: dark)');
          const diagramas = new Map();
          let cargaMermaid = null;
          let numeroDiagrama = 0;

          function cargarMermaid() {
            return cargaMermaid ??= new Promise((listo, fallo) => {
              const script = document.createElement('script');
              script.src = 'https://{{Documentos.HostRecursos}}/mermaid.min.js';
              script.onload = () => { configurarMermaid(); listo(); };
              script.onerror = fallo;
              document.head.appendChild(script);
            });
          }

          function configurarMermaid() {
            mermaid.initialize({
              startOnLoad: false,
              securityLevel: 'strict',
              theme: temaOscuro.matches ? 'dark' : 'default',
              fontFamily: '"Segoe UI Variable Text", "Segoe UI", sans-serif',
            });
          }

          temaOscuro.addEventListener('change', () => {
            if (!window.mermaid) return;
            configurarMermaid();
            for (const div of main.querySelectorAll('.diagrama[data-fuente]')) div.replaceWith(bloqueDeCodigo(div));
            dibujarDiagramas();
          });

          // Vuelve a armar el bloque original a partir de un diagrama ya dibujado (como lo genera Markdig).
          function bloqueDeCodigo(div) {
            const pre = document.createElement('pre');
            pre.className = 'mermaid';
            if (div.dataset.line) pre.dataset.line = div.dataset.line;
            pre.textContent = div.dataset.fuente;
            return pre;
          }

          async function dibujarDiagramas() {
            // Markdig convierte ```mermaid en <pre class="mermaid">, sin <code> adentro.
            const bloques = [...main.querySelectorAll('pre.mermaid')];
            if (bloques.length === 0) return;
            try { await cargarMermaid(); } catch { return; }

            for (const pre of bloques) {
              const fuente = pre.textContent;
              const clave = (temaOscuro.matches ? 'oscuro:' : 'claro:') + fuente;
              const div = document.createElement('div');
              div.className = 'diagrama';
              div.dataset.fuente = fuente;
              if (pre.dataset.line) div.dataset.line = pre.dataset.line;

              let svg = diagramas.get(clave);
              if (svg === undefined) {
                const id = 'diagrama-' + ++numeroDiagrama;
                try {
                  await mermaid.parse(fuente);
                  svg = (await mermaid.render(id, fuente)).svg;
                  diagramas.set(clave, svg);
                } catch (error) {
                  document.getElementById('d' + id)?.remove(); // Mermaid deja restos al fallar
                  div.classList.add('diagrama-error');
                  div.textContent = 'Error en el diagrama: ' + (error?.message ?? error);
                  if (pre.isConnected) pre.after(div); // el código queda visible para corregirlo
                  continue;
                }
              }
              if (!pre.isConnected) continue; // el contenido cambió mientras se dibujaba
              div.innerHTML = svg;
              pre.replaceWith(div);
            }
          }

          // Sin la barra de estado del navegador, la dirección de un enlace se muestra como tooltip.
          document.addEventListener('mouseover', e => {
            const a = e.target.closest('a[href]');
            if (a && !a.hasAttribute('title')) a.title = a.href;
          });

          // Los enlaces internos (#seccion) se resuelven aquí, sin salir del documento.
          document.addEventListener('click', e => {
            const a = e.target.closest('a[href^="#"]');
            if (!a) return;
            e.preventDefault();
            const id = decodeURIComponent(a.getAttribute('href').slice(1));
            document.getElementById(id)?.scrollIntoView({ behavior: 'smooth' });
          });
        </script>
        </body>
        </html>
        """;

    private const string Estilo = """
        :root {
          color-scheme: light dark;
          --fondo: #ffffff;
          --texto: #1f2328;
          --suave: #59636e;
          --borde: #d1d9e0;
          --codigo: #f6f8fa;
          --enlace: #0969da;
          --cita: #59636e;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            /* Igual que el área de trabajo (Tema.cs); el código, un tono más hundido. */
            --fondo: #1f242b;
            --texto: #e6edf3;
            --suave: #9198a1;
            --borde: #3d444d;
            --codigo: #161b22;
            --enlace: #4493f8;
            --cita: #9198a1;
          }
        }
        * { box-sizing: border-box; }
        html { background: var(--fondo); }
        body {
          margin: 0;
          background: var(--fondo);
          color: var(--texto);
          font: 16px/1.6 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
          word-wrap: break-word;
        }
        main { max-width: 860px; margin: 0 auto; padding: 32px 40px 64px; }
        /* Configuración "Todo el ancho". */
        body.completo main { max-width: none; }
        h1, h2, h3, h4, h5, h6 { margin: 1.5em 0 .5em; line-height: 1.25; font-weight: 600; }
        h1 { font-size: 2em; padding-bottom: .3em; border-bottom: 1px solid var(--borde); }
        h2 { font-size: 1.5em; padding-bottom: .3em; border-bottom: 1px solid var(--borde); }
        h3 { font-size: 1.25em; }
        main > :first-child { margin-top: 0; }
        p, ul, ol, blockquote, pre, table { margin: 0 0 1em; }
        a { color: var(--enlace); text-decoration: none; }
        a:hover { text-decoration: underline; }
        img { max-width: 100%; border-radius: 8px; }
        hr { border: 0; height: 1px; background: var(--borde); margin: 1.5em 0; }
        blockquote { padding: 0 1em; color: var(--cita); border-left: .25em solid var(--borde); }
        /* Cascadia Mono y no Code: sin ligaduras, el código se ve tal como está escrito (=> y no ⇒). */
        code, kbd, pre { font-family: "Cascadia Mono", Consolas, monospace; font-size: 85%; }
        code { background: var(--codigo); padding: .2em .4em; border-radius: 6px; }
        pre { background: var(--codigo); padding: 16px; border-radius: 8px; overflow: auto; line-height: 1.45; }
        pre code { background: none; padding: 0; font-size: 100%; }
        /* El tema de highlight.js trae fondo y relleno propios; manda el del bloque. */
        pre code.hljs { background: none; padding: 0; }
        .diagrama { margin: 0 0 1em; text-align: center; overflow-x: auto; }
        .diagrama svg { max-width: 100%; height: auto; }
        .diagrama-error { --error: #d1242f; text-align: left; padding: 10px 14px; border-radius: 8px; font-size: 90%;
                          color: var(--error); background: color-mix(in srgb, var(--error) 12%, transparent); white-space: pre-wrap; }
        @media (prefers-color-scheme: dark) { .diagrama-error { --error: #ff7b72; } }
        kbd { border: 1px solid var(--borde); border-bottom-width: 2px; border-radius: 6px; padding: .1em .4em; }
        /* Bordes separados: con border-collapse las esquinas redondeadas no se dibujan. */
        table { border-collapse: separate; border-spacing: 0; display: block; width: max-content; max-width: 100%;
                overflow: auto; border: 1px solid var(--borde); border-radius: 8px; }
        th, td { border: 0; border-right: 1px solid var(--borde); border-bottom: 1px solid var(--borde); padding: 6px 13px; }
        th:last-child, td:last-child { border-right: 0; }
        tr:last-child td { border-bottom: 0; }
        th { font-weight: 600; background: var(--codigo); }
        ul.contains-task-list { list-style: none; padding-left: 1.2em; }
        .task-list-item input { margin: 0 .4em 0 -1.2em; }
        .footnotes { font-size: 90%; color: var(--suave); }
        .vacio { text-align: center; margin-top: 18vh; color: var(--suave); }
        .vacio h1 { border: 0; color: var(--texto); }
        .nota { font-size: 90%; }
        /* Marca del desarrollador al pie de la bienvenida; el logo es una máscara pintada con el color del tema. */
        .marca { position: fixed; left: 0; right: 0; bottom: 28px; display: flex; flex-direction: column;
                 align-items: center; gap: 10px; color: var(--suave); font-size: 13px; }
        .marca strong { color: var(--texto); font-weight: 600; }
        .lobo { width: 56px; height: 56px; background: var(--suave);
                -webkit-mask: var(--logo) center / contain no-repeat; mask: var(--logo) center / contain no-repeat; }
        """;
}
