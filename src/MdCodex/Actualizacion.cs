using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MdCodex;

// Actualizaciones desde las releases de GitHub: buscar la última, descargar su instalador
// y ejecutarlo en modo silencioso al cerrar la app (no puede reemplazar sus archivos mientras corre).
public static class Actualizacion
{
    // Instalador y Sha256 son null si la release no trae el .exe adjunto.
    public sealed record Release(string Version, string Pagina, string? Instalador, string? Sha256);

    private static HttpClient Cliente(TimeSpan espera)
    {
        var http = new HttpClient { Timeout = espera };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MDCodex/" + Marca.Version);
        return http;
    }

    // Devuelve la última release si es más nueva que la instalada. Cualquier fallo (sin red,
    // límite de la API, sin releases) se ignora: el aviso es opcional.
    public static async Task<Release?> BuscarAsync()
    {
        try
        {
            using var http = Cliente(TimeSpan.FromSeconds(10));
            using var json = JsonDocument.Parse(
                await http.GetStringAsync("https://api.github.com/repos/marcod-gg/md-codex/releases/latest"));
            var raiz = json.RootElement;
            var version = raiz.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            if (System.Version.Parse(version) <= System.Version.Parse(Marca.Version))
                return null;

            string? instalador = null, sha256 = null;
            foreach (var archivo in raiz.GetProperty("assets").EnumerateArray())
            {
                if (!archivo.GetProperty("name").GetString()!.EndsWith("-instalador.exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                instalador = archivo.GetProperty("browser_download_url").GetString();
                if (archivo.TryGetProperty("digest", out var digest) && digest.GetString() is { } d && d.StartsWith("sha256:"))
                    sha256 = d["sha256:".Length..];
                break;
            }
            return new Release(version, raiz.GetProperty("html_url").GetString()!, instalador, sha256);
        }
        catch
        {
            return null;
        }
    }

    // Descarga el instalador a %TEMP%\MDCodex y, si GitHub publica su SHA-256, lo comprueba.
    public static async Task<string> DescargarAsync(Release release, IProgress<double> progreso)
    {
        var carpeta = Path.Combine(Path.GetTempPath(), "MDCodex");
        Directory.CreateDirectory(carpeta);
        var destino = Path.Combine(carpeta, Path.GetFileName(new Uri(release.Instalador!).LocalPath));

        using var http = Cliente(TimeSpan.FromMinutes(10));
        using var respuesta = await http.GetAsync(release.Instalador, HttpCompletionOption.ResponseHeadersRead);
        respuesta.EnsureSuccessStatusCode();
        var total = respuesta.Content.Headers.ContentLength;
        await using (var origen = await respuesta.Content.ReadAsStreamAsync())
        await using (var archivo = File.Create(destino))
        {
            var bufer = new byte[81920];
            long leidos = 0;
            int n;
            while ((n = await origen.ReadAsync(bufer)) > 0)
            {
                await archivo.WriteAsync(bufer.AsMemory(0, n));
                leidos += n;
                if (total > 0)
                    progreso.Report((double)leidos / total.Value);
            }
        }

        if (release.Sha256 is not null)
        {
            await using var archivo = File.OpenRead(destino);
            if (!string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(archivo)), release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                archivo.Close();
                File.Delete(destino);
                throw new InvalidDataException("el archivo descargado no coincide con el publicado");
            }
        }
        return destino;
    }

    // Ejecuta el instalador sin ventanas; con reabrir, el instalador vuelve a abrir la app al terminar.
    // Se llama al cerrar la app, para que el instalador pueda reemplazar sus archivos.
    public static void Instalar(string instalador, bool reabrir) =>
        Process.Start(new ProcessStartInfo(instalador,
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART" + (reabrir ? " /REABRIR=1" : "")) { UseShellExecute = true });
}
