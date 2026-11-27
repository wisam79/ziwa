using System.Runtime.Versioning;
using Microsoft.Win32;
using Ziwa.Application.Abstractions;

namespace Ziwa.Platform.Windows.Shell;

[SupportedOSPlatform("windows")]
public sealed class WindowsFileAssociationService : IFileAssociationService
{
    private const string ProgId = "ZiwaStudio.Project";
    private const string ExtensionKeyName = ".ziwa";

    private readonly string _classesSubKeyPrefix;
    private readonly string? _executablePath;

    public WindowsFileAssociationService(string? classesSubKeyPrefix = null, string? executablePath = null)
    {
        _classesSubKeyPrefix = string.IsNullOrWhiteSpace(classesSubKeyPrefix)
            ? @"Software\Classes"
            : classesSubKeyPrefix.Trim().Trim('\\');
        _executablePath = string.IsNullOrWhiteSpace(executablePath)
            ? Environment.ProcessPath
            : executablePath.Trim();
    }

    public bool IsZiwaExtensionRegistered()
    {
        try
        {
            using RegistryKey? extKey = Registry.CurrentUser.OpenSubKey($@"{_classesSubKeyPrefix}\{ExtensionKeyName}");
            if (!string.Equals(extKey?.GetValue(null) as string, ProgId, StringComparison.Ordinal))
            {
                return false;
            }

            using RegistryKey? cmdKey = Registry.CurrentUser.OpenSubKey($@"{_classesSubKeyPrefix}\{ProgId}\shell\open\command");
            string? cmd = cmdKey?.GetValue(null) as string;
            return !string.IsNullOrWhiteSpace(cmd) &&
                   cmd.Contains("\"%1\"", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public void EnsureZiwaExtensionRegistered()
    {
        if (string.IsNullOrWhiteSpace(_executablePath))
        {
            return;
        }

        using (RegistryKey extKey = Registry.CurrentUser.CreateSubKey($@"{_classesSubKeyPrefix}\{ExtensionKeyName}"))
        {
            extKey.SetValue(null, ProgId);
            extKey.SetValue("Content Type", "application/vnd.ziwa.project");
        }

        using (RegistryKey progKey = Registry.CurrentUser.CreateSubKey($@"{_classesSubKeyPrefix}\{ProgId}"))
        {
            progKey.SetValue(null, "مشروع Ziwa Studio");
        }

        using (RegistryKey iconKey = Registry.CurrentUser.CreateSubKey($@"{_classesSubKeyPrefix}\{ProgId}\DefaultIcon"))
        {
            iconKey.SetValue(null, $"\"{_executablePath}\",0");
        }

        using (RegistryKey cmdKey = Registry.CurrentUser.CreateSubKey($@"{_classesSubKeyPrefix}\{ProgId}\shell\open\command"))
        {
            cmdKey.SetValue(null, $"\"{_executablePath}\" \"%1\"");
        }
    }
}
