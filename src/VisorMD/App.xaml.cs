using System.IO;
using System.Windows;

namespace VisorMD;

public partial class App : Application
{
    // Acepta la ruta del archivo como argumento para poder usarlo con "Abrir con…" en Windows.
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var archivo = e.Args.FirstOrDefault(File.Exists);
        new MainWindow(archivo).Show();
    }
}
