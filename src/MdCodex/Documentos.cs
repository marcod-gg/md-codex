using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MdCodex;

public static class Documentos
{
    private static readonly string[] Extensiones = [".md", ".markdown", ".mdown", ".mkd", ".txt"];

    // Host de las librerías de la vista previa (carpeta Vista junto al .exe).
    public const string HostRecursos = "app.mdcodex";

    public static bool EsMarkdown(string ruta) =>
        Extensiones.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

    public static bool EstaDentro(string ruta, string carpeta) =>
        (ruta + Path.DirectorySeparatorChar).StartsWith(carpeta + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Ruta de disco ↔ camino de URL: "C:\Users\x" ↔ "C/Users/x" y "\\servidor\recurso" ↔ "UNC/servidor/recurso".
    // Con la ruta completa en la URL, un enlace relativo que sube con "../" llega al archivo real.
    public static string CaminoUrl(string carpeta)
    {
        var completa = Path.TrimEndingDirectorySeparator(Path.GetFullPath(carpeta));
        var partes = completa.StartsWith(@"\\", StringComparison.Ordinal)
            ? ["UNC", .. completa[2..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)]
            : new[] { completa[..1] }.Concat(completa[2..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)).ToArray();
        return string.Join('/', partes.Select(Uri.EscapeDataString));
    }

    public static string? RutaDeCamino(string camino)
    {
        var partes = camino.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length == 0)
            return null;
        if (partes[0] == "UNC" && partes.Length >= 3)
            return @"\\" + string.Join('\\', partes[1..]);
        if (partes[0].Length == 1 && char.IsAsciiLetter(partes[0][0]))
            return partes[0] + @":\" + string.Join('\\', partes[1..]);
        return null;
    }

    // Host virtual para una carpeta; uno distinto por carpeta evita que el caché mezcle archivos.
    public static string HostPara(string carpeta) =>
        $"d{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(carpeta.ToUpperInvariant())))[..16]}.mdcodex";
}
