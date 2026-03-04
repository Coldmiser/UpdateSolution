// UpdateService/Workers/WingetWorker.cs
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Shared.Models;
using UpdateService.Logging;

namespace UpdateService.Workers;

public static class WingetWorker
{
    public static async Task<List<UpdateResult>> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<UpdateResult>();
        LogConfig.ServiceLog.Information("WingetWorker: starting upgrade pass.");

        var availableUpgrades = await ListAvailableUpgradesAsync(cancellationToken);

        if (availableUpgrades.Count == 0)
        {
            LogConfig.ServiceLog.Information("WingetWorker: no winget upgrades available.");
            return results;
        }

        LogConfig.ServiceLog.Information(
            "WingetWorker: {Count} package(s) queued for upgrade.", availableUpgrades.Count);

        foreach (var pkg in availableUpgrades)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await UpgradePackageAsync(pkg, cancellationToken);
            results.Add(result);
            LogHistoryEntry(result);
        }

        LogConfig.ServiceLog.Information(
            "WingetWorker: pass complete. Succeeded={S} Failed={F} Skipped={K}",
            results.Count(r => r.Status == UpdateStatus.Succeeded),
            results.Count(r => r.Status == UpdateStatus.Failed),
            results.Count(r => r.Status == UpdateStatus.Skipped));

        return results;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private static async Task<List<string>> ListAvailableUpgradesAsync(
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();

        var (exitCode, stdout, stderr) = await RunWingetAsync(
            "upgrade --include-unknown --accept-source-agreements",
            cancellationToken);

        if (exitCode == -1)
            return ids; // winget not found — already logged

        if (exitCode != 0 && exitCode != 3010)
        {
            LogConfig.ServiceLog.Warning(
                "WingetWorker: listing upgrades returned exit code {Code}. Stderr: {Err}",
                exitCode, stderr);
            return ids;
        }

        foreach (var line in stdout.Split('\n'))
        {
            var parts = Regex.Split(line.Trim(), @"\s{2,}");
            if (parts.Length >= 2
                && !parts[1].StartsWith("Id", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(parts[1]))
            {
                ids.Add(parts[1].Trim());
            }
        }

        return ids;
    }

    private static async Task<UpdateResult> UpgradePackageAsync(
        string packageId, CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information("WingetWorker: upgrading package '{Id}'.", packageId);

        var args = string.Concat(
            "upgrade --id \"", packageId, "\" ",
            "--silent ",
            "--accept-source-agreements ",
            "--accept-package-agreements ",
            "--include-unknown");

        var (exitCode, stdout, stderr) = await RunWingetAsync(args, cancellationToken);

        var succeeded    = exitCode is 0 or 3010;
        var rebootNeeded = exitCode == 3010;

        if (succeeded)
            LogConfig.ServiceLog.Information(
                "WingetWorker: '{Id}' upgraded successfully. RebootRequired={R}",
                packageId, rebootNeeded);
        else
            LogConfig.ServiceLog.Warning(
                "WingetWorker: '{Id}' upgrade failed (exit {Code}). Stderr: {Err}",
                packageId, exitCode, stderr);

        return new UpdateResult
        {
            Identifier     = packageId,
            Title          = packageId,
            Status         = succeeded ? UpdateStatus.Succeeded : UpdateStatus.Failed,
            ErrorMessage   = succeeded ? null : string.Concat("winget exit code ", exitCode, ": ", stderr),
            RebootRequired = rebootNeeded,
            AttemptedAt    = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Locates winget.exe on the system.
    /// The SYSTEM account does not have winget in its PATH — we search known locations.
    /// </summary>
    private static string? FindWinget()
    {
        // 1. Try PATH first (works for interactive user sessions).
        try
        {
            using var which = new System.Diagnostics.Process();
            which.StartInfo = new ProcessStartInfo
            {
                FileName               = "where.exe",
                Arguments              = "winget",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true
            };
            which.Start();
            var result = which.StandardOutput.ReadLine();
            which.WaitForExit();
            if (!string.IsNullOrWhiteSpace(result) && File.Exists(result.Trim()))
                return result.Trim();
        }
        catch { /* fall through */ }

        // 2. WindowsApps folder — where App Installer places winget on Win 10/11.
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "WindowsApps");
        if (Directory.Exists(windowsApps))
        {
            var match = Directory
                .GetDirectories(windowsApps, "Microsoft.DesktopAppInstaller_*_x64*")
                .OrderByDescending(d => d)
                .Select(d => Path.Combine(d, "winget.exe"))
                .FirstOrDefault(File.Exists);

            if (match is not null)
                return match;
        }

        // 3. SYSTEM profile WindowsApps fallback.
        var systemPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "config", "systemprofile", "AppData", "Local",
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(systemPath))
            return systemPath;

        return null;
    }

    /// <summary>
    /// Runs winget via a PowerShell wrapper script.
    /// Direct invocation of winget as SYSTEM fails with 0xC0000135 because winget
    /// is MSIX-packaged and its Visual C++ Runtime DLLs only load within the
    /// package activation context that PowerShell sets up automatically.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunWingetAsync(
        string arguments, CancellationToken cancellationToken)
    {
        var wingetPath = FindWinget();
        if (wingetPath is null)
        {
            LogConfig.ServiceLog.Warning(
                "WingetWorker: winget.exe not found. " +
                "Install App Installer from the Microsoft Store to enable application updates.");
            return (-1, string.Empty, "winget not found");
        }

        LogConfig.ServiceLog.Debug("WingetWorker: using winget at {Path}", wingetPath);
        LogConfig.ServiceLog.Debug("WingetWorker: winget {Args}", arguments);

        // Build the wrapper script as a verbatim string to avoid interpolation
        // conflicts between C# $ syntax and PowerShell $ variables.
        // The winget path and arguments are injected via placeholder replacement
        // rather than C# string interpolation.
        var script = BuildWingetScript(wingetPath, arguments);

        var tempScript = Path.Combine(Path.GetTempPath(), string.Concat("winget_", Guid.NewGuid().ToString("N"), ".ps1"));
        await File.WriteAllTextAsync(tempScript, script, cancellationToken);

        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = string.Concat("-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"", tempScript, "\""),
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            proc.Start();
            var rawOut = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var psErr  = await proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(psErr))
                LogConfig.ServiceLog.Warning("WingetWorker: PS wrapper stderr: {Err}", psErr.Trim());

            // Parse structured output back out using sentinel markers.
            var exitCode = 0;
            foreach (var line in rawOut.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("EXIT:", StringComparison.Ordinal)
                    && int.TryParse(trimmed.Substring(5), out var parsed))
                {
                    exitCode = parsed;
                    break;
                }
            }

            var stdout = ExtractSection(rawOut, "STDOUT_START", "STDOUT_END");
            var stderr = ExtractSection(rawOut, "STDERR_START", "STDERR_END");

            return (exitCode, stdout, stderr);
        }
        finally
        {
            try { File.Delete(tempScript); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Builds the PowerShell wrapper script using simple string replacement
    /// so we can use a verbatim @"..." string without C# interpolation
    /// conflicting with PowerShell's $variable syntax.
    /// </summary>
    private static string BuildWingetScript(string wingetPath, string arguments)
    {
        // Use a verbatim string so PowerShell backticks and $ variables are literal.
        // WINGET_PATH and WINGET_ARGS are replaced after the fact.
        const string template = @"
$ProgressPreference = 'SilentlyContinue'
$env:LOCALAPPDATA = ""$env:SystemRoot\System32\config\systemprofile\AppData\Local""
$env:APPDATA      = ""$env:SystemRoot\System32\config\systemprofile\AppData\Roaming""

$outFile = [System.IO.Path]::GetTempFileName()
$errFile = [System.IO.Path]::GetTempFileName()

try {
    $proc = Start-Process -FilePath 'WINGET_PATH' `
        -ArgumentList 'WINGET_ARGS' `
        -RedirectStandardOutput $outFile `
        -RedirectStandardError  $errFile `
        -Wait -NoNewWindow -PassThru

    $exitCode = $proc.ExitCode
    $stdout   = Get-Content $outFile -Raw -ErrorAction SilentlyContinue
    $stderr   = Get-Content $errFile -Raw -ErrorAction SilentlyContinue

    Write-Output ""EXIT:$exitCode""
    Write-Output 'STDOUT_START'
    if ($stdout) { Write-Output $stdout }
    Write-Output 'STDOUT_END'
    Write-Output 'STDERR_START'
    if ($stderr) { Write-Output $stderr }
    Write-Output 'STDERR_END'
} finally {
    Remove-Item $outFile -ErrorAction SilentlyContinue
    Remove-Item $errFile -ErrorAction SilentlyContinue
}
";
        // Escape single quotes in the path/args for PowerShell string embedding.
        var safePath = wingetPath.Replace("'", "''");
        var safeArgs = arguments.Replace("'", "''");

        return template
            .Replace("WINGET_PATH", safePath)
            .Replace("WINGET_ARGS", safeArgs);
    }

    private static string ExtractSection(string text, string startMarker, string endMarker)
    {
        var startIdx = text.IndexOf(startMarker, StringComparison.Ordinal);
        var endIdx   = text.IndexOf(endMarker,   StringComparison.Ordinal);
        if (startIdx < 0 || endIdx < 0 || endIdx <= startIdx)
            return string.Empty;
        return text
            .Substring(startIdx + startMarker.Length, endIdx - startIdx - startMarker.Length)
            .Trim();
    }

    private static void LogHistoryEntry(UpdateResult result)
    {
        if (result.Status == UpdateStatus.Succeeded)
            LogConfig.HistoryLog.Information(
                "WINGET | {Id} | Status={Status} | RebootRequired={Reboot} | {At:O}",
                result.Identifier, result.Status, result.RebootRequired, result.AttemptedAt);
        else
            LogConfig.HistoryLog.Warning(
                "WINGET | {Id} | Status={Status} | Error={Err} | {At:O}",
                result.Identifier, result.Status, result.ErrorMessage, result.AttemptedAt);
    }
}
