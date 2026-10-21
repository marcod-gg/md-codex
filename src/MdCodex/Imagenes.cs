using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

namespace MdCodex;

// Imágenes que se agregan a un documento desde el modo Pro: se copian a la carpeta de imágenes (por
// omisión, Imágenes\md-codex; o una carpeta con nombre junto a cada documento) y el Markdown las enlaza
// con una ruta relativa al documento. Si la carpeta está en otro disco, no hay ruta relativa posible y se
// usa la dirección file:/// de la imagen.
public static class Imagenes
{
    // De la configuración (ya validada): ruta completa, o nombre relativo al documento.
    public static string Carpeta { get; set; } = Configuracion.CarpetaImagenesPorDefecto;

    public const long TamanoMaximo = 20 * 1024 * 1024;

    public const string Filtro = "Imágenes (*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg)|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg";

    private static readonly string[] Extensiones = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg"];

    public static bool EsImagen(string ruta) =>
        Extensiones.Contains(Path.GetExtension(ruta), StringComparer.OrdinalIgnoreCase);

    // La carpeta fija de imágenes, si la configuración es una ruta completa; null si es una por documento.
    public static string? CarpetaFija => Path.IsPathFullyQualified(Carpeta) ? Carpeta : null;

    // Dónde quedan las imágenes de un documento de esa carpeta.
    public static string CarpetaPara(string carpetaDocumento) =>
        Path.Combine(carpetaDocumento, Configuracion.CarpetaValida(Carpeta).Replace('/', Path.DirectorySeparatorChar));

    // Copia la imagen a la carpeta de imágenes (salvo que ya esté ahí o dentro de la carpeta del documento)
    // y devuelve la ruta para el Markdown.
    public static string Incorporar(string carpetaDocumento, string origen)
    {
        var completa = Path.GetFullPath(origen);
        if (new FileInfo(completa).Length > TamanoMaximo)
            throw new InvalidOperationException($"{Path.GetFileName(completa)} pesa más de 20 MB.");

        var carpeta = CarpetaPara(carpetaDocumento);
        var destino = completa;
        if (!Documentos.EstaDentro(completa, carpetaDocumento) && !Documentos.EstaDentro(completa, carpeta))
        {
            destino = NombreLibre(Crear(carpeta), Path.GetFileName(completa));
            File.Copy(completa, destino);
        }
        return RutaMarkdown(carpetaDocumento, destino);
    }

    // Guarda como PNG una imagen pegada sin archivo (por ejemplo, una captura de pantalla).
    public static string IncorporarPortapapeles(string carpetaDocumento, BitmapSource imagen)
    {
        var destino = NombreLibre(Crear(CarpetaPara(carpetaDocumento)), $"pegada-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        using (var flujo = File.Create(destino))
        {
            var codificador = new PngBitmapEncoder();
            codificador.Frames.Add(BitmapFrame.Create(imagen));
            codificador.Save(flujo);
        }
        return RutaMarkdown(carpetaDocumento, destino);
    }

    private static string Crear(string carpeta)
    {
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

    // Relativa al documento (puede subir con "../"); en otro disco, la dirección file:/// completa.
    private static string RutaMarkdown(string carpetaDocumento, string archivo)
    {
        var relativa = Path.GetRelativePath(carpetaDocumento, archivo);
        if (Path.IsPathRooted(relativa))
            return new Uri(archivo).AbsoluteUri;
        return string.Join('/', relativa.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
    }
}
