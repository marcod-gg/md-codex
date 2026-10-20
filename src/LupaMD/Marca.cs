using System.IO;
using System.Reflection;
using System.Windows;

namespace LupaMD;

// Marca del desarrollador (skolldev) y datos de la app que se muestran en la interfaz.
public static class Marca
{
    public const string Desarrollador = "skolldev";

    // El logo es trazo negro sobre fondo transparente: se usa como máscara y se pinta con el color del tema.
    public static readonly Uri Logo = new("pack://application:,,,/Recursos/skolldev.png");

    private static readonly Assembly Ensamblado = typeof(Marca).Assembly;

    public static string Version =>
        Ensamblado.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";

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
