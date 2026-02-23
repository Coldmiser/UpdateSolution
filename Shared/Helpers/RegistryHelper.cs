// Shared/Helpers/RegistryHelper.cs
// Thin wrapper around Microsoft.Win32.Registry so that the rest of the code
// never has to deal with null coalescing or exception handling for missing keys.

using Microsoft.Win32;
using Shared.Constants;

namespace Shared.Helpers;

/// <summary>
/// Reads configuration values from the HKLM registry hive with safe defaults.
/// </summary>
public static class RegistryHelper
{
    /// <summary>
    /// Reads a string value from the CapTG registry key.
    /// Returns <paramref name="defaultValue"/> if the key or value is absent.
    /// </summary>
    public static string GetString(string valueName, string defaultValue)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryConstants.RootKeyPath);
            if (key?.GetValue(valueName) is string s && !string.IsNullOrWhiteSpace(s))
                return s;
        }
        catch
        {
            // Silently fall through to the default — we cannot log here because
            // the logger itself may not yet be configured.
        }

        return defaultValue;
    }

    /// <summary>
    /// Reads a DWORD (int) value from the CapTG registry key.
    /// Returns <paramref name="defaultValue"/> if absent or unreadable.
    /// </summary>
    public static int GetInt(string valueName, int defaultValue)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryConstants.RootKeyPath);
            if (key?.GetValue(valueName) is int i)
                return i;
        }
        catch
        {
            // Same rationale as GetString — swallow and fall through.
        }

        return defaultValue;
    }

    /// <summary>
    /// Ensures the CapTG registry key exists (idempotent).
    /// Called once during service installation so subsequent reads never throw.
    /// </summary>
    public static void EnsureKeyExists()
    {
        Registry.LocalMachine.CreateSubKey(RegistryConstants.RootKeyPath, writable: true)
                             ?.Dispose();
    }

    /// <summary>
    /// Writes a string value to the CapTG registry key.
    /// Used by the installer to persist default values.
    /// </summary>
    public static void SetString(string valueName, string value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(
            RegistryConstants.RootKeyPath, writable: true);
        key?.SetValue(valueName, value, RegistryValueKind.String);
    }
}
