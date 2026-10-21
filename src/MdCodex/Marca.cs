using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace MdCodex;

// Marca del desarrollador (skolldev) y datos de la app que se muestran en la interfaz.
public static class Marca
{
    // Marca (el lobo del logo) y persona que firma el desarrollo.
    public const string Desarrollador = "skolldev";
    public const string Autor = "Marco González G.";

    public const string Repositorio = "https://github.com/marcod-gg/md-codex";
    public const string RepositorioCorto = "github.com/marcod-gg/md-codex";
    public const string NuevoProblema = Repositorio + "/issues/new";
    // Hyperlink.NavigateUri exige un Uri; con el texto, la ventana falla al cargarse.
    public static readonly Uri RepositorioUri = new(Repositorio);

    public static void Abrir(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    // El logo es trazo negro sobre fondo transparente: se usa como máscara y se pinta con el color del tema.
    public static readonly Uri Logo = new("pack://application:,,,/Recursos/skolldev.png");

    private static readonly Assembly Ensamblado = typeof(Marca).Assembly;

    public static string Version =>
        Ensamblado.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    // Consulta la última release de GitHub; devuelve su versión y página si es más nueva que la instalada.
    // Cualquier fallo (sin red, límite de la API, sin releases) se ignora: el aviso es opcional.
    public static async Task<(string Version, string Url)?> BuscarActualizacionAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MDCodex/" + Version);
            using var json = JsonDocument.Parse(
                await http.GetStringAsync("https://api.github.com/repos/marcod-gg/md-codex/releases/latest"));
            var tag = json.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
            var url = json.RootElement.GetProperty("html_url").GetString()!;
            return System.Version.Parse(tag) > System.Version.Parse(Version) ? (tag, url) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string Copyright =>
        Ensamblado.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? $"© {Desarrollador}";

    // El logo como data: URI, para usarlo en la vista previa (que no puede leer recursos de WPF).
    public static string LogoDataUri { get; } = CrearDataUri();

    private static string CrearDataUri()
    {
        using var flujo = Application.GetResourceStream(Logo)!.Stream;
        using var memoria = new MemoryStream();
        flujo.CopyTo(memoria);
        return "data:image/png;base64," + Convert.ToBase64String(memoria.ToArray());
    }
}
