using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace VisorMD;

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

    public static string Bienvenida() => """
        <div class="vacio">
          <h1>VisorMD</h1>
          <p>Arrastra un archivo <code>.md</code> a esta ventana o presiona <kbd>Ctrl</kbd>+<kbd>O</kbd> para abrir uno.</p>
          <p class="nota"><kbd>Ctrl</kbd>+<kbd>N</kbd> crea un documento nuevo · <kbd>Ctrl</kbd>+<kbd>2</kbd> muestra editor y vista lado a lado</p>
        </div>
        """;

    public static string Plantilla() => $$"""
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        <base>
        <title>VisorMD</title>
        <style>{{Estilo}}</style>
        </head>
        <body>
        <main></main>
        <script>
          const main = document.querySelector('main');
          const base = document.querySelector('base');
          let pestana = 0;
          let avisoPendiente = false;

          chrome.webview.addEventListener('message', e => {
            const m = e.data;
            if (m.tipo === 'html') {
              pestana = m.id;
              if (m.base) base.setAttribute('href', m.base); else base.removeAttribute('href');
              main.innerHTML = m.html;
              if (m.scroll != null) scrollTo(0, m.scroll);
            } else if (m.tipo === 'linea') {
              irALinea(m.linea);
            }
          });

          // Lleva a la vista el último bloque que empieza en o antes de la línea indicada del editor.
          function irALinea(linea) {
            let destino = null;
            for (const el of main.querySelectorAll('[data-line]')) {
              if (+el.dataset.line <= linea) destino = el; else break;
            }
            scrollTo({ top: destino ? destino.getBoundingClientRect().top + scrollY - 16 : 0 });
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
            --fondo: #0d1117;
            --texto: #e6edf3;
            --suave: #9198a1;
            --borde: #3d444d;
            --codigo: #151b23;
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
        code, kbd, pre { font-family: "Cascadia Code", Consolas, monospace; font-size: 85%; }
        code { background: var(--codigo); padding: .2em .4em; border-radius: 6px; }
        pre { background: var(--codigo); padding: 16px; border-radius: 8px; overflow: auto; line-height: 1.45; }
        pre code { background: none; padding: 0; font-size: 100%; }
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
        """;
}
