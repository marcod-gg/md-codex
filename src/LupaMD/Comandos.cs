using System.Windows.Input;

namespace LupaMD;

public static class Comandos
{
    public static readonly RoutedUICommand ModoVista = new("Vista", nameof(ModoVista), typeof(Comandos));
    public static readonly RoutedUICommand ModoDividido = new("Dividido", nameof(ModoDividido), typeof(Comandos));
    public static readonly RoutedUICommand ModoEditor = new("Editor", nameof(ModoEditor), typeof(Comandos));
    public static readonly RoutedUICommand ModoPro = new("Pro", nameof(ModoPro), typeof(Comandos));
    public static readonly RoutedUICommand Negrita = new("Negrita", nameof(Negrita), typeof(Comandos));
    public static readonly RoutedUICommand Cursiva = new("Cursiva", nameof(Cursiva), typeof(Comandos));
    public static readonly RoutedUICommand AbrirCarpeta = new("Abrir carpeta", nameof(AbrirCarpeta), typeof(Comandos));
    public static readonly RoutedUICommand VerExplorador = new("Explorador", nameof(VerExplorador), typeof(Comandos));
    public static readonly RoutedUICommand VerEsquema = new("Esquema", nameof(VerEsquema), typeof(Comandos));
    public static readonly RoutedUICommand AlternarLateral = new("Barra lateral", nameof(AlternarLateral), typeof(Comandos));
    public static readonly RoutedUICommand GuardarTodo = new("Guardar todo", nameof(GuardarTodo), typeof(Comandos));
    public static readonly RoutedUICommand CerrarPestana = new("Cerrar pestaña", nameof(CerrarPestana), typeof(Comandos));
    public static readonly RoutedUICommand CerrarTodas = new("Cerrar todas", nameof(CerrarTodas), typeof(Comandos));
    public static readonly RoutedUICommand PestanaSiguiente = new("Pestaña siguiente", nameof(PestanaSiguiente), typeof(Comandos));
    public static readonly RoutedUICommand PestanaAnterior = new("Pestaña anterior", nameof(PestanaAnterior), typeof(Comandos));
}
