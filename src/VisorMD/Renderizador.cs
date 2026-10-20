using System.Net;
using Markdig;

namespace VisorMD;

// Convierte Markdown en una página HTML completa, con estilo que sigue el tema claro/oscuro de Windows.
public static class Renderizador
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // tablas, listas de tareas, notas al pie, ids en títulos, etc.
        .UseEmojiAndSmiley()
        .Build();

    public static string Documento(string markdown, string titulo, string urlBase) =>
        Pagina(titulo, Markdown.ToHtml(markdown, Pipeline), urlBase);

    public static string Bienvenida() => Pagina("VisorMD", """
        <div class="vacio">
          <h1>VisorMD</h1>
          <p>Arrastra un archivo <code>.md</code> a esta ventana o presiona <kbd>Ctrl</kbd>+<kbd>O</kbd> para abrir uno.</p>
          <p class="nota">El documento se actualiza solo cada vez que lo guardas.</p>
        </div>
        """);

    public static string Error(string mensaje) => Pagina("Error", $"""
        <div class="vacio">
          <h1>No se pudo leer el archivo</h1>
          <p>{WebUtility.HtmlEncode(mensaje)}</p>
        </div>
        """);

    private static string Pagina(string titulo, string cuerpo, string? urlBase = null) => $$"""
        <!doctype html>
        <html lang="es">
        <head>
        <meta charset="utf-8">
        {{(urlBase is null ? "" : $"<base href=\"{urlBase}\">")}}
        <title>{{WebUtility.HtmlEncode(titulo)}}</title>
        <style>{{Estilo}}</style>
        </head>
        <body>
        <main>
        {{cuerpo}}
        </main>
        <script>
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
        img { max-width: 100%; }
        hr { border: 0; height: 1px; background: var(--borde); margin: 1.5em 0; }
        blockquote { padding: 0 1em; color: var(--cita); border-left: .25em solid var(--borde); }
        code, kbd, pre { font-family: "Cascadia Code", Consolas, monospace; font-size: 85%; }
        code { background: var(--codigo); padding: .2em .4em; border-radius: 6px; }
        pre { background: var(--codigo); padding: 16px; border-radius: 6px; overflow: auto; line-height: 1.45; }
        pre code { background: none; padding: 0; font-size: 100%; }
        kbd { border: 1px solid var(--borde); border-bottom-width: 2px; border-radius: 4px; padding: .1em .4em; }
        table { border-collapse: collapse; display: block; overflow: auto; }
        th, td { border: 1px solid var(--borde); padding: 6px 13px; }
        th { font-weight: 600; background: var(--codigo); }
        ul.contains-task-list { list-style: none; padding-left: 1.2em; }
        .task-list-item input { margin: 0 .4em 0 -1.2em; }
        .footnotes { font-size: 90%; color: var(--suave); }
        .vacio { text-align: center; margin-top: 18vh; color: var(--suave); }
        .vacio h1 { border: 0; color: var(--texto); }
        .nota { font-size: 90%; }
        """;
}
