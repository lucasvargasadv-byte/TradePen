using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;

namespace TraderPen;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            WindowsIntegration.RegisterProjectFileAssociation();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            MessageBox.Show(
                $"Não foi possível registrar o ícone e a abertura automática dos arquivos .traderpen.\n{ex.Message}",
                "Integração com o Windows",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        var window = new MainWindow
        {
            StartupProjectPath = e.Args.FirstOrDefault(argument =>
                string.Equals(System.IO.Path.GetExtension(argument), ".traderpen", StringComparison.OrdinalIgnoreCase))
        };
        MainWindow = window;
        window.Show();
    }
}
