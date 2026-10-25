using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace XRay.App.Services;

/// <summary>
/// Adds or removes X-RAY from the current user's sign-in startup list (HKCU\...\Run).
/// Only called in response to the user explicitly toggling the setting; never requires elevation.
/// </summary>
public sealed class StartupRegistration(ILogger<StartupRegistration> logger)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "X-RAY";

    public bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public bool TrySet(bool enabled)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Executable path unknown.");
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            logger.LogInformation("Start with Windows set to {Enabled}", enabled);
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            logger.LogWarning("Could not change startup registration: {Message}", ex.Message);
            return false;
        }
    }
}
