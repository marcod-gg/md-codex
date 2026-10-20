using System.Windows.Media;
using Microsoft.Win32;

namespace VisorMD;

// Colores del editor; la vista previa usa los mismos tonos desde su CSS.
public sealed record Paleta(
    string Fondo, string Texto, string Tenue, string Seleccion, string LineaActual,
    string Titulo, string Codigo, string Cita, string Enlace, string Lista);

public static class Tema
{
    private static readonly Paleta Clara = new(
        Fondo: "#FFFFFF", Texto: "#1F2328", Tenue: "#8C959F", Seleccion: "#400969DA", LineaActual: "#F6F8FA",
        Titulo: "#0550AE", Codigo: "#953800", Cita: "#59636E", Enlace: "#0969DA", Lista: "#8250DF");

    private static readonly Paleta Oscura = new(
        Fondo: "#0D1117", Texto: "#E6EDF3", Tenue: "#6E7681", Seleccion: "#60388BFD", LineaActual: "#151B23",
        Titulo: "#79C0FF", Codigo: "#FFA657", Cita: "#9198A1", Enlace: "#58A6FF", Lista: "#D2A8FF");

    public static Paleta Actual() => EsOscuro() ? Oscura : Clara;

    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush Pincel(string hex)
    {
        var pincel = new SolidColorBrush(Color(hex));
        pincel.Freeze();
        return pincel;
    }

    private static bool EsOscuro()
    {
        using var clave = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return clave?.GetValue("AppsUseLightTheme") is int valor && valor == 0;
    }
}
