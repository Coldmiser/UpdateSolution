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

    /// <summary>
    /// Checks Windows registry keys that WUA sets when a reboot is required
    /// before further updates can be installed.
    /// </summary>
    private static bool IsRebootPending()
    {
        try
        {
            using var wuKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (wuKey != null) return true;

            using var cbsKey = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            if (cbsKey != null) return true;

            using var smKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (smKey?.GetValue("PendingFileRenameOperations") != null) return true;
        }
        catch { /* best effort */ }

        return false;
    }

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