// UpdateService/Workers/WindowsUpdateWorker.cs
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using Shared.Models;
using UpdateService.Logging;

namespace UpdateService.Workers;

public static class WindowsUpdateWorker
{
    public static async Task<List<UpdateResult>> RunAsync(CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information("WindowsUpdateWorker: starting update pass.");
        await EnsureModuleInstalledAsync(cancellationToken);

        var script = BuildUpdateScript();
        var (exitCode, stdout, stderr) = await RunPowerShellAsync(script, cancellationToken);

        if (!string.IsNullOrWhiteSpace(stderr))
            LogConfig.ServiceLog.Warning("WindowsUpdateWorker: PS stderr: {Err}", stderr.Trim());

        var results = ParseResults(stdout);

        // If nothing installed this cycle but the system already has a pending reboot
        // (from a previous cycle), inject a sentinel so the orchestrator re-prompts
        // the user to reboot — which unblocks WUA from installing the queued updates.
        if (!results.Any(r => r.Status == UpdateStatus.Succeeded) && IsRebootPending())
        {
            LogConfig.ServiceLog.Information(
                "WindowsUpdateWorker: no updates installed — system reboot is already pending.");
            results.Add(new UpdateResult
            {
                Identifier     = "PendingReboot",
                Title          = "Reboot pending from a previous update cycle",
                Status         = UpdateStatus.Succeeded,
                RebootRequired = true,
                AttemptedAt    = DateTime.UtcNow
            });
        }

        LogConfig.ServiceLog.Information(
            "WindowsUpdateWorker: pass complete. Total={T} Succeeded={S} Failed={F} Skipped={K}",
            results.Count,
            results.Count(r => r.Status == UpdateStatus.Succeeded),
            results.Count(r => r.Status == UpdateStatus.Failed),
            results.Count(r => r.Status == UpdateStatus.Skipped));

        foreach (var r in results)
            LogHistoryEntry(r);

        return results;
    }

    private static async Task EnsureModuleInstalledAsync(CancellationToken cancellationToken)
    {
        const string checkScript = @"
            $ProgressPreference = 'SilentlyContinue'
            $ErrorActionPreference = 'Stop'
            try {
                if (-not (Get-PackageProvider -Name NuGet -ListAvailable -ErrorAction SilentlyContinue)) {
                    Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
                }
                Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue
                if (-not (Get-Module -ListAvailable -Name PSWindowsUpdate -ErrorAction SilentlyContinue)) {
                    Install-Module PSWindowsUpdate -Force -SkipPublisherCheck -Scope AllUsers | Out-Null
                }
                Import-Module PSWindowsUpdate -Force -ErrorAction Stop
                try {
                    Add-WUServiceManager -MicrosoftUpdate -Confirm:$false -ErrorAction SilentlyContinue | Out-Null
                } catch {}
                Write-Output 'OK'
            } catch {
                Write-Output ('ERROR: ' + $_.Exception.Message)
            }
        ";

        LogConfig.ServiceLog.Information("WindowsUpdateWorker: verifying PSWindowsUpdate module.");
        var (exitCode, stdout, stderr) = await RunPowerShellAsync(checkScript, cancellationToken);

        if (!string.IsNullOrWhiteSpace(stderr))
            LogConfig.ServiceLog.Warning("WindowsUpdateWorker: module check stderr: {Err}", stderr.Trim());

        if (stdout.Contains("OK", StringComparison.OrdinalIgnoreCase))
            LogConfig.ServiceLog.Information("WindowsUpdateWorker: PSWindowsUpdate module OK.");
        else
            LogConfig.ServiceLog.Warning(
                "WindowsUpdateWorker: module check did not return OK. ExitCode={Code} Output={Out}",
                exitCode, stdout.Trim());
    }

    private static string BuildUpdateScript() => @"
        Import-Module PSWindowsUpdate -ErrorAction Stop
        $ProgressPreference = 'SilentlyContinue'

        # Force the Windows Update Agent to scan before installing.
        # Fixes 0x80248007 (WU_E_DS_NODATA) on fresh builds or machines
        # whose update data store has not been initialised yet.
        try {
            Start-Process -FilePath 'UsoClient.exe' `
                -ArgumentList 'StartScan' `
                -Wait -NoNewWindow -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 30
        } catch {}

