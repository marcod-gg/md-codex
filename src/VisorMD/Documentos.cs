using System.IO;

namespace VisorMD;

public static class Documentos
{
    private static readonly string[] Extensiones = [".md", ".markdown", ".mdown", ".mkd", ".txt"];

    public static bool EsMarkdown(string ruta) =>
        Extensiones.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);
}
