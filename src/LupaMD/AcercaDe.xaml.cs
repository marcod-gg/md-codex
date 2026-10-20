using System.Windows;
using System.Windows.Interop;
using System.Windows.Navigation;

namespace LupaMD;

public partial class AcercaDe : Window
{
    public AcercaDe()
    {
        InitializeComponent();
        TextoVersion.Text = $"Versión {Marca.Version}";
        TextoCopyright.Text = Marca.Copyright;
        SourceInitialized += (_, _) => Nativo.RedondearEsquinas(new WindowInteropHelper(this).Handle);
        // Con WindowChrome, SizeToContent reserva un borde que ya no existe y deja una franja vacía:
        // una vez dibujado, la ventana toma exactamente el tamaño de su contenido.
        ContentRendered += (_, _) =>
        {
            SizeToContent = SizeToContent.Manual;
            var contenido = (FrameworkElement)Content;
            Width = contenido.ActualWidth;
            Height = contenido.ActualHeight;
        };
    }

    private void Cerrar_Click(object sender, RoutedEventArgs e) => Close();

    private void Enlace_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Marca.Abrir(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
