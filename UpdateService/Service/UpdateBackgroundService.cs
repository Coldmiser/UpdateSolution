// UpdateService/Service/UpdateBackgroundService.cs
// The .NET BackgroundService that drives the entire update loop.
// Registered with UseWindowsService() so the .NET host handles
// Start/Stop signals from the Windows Service Control Manager.

using Microsoft.Extensions.Hosting;
using Shared.Constants;
using Shared.Helpers;
using UpdateService.Logging;
using UpdateService.Pipes;
using UpdateService.SelfUpdate;
using UpdateService.Workers;

namespace UpdateService.Service;

/// <summary>
/// Long-running hosted service: waits for the configurable interval,
/// runs the update cycle, and notifies the user if a reboot is needed.
/// </summary>
public sealed class UpdateBackgroundService : BackgroundService
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly SelfUpdater _selfUpdater;
    private readonly PipeServer  _pipeServer;
    private readonly TimeSpan    _updateInterval;

    // ── Constructor ──────────────────────────────────────────────────────────

    public UpdateBackgroundService(SelfUpdater selfUpdater, PipeServer pipeServer)
    {
        _selfUpdater    = selfUpdater;
        _pipeServer     = pipeServer;

        // Allow the interval to be overridden from the registry (in minutes).
        var minutes = RegistryHelper.GetInt(
            RegistryConstants.UpdateIntervalMinutes,
            (int)AppConstants.UpdateInterval.TotalMinutes);

        _updateInterval = TimeSpan.FromMinutes(Math.Max(5, minutes)); // floor at 5 minutes
        LogConfig.ServiceLog.Information(
            "UpdateBackgroundService: update interval = {Interval}", _updateInterval);
    }

    // ── BackgroundService override ───────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogConfig.ServiceLog.Information(
            "UpdateBackgroundService: service started. Initial delay = {Delay}.",
            AppConstants.InitialDelay);

        // Snapshot the pending-reboot registry indicators now, so flags that
        // already existed at service start (and never change) are treated as
        // stale and do not trigger reboot prompts on their own.
        WindowsUpdateWorker.CaptureRebootBaseline();

        // Honour a user-scheduled "Reboot at 5:30 PM" that was persisted before
        // a sleep, power-off, or service restart. If the scheduled time was
        // missed (machine off/asleep at 5:30), this reboots the PC now — after
        // the standard 5-minute warning — and does not return until then.
        try
        {
            await _pipeServer.ResumeScheduledRebootIfPendingAsync(stoppingToken);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex,
                "UpdateBackgroundService: failed to resume pending scheduled reboot.");
        }

        // Short delay after start so the system has time to settle.
        await Task.Delay(AppConstants.InitialDelay, stoppingToken);

        // Build the orchestrator, wiring the reboot callback to the pipe server.
        var orchestrator = new UpdateOrchestrator(
            results => _pipeServer.NotifyRebootRequiredAsync(results, stoppingToken));

        while (!stoppingToken.IsCancellationRequested)
        {
            // ── Self-update check ────────────────────────────────────────────
            try
            {
                await _selfUpdater.CheckAndApplyAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogConfig.ServiceLog.Error(ex, "UpdateBackgroundService: self-update check failed.");
            }

            // ── Update cycle ─────────────────────────────────────────────────
            try
            {
                await orchestrator.RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogConfig.ServiceLog.Error(ex,
                    "UpdateBackgroundService: unhandled exception in update cycle — will retry next interval.");
            }

            // ── Wait before next cycle ───────────────────────────────────────
            LogConfig.ServiceLog.Information(
                "UpdateBackgroundService: next cycle in {Interval}.", _updateInterval);

            try
            {
                await Task.Delay(_updateInterval, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }

        LogConfig.ServiceLog.Information("UpdateBackgroundService: stopping.");
    }

    // ── IHostedService overrides for logging ─────────────────────────────────

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information("UpdateBackgroundService: StartAsync called by SCM.");
        return base.StartAsync(cancellationToken);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        LogConfig.ServiceLog.Information("UpdateBackgroundService: StopAsync called by SCM.");
        return base.StopAsync(cancellationToken);
    }
}
