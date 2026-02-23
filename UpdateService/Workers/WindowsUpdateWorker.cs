// UpdateService/Workers/WindowsUpdateWorker.cs
// Uses PowerShell + the PSWindowsUpdate module to install Windows Updates,
// patches, and driver updates silently.  If the module is not installed it
// is installed automatically from the PowerShell Gallery.
// All individual update results (KB numbers, titles, status) are captured
// and returned so the orchestrator can log them and determine reboot need.

using System.Diagnostics;
using System.Text.Json;
using Shared.Models;
using UpdateService.Logging;

namespace UpdateService.Workers;

/// <summary>
/// Drives Windows Update via the PSWindowsUpdate PowerShell module.
/// </summary>
public static class WindowsUpdateWorker
{
    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads and installs all available Windows Updates, patches, and drivers.
    /// </summary>
    /// <param name="cancellationToken">Used to abort the PowerShell process.</param>
    /// <returns>One <see cref="UpdateResult"/> per update attempted.</returns>
    public static async Task<List<UpdateResult>> RunAsync(CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information("WindowsUpdateWorker: starting update pass.");

        // Ensure PSWindowsUpdate is available before running the update script.
        await EnsureModuleInstalledAsync(cancellationToken);

        // The PowerShell script installs all updates and emits a JSON array of results.
        var script = BuildUpdateScript();
        var (exitCode, stdout, stderr) = await RunPowerShellAsync(script, cancellationToken);

        if (!string.IsNullOrWhiteSpace(stderr))
            LogConfig.ServiceLog.Warning("WindowsUpdateWorker: PS stderr: {Err}", stderr.Trim());

        var results = ParseResults(stdout);

        LogConfig.ServiceLog.Information(
            "WindowsUpdateWorker: pass complete. Total={T} Succeeded={S} Failed={F}",
            results.Count,
            results.Count(r => r.Status == UpdateStatus.Succeeded),
            results.Count(r => r.Status == UpdateStatus.Failed));

        // Write every result to the dedicated history log.
        foreach (var r in results)
            LogHistoryEntry(r);

        return results;
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Installs the PSWindowsUpdate module from the Gallery if it is not already present.
    /// Runs in AllSigned bypass scope so it works on systems with restrictive execution policies.
    /// </summary>
    private static async Task EnsureModuleInstalledAsync(CancellationToken cancellationToken)
    {
        const string checkScript = @"
            if (-not (Get-Module -ListAvailable -Name PSWindowsUpdate)) {
                Install-Module PSWindowsUpdate -Force -SkipPublisherCheck -Scope AllUsers
            }
            Write-Output 'OK'
        ";

        LogConfig.ServiceLog.Information("WindowsUpdateWorker: verifying PSWindowsUpdate module.");
        var (_, stdout, _) = await RunPowerShellAsync(checkScript, cancellationToken);

        if (!stdout.Contains("OK", StringComparison.OrdinalIgnoreCase))
            LogConfig.ServiceLog.Warning(
                "WindowsUpdateWorker: PSWindowsUpdate module check returned unexpected output: {Out}",
                stdout.Trim());
        else
            LogConfig.ServiceLog.Information("WindowsUpdateWorker: PSWindowsUpdate module OK.");
    }

    /// <summary>
    /// Builds the PowerShell script that installs all pending updates and
    /// emits a JSON array describing each result.
    /// </summary>
    private static string BuildUpdateScript() => @"
        Import-Module PSWindowsUpdate -ErrorAction Stop

        # Accept all updates including drivers and optional patches.
        $updates = Get-WindowsUpdate -AcceptAll -IgnoreReboot -ErrorAction Continue 2>&1

        $results = @()

        foreach ($u in $updates) {
            $status  = if ($u.Result -eq 'Installed') { 'Succeeded' } else { 'Failed' }
            $kb      = if ($u.KBArticleIDs.Count -gt 0) { $u.KBArticleIDs[0] } else { '' }

            $results += [PSCustomObject]@{
                Identifier    = $kb
                Title         = $u.Title
                Status        = $status
                ErrorMessage  = ''
                RebootRequired = $u.RebootRequired
                AttemptedAt   = (Get-Date -Format 'o')
            }
        }

        # Always output valid JSON even if $results is empty.
        if ($results.Count -eq 0) {
            Write-Output '[]'
        } else {
            $results | ConvertTo-Json -Compress
        }
    ";

    /// <summary>
    /// Parses the JSON emitted by the PowerShell script into a list of <see cref="UpdateResult"/>.
    /// Falls back to an empty list if the output is malformed.
    /// </summary>
    private static List<UpdateResult> ParseResults(string json)
    {
        try
        {
            // Isolate the JSON line — PowerShell may emit progress/verbose lines first.
            var jsonLine = json
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(l => l.TrimStart().StartsWith('[') || l.TrimStart().StartsWith('{'))
                ?? "[]";

            // PowerShell may emit a single object instead of an array for one update.
            if (jsonLine.TrimStart().StartsWith('{'))
                jsonLine = $"[{jsonLine}]";

            var dtos = JsonSerializer.Deserialize<List<WuUpdateDto>>(jsonLine,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? [];

            return dtos.Select(d => new UpdateResult
            {
                Identifier    = d.Identifier ?? string.Empty,
                Title         = d.Title       ?? string.Empty,
                Status        = Enum.TryParse<UpdateStatus>(d.Status, out var s) ? s : UpdateStatus.Failed,
                ErrorMessage  = string.IsNullOrWhiteSpace(d.ErrorMessage) ? null : d.ErrorMessage,
                RebootRequired = d.RebootRequired,
                AttemptedAt   = DateTime.TryParse(d.AttemptedAt, out var dt) ? dt : DateTime.UtcNow
            }).ToList();
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "WindowsUpdateWorker: failed to parse PS output. Raw: {Json}", json);
            return [];
        }
    }

    /// <summary>
    /// Runs a PowerShell script string using powershell.exe and captures all output.
    /// ExecutionPolicy is bypassed for this process only.
    /// </summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellAsync(
        string script, CancellationToken cancellationToken)
    {
        // Write the script to a temp file to avoid command-line length limits.
        var tempScript = Path.Combine(Path.GetTempPath(), $"wuworker_{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(tempScript, script, cancellationToken);

        try
        {
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName               = "powershell.exe",
                Arguments              = $"-NonInteractive -NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\"",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };

            LogConfig.ServiceLog.Debug("WindowsUpdateWorker: running PS script: {Script}", script.Trim());

            proc.Start();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            return (proc.ExitCode, await stdoutTask, await stderrTask);
        }
        finally
        {
            // Clean up the temp script file.
            try { File.Delete(tempScript); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Logs one update result to the dedicated update-history sink.
    /// </summary>
    private static void LogHistoryEntry(UpdateResult r)
    {
        if (r.Status == UpdateStatus.Succeeded)
        {
            LogConfig.HistoryLog.Information(
                "WINDOWS-UPDATE | KB={KB} | Title={Title} | RebootRequired={Reboot} | {At:O}",
                r.Identifier, r.Title, r.RebootRequired, r.AttemptedAt);
        }
        else
        {
            LogConfig.HistoryLog.Warning(
                "WINDOWS-UPDATE | KB={KB} | Title={Title} | Status=Failed | Error={Err} | {At:O}",
                r.Identifier, r.Title, r.ErrorMessage, r.AttemptedAt);
        }
    }

    // ── Private DTO used only for JSON deserialisation ───────────────────────

    private sealed class WuUpdateDto
    {
        public string? Identifier     { get; set; }
        public string? Title          { get; set; }
        public string? Status         { get; set; }
        public string? ErrorMessage   { get; set; }
        public bool    RebootRequired { get; set; }
        public string? AttemptedAt    { get; set; }
    }
}