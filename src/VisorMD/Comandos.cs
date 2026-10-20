using System.Windows.Input;

namespace VisorMD;

public static class Comandos
{
    public static readonly RoutedUICommand ModoVista = new("Vista", nameof(ModoVista), typeof(Comandos));
    public static readonly RoutedUICommand ModoDividido = new("Dividido", nameof(ModoDividido), typeof(Comandos));
    public static readonly RoutedUICommand ModoEditor = new("Editor", nameof(ModoEditor), typeof(Comandos));
    public static readonly RoutedUICommand Negrita = new("Negrita", nameof(Negrita), typeof(Comandos));
    public static readonly RoutedUICommand Cursiva = new("Cursiva", nameof(Cursiva), typeof(Comandos));
    public static readonly RoutedUICommand AbrirCarpeta = new("Abrir carpeta", nameof(AbrirCarpeta), typeof(Comandos));
    public static readonly RoutedUICommand VerExplorador = new("Explorador", nameof(VerExplorador), typeof(Comandos));
    public static readonly RoutedUICommand VerEsquema = new("Esquema", nameof(VerEsquema), typeof(Comandos));
    public static readonly RoutedUICommand AlternarLateral = new("Barra lateral", nameof(AlternarLateral), typeof(Comandos));
}
