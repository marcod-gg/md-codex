using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace MdCodex;

// Imágenes que se agregan a un documento desde el modo Pro: se guardan en "imagenes/" junto al
// documento y el Markdown las enlaza con una ruta relativa, así el documento sigue siendo portable.
public static class Imagenes
{
    public const long TamanoMaximo = 20 * 1024 * 1024;

    public const string Filtro = "Imágenes (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg";

    private static readonly string[] Extensiones = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"];

    public static bool EsImagen(string ruta) =>
        Extensiones.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

    // Copia la imagen a "imagenes/" (salvo que ya esté dentro de la carpeta del documento) y devuelve
    // la ruta para el Markdown.
    public static string Incorporar(string carpetaDocumento, string origen)
    {
        var completa = Path.GetFullPath(origen);
        if (new FileInfo(completa).Length > TamanoMaximo)
            throw new InvalidOperationException($"{Path.GetFileName(completa)} pesa más de 20 MB.");

        var destino = completa;
        if (!Documentos.EstaDentro(completa, carpetaDocumento))
        {
            destino = NombreLibre(CarpetaImagenes(carpetaDocumento), Path.GetFileName(completa));
            File.Copy(completa, destino);
        }
        return RutaMarkdown(carpetaDocumento, destino);
    }

    // Guarda como PNG una imagen pegada sin archivo (por ejemplo, una captura de pantalla).
    public static string IncorporarPortapapeles(string carpetaDocumento, BitmapSource imagen)
    {
        var destino = NombreLibre(CarpetaImagenes(carpetaDocumento), $"pegada-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        using (var flujo = File.Create(destino))
        {
            var codificador = new PngBitmapEncoder();
            codificador.Frames.Add(BitmapFrame.Create(imagen));
            codificador.Save(flujo);
        }
        return RutaMarkdown(carpetaDocumento, destino);
    }

    private static string CarpetaImagenes(string carpetaDocumento)
    {
        var carpeta = Path.Combine(carpetaDocumento, "imagenes");
        Directory.CreateDirectory(carpeta);
        return carpeta;
    }

    // Sin espacios ni caracteres raros (la ruta queda legible en el Markdown) y sin pisar archivos existentes.
    private static string NombreLibre(string carpeta, string nombre)
    {
        var limpio = new StringBuilder();
        foreach (var c in Path.GetFileNameWithoutExtension(nombre))
            limpio.Append(char.IsWhiteSpace(c) || Path.GetInvalidFileNameChars().Contains(c) ? '-' : c);
        var baseNombre = limpio.Length > 0 ? limpio.ToString() : "imagen";
        var extension = Path.GetExtension(nombre).ToLowerInvariant();

        var candidato = Path.Combine(carpeta, baseNombre + extension);
        for (var n = 2; File.Exists(candidato); n++)
            candidato = Path.Combine(carpeta, $"{baseNombre}-{n}{extension}");
        return candidato;
    }

    private static string RutaMarkdown(string carpetaDocumento, string archivo) =>
        string.Join('/', Path.GetRelativePath(carpetaDocumento, archivo).Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
}