        # 2>&1 merges stderr into the pipeline mixing strings with update
        # objects -- we filter them out in the foreach loop below.
        $rawOutput = Install-WindowsUpdate `
            -MicrosoftUpdate `
            -AcceptAll `
            -IgnoreReboot `
            -Install `
            -ErrorAction Continue 2>&1

        # Use a hashtable keyed on Title to keep only the LAST result per update.
        # PSWindowsUpdate emits objects at multiple pipeline stages (search, download,
        # install); overwriting ensures we capture the final post-install ResultCode
        # rather than an early ResultCode=0 (NotStarted).
        $byKey = @{}

        foreach ($u in $rawOutput) {
            if ($null -eq $u -or $u -isnot [PSObject]) { continue }
            if (-not ($u.PSObject.Properties.Name -contains 'Title')) { continue }
            if ([string]::IsNullOrWhiteSpace($u.Title)) { continue }

            $resultCode = 0
            try { $resultCode = [int]$u.ResultCode } catch { $resultCode = 4 }
            $succeeded = ($resultCode -eq 2 -or $resultCode -eq 3)

            # ResultCode=0 means NotStarted — treat as Skipped, not Failed.
            $status = if ($succeeded) { 'Succeeded' } elseif ($resultCode -eq 0) { 'Skipped' } else { 'Failed' }

            $kb = ''
            try {
                if ($u.KBArticleIDs -and $u.KBArticleIDs.Count -gt 0) {
                    $kb = 'KB' + $u.KBArticleIDs[0]
                }
            } catch {}

            $needsReboot = $false
            try { $needsReboot = [bool]$u.RebootRequired } catch {}

            $byKey[$u.Title] = [PSCustomObject]@{
                Identifier     = $kb
                Title          = [string]$u.Title
                Status         = $status
                ErrorMessage   = if ($succeeded -or $resultCode -eq 0) { '' } else { 'ResultCode=' + $resultCode }
                RebootRequired = $needsReboot
                AttemptedAt    = (Get-Date -Format 'o')
            }
        }

        $results = @($byKey.Values)

        if ($results.Count -eq 0) {
            Write-Output '[]'
        } else {
            $results | ConvertTo-Json -Compress
        }
    ";

    private static List<UpdateResult> ParseResults(string json)
    {
        try
        {
            var jsonLine = json
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .LastOrDefault(l => l.TrimStart().StartsWith('[') || l.TrimStart().StartsWith('{'))
                ?? "[]";

            if (jsonLine.TrimStart().StartsWith('{'))
                jsonLine = $"[{jsonLine}]";

            var dtos = JsonSerializer.Deserialize<List<WuUpdateDto>>(jsonLine,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? [];

            return dtos.Select(d => new UpdateResult
            {
                Identifier     = d.Identifier ?? string.Empty,
                Title          = d.Title       ?? string.Empty,
                Status         = Enum.TryParse<UpdateStatus>(d.Status, out var s) ? s : UpdateStatus.Failed,
                ErrorMessage   = string.IsNullOrWhiteSpace(d.ErrorMessage) ? null : d.ErrorMessage,
                RebootRequired = d.RebootRequired,
                AttemptedAt    = DateTime.TryParse(d.AttemptedAt, out var dt) ? dt : DateTime.UtcNow
            }).ToList();
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex,
                "WindowsUpdateWorker: failed to parse PS output. Raw: {Json}", json);
            return [];
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellAsync(
        string script, CancellationToken cancellationToken)
    {
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
            try { File.Delete(tempScript); } catch { /* best effort */ }
        }
    }

    private static void LogHistoryEntry(UpdateResult r)
    {
        if (r.Status == UpdateStatus.Succeeded)
            LogConfig.HistoryLog.Information(
                "WINDOWS-UPDATE | KB={KB} | Title={Title} | RebootRequired={Reboot} | {At:O}",
                r.Identifier, r.Title, r.RebootRequired, r.AttemptedAt);
        else if (r.Status == UpdateStatus.Skipped)
            LogConfig.HistoryLog.Information(
                "WINDOWS-UPDATE | KB={KB} | Title={Title} | Status=Skipped | {At:O}",
                r.Identifier, r.Title, r.AttemptedAt);
        else
            LogConfig.HistoryLog.Warning(
                "WINDOWS-UPDATE | KB={KB} | Title={Title} | Status=Failed | Error={Err} | {At:O}",
                r.Identifier, r.Title, r.ErrorMessage, r.AttemptedAt);
    }

    // ── Pending-reboot registry indicators ───────────────────────────────────

    private const string WuRebootKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired";
    private const string CbsRebootKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending";
    private const string SessionManagerKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager";

    // Snapshot of the three pending-reboot registry indicators taken when the
    // service starts. An indicator that was already present at startup and has
    // not changed since is considered stale — it does not, on its own, trigger
    // a reboot suggestion. Null until CaptureRebootBaseline() runs, in which
    // case any present indicator counts as pending (legacy behavior).
    private static (string? Wu, string? Cbs, string? Pfro)? _rebootBaseline;

    /// <summary>
    /// Records the current state of the pending-reboot registry indicators.
    /// Called once at service start by <c>UpdateBackgroundService</c>.
    /// </summary>
    public static void CaptureRebootBaseline()
    {
        var snapshot = ReadRebootIndicators();
        _rebootBaseline = snapshot;

        LogConfig.ServiceLog.Information(
            "WindowsUpdateWorker: pending-reboot baseline captured at service start. " +
            "WU-RebootRequired={Wu} CBS-RebootPending={Cbs} PendingFileRenameOperations={Pfro}",
            snapshot.Wu  is null ? "absent" : "present",
            snapshot.Cbs is null ? "absent" : "present",
            snapshot.Pfro is null ? "absent" : "present");
    }

    /// <summary>
    /// Checks Windows registry keys that WUA sets when a reboot is required
    /// before further updates can be installed. An indicator only counts as
    /// pending when it is present AND has appeared or changed since the
    /// baseline captured at service start — pre-existing, unchanged flags are
    /// treated as stale and ignored.
    /// </summary>
    private static bool IsRebootPending()
    {
        var current  = ReadRebootIndicators();
        var baseline = _rebootBaseline;

        var wuPending   = IsIndicatorPending(current.Wu,   baseline?.Wu);
        var cbsPending  = IsIndicatorPending(current.Cbs,  baseline?.Cbs);
        var pfroPending = IsIndicatorPending(current.Pfro, baseline?.Pfro);

        // Log indicators that are present but suppressed as stale, for auditability.
        if (!wuPending && current.Wu != null)
            LogConfig.ServiceLog.Information(
                "WindowsUpdateWorker: WU RebootRequired key present but unchanged since service start — ignoring as stale.");
        if (!cbsPending && current.Cbs != null)
            LogConfig.ServiceLog.Information(
                "WindowsUpdateWorker: CBS RebootPending key present but unchanged since service start — ignoring as stale.");
        if (!pfroPending && current.Pfro != null)
            LogConfig.ServiceLog.Information(
                "WindowsUpdateWorker: PendingFileRenameOperations present but unchanged since service start — ignoring as stale.");

        return wuPending || cbsPending || pfroPending;
    }

    /// <summary>
    /// An indicator is pending when it exists now and either was absent at
    /// baseline (appeared after service start), no baseline was captured,
    /// or its contents have changed since the baseline.
    /// </summary>
    private static bool IsIndicatorPending(string? current, string? baseline)
    {
        if (current is null) return false;   // indicator absent — nothing pending
        if (baseline is null) return true;   // appeared after start (or no baseline)
        return !string.Equals(current, baseline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a content fingerprint for each of the three indicators.
    /// Null = indicator absent (or unreadable — best effort, as before).
    /// </summary>
    private static (string? Wu, string? Cbs, string? Pfro) ReadRebootIndicators()
        => (FingerprintKey(WuRebootKeyPath),
            FingerprintKey(CbsRebootKeyPath),
            FingerprintPendingFileRenames());

    /// <summary>
    /// Returns a stable fingerprint of a registry key's subkey names and
    /// values, or null when the key does not exist.
    /// </summary>
    private static string? FingerprintKey(string subKeyPath)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(subKeyPath);
            if (key is null) return null;

            var parts = new List<string>();
            foreach (var name in key.GetSubKeyNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                parts.Add("K:" + name);
            foreach (var name in key.GetValueNames().OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                parts.Add("V:" + name + "=" + FormatRegistryValue(key.GetValue(name)));

            return string.Join("|", parts);
        }
        catch { return null; /* best effort */ }
    }

    /// <summary>
    /// Returns the contents of Session Manager\PendingFileRenameOperations,
    /// or null when the value is not set.
    /// </summary>
    private static string? FingerprintPendingFileRenames()
    {
        try
        {
            using var smKey = Registry.LocalMachine.OpenSubKey(SessionManagerKeyPath);
            var value = smKey?.GetValue("PendingFileRenameOperations");
            return value is null ? null : FormatRegistryValue(value);
        }
        catch { return null; /* best effort */ }
    }

    private static string FormatRegistryValue(object? value) => value switch
    {
        null              => string.Empty,
        string[] multiSz  => string.Join("\n", multiSz),
        byte[] bytes      => Convert.ToBase64String(bytes),
        _                 => value.ToString() ?? string.Empty
    };

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