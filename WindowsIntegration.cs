using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace TraderPen;

internal static class WindowsIntegration
{
    private const string ApplicationRegistryKey = @"Software\Classes\TraderPen.Project";
    private const string ExtensionRegistryKey = @"Software\Classes\.traderpen";
    private const string PreferencesRegistryKey = @"Software\TraderPen\Preferences";
    private const string ProjectFileProgId = "TraderPen.Project";

    public static bool IsToolbarVertical()
    {
        using var preferences = Registry.CurrentUser.OpenSubKey(PreferencesRegistryKey);
        return preferences?.GetValue("ToolbarVertical") is int value && value == 1;
    }

    public static void SetToolbarVertical(bool isVertical)
    {
        using var preferences = Registry.CurrentUser.CreateSubKey(PreferencesRegistryKey, writable: true)
            ?? throw new IOException("Não foi possível abrir as preferências do TraderPen.");
        preferences.SetValue("ToolbarVertical", isVertical ? 1 : 0, RegistryValueKind.DWord);
    }

    public static void RegisterProjectFileAssociation()
    {
        string executablePath = Environment.ProcessPath
            ?? throw new IOException("Não foi possível localizar o executável do TraderPen.");
        string quotedExecutablePath = $"\"{executablePath}\"";

        using (var extensionKey = Registry.CurrentUser.CreateSubKey(ExtensionRegistryKey, writable: true)
               ?? throw new IOException("Não foi possível registrar a extensão .traderpen."))
        {
            extensionKey.SetValue(null, ProjectFileProgId, RegistryValueKind.String);
            extensionKey.SetValue("Content Type", "application/x-traderpen", RegistryValueKind.String);
            using var openWith = extensionKey.CreateSubKey("OpenWithProgids", writable: true)
                ?? throw new IOException("Não foi possível registrar a abertura de arquivos .traderpen.");
            openWith.SetValue(ProjectFileProgId, string.Empty, RegistryValueKind.String);
        }

        using (var progIdKey = Registry.CurrentUser.CreateSubKey(ApplicationRegistryKey, writable: true)
               ?? throw new IOException("Não foi possível registrar o tipo de documento TraderPen."))
        {
            progIdKey.SetValue(null, "Documento TraderPen", RegistryValueKind.String);
            using var defaultIcon = progIdKey.CreateSubKey("DefaultIcon", writable: true)
                ?? throw new IOException("Não foi possível registrar o ícone dos documentos TraderPen.");
            defaultIcon.SetValue(null, $"{quotedExecutablePath},0", RegistryValueKind.String);

            using var openCommand = progIdKey.CreateSubKey(@"shell\open\command", writable: true)
                ?? throw new IOException("Não foi possível registrar como abrir documentos TraderPen.");
            openCommand.SetValue(null, $"{quotedExecutablePath} \"%1\"", RegistryValueKind.String);
        }

        SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
