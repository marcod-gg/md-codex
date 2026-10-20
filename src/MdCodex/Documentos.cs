using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MdCodex;

public static class Documentos
{
    private static readonly string[] Extensiones = [".md", ".markdown", ".mdown", ".mkd", ".txt"];

    public static bool EsMarkdown(string ruta) =>
        Extensiones.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

    public static bool EstaDentro(string ruta, string carpeta) =>
        (ruta + Path.DirectorySeparatorChar).StartsWith(carpeta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Host virtual para una carpeta; uno distinto por carpeta evita que el caché mezcle archivos.
    public static string HostPara(string carpeta) =>
        $"d{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(carpeta.ToUpperInvariant())))[..16]}.mdcodex";
}
