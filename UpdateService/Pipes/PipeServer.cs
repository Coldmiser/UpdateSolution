// UpdateService/Pipes/PipeServer.cs
// The service acts as the named-pipe SERVER.
// When updates require a reboot it:
//   1. Launches UpdateNotifier.exe as the active desktop user.
//   2. Waits for the notifier to connect to the pipe.
//   3. Sends a RebootRequired message.
//   4. Receives the user's snooze/reboot-now response.
//   5. Either schedules the next notification or initiates a reboot.

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shared.Constants;
using Shared.Models;
using UpdateService.Logging;
using UpdateService.Process;
using UpdateService.Workers;

namespace UpdateService.Pipes;

/// <summary>
/// Manages one named-pipe conversation cycle with the WPF notifier.
/// </summary>
public sealed class PipeServer
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private readonly string _notifierPath;

    // ── Constructor ──────────────────────────────────────────────────────────

    public PipeServer(string notifierPath)
    {
        _notifierPath = notifierPath;
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Notifies the logged-in user that a reboot is required.
    /// Launches the WPF notifier, sends the update details, and waits for
    /// the user's decision.  Re-queues a snooze timer if the user defers.
    /// Abandons after 96 consecutive failed attempts (≈ 24 hours with no active user session).
    /// </summary>
    public async Task NotifyRebootRequiredAsync(
        List<UpdateResult> results, CancellationToken cancellationToken)
    {
        // Build the message we will send to the WPF notifier.
        var message = new PipeMessage
        {
            Type            = MessageType.RebootRequired,
            UpdateSummary   = UpdateOrchestrator.BuildSummary(results),
            KbNumbers       = UpdateOrchestrator.ExtractKbNumbers(results),
            UpdatedPackages = UpdateOrchestrator.ExtractPackageIds(results),
            Timestamp       = DateTime.UtcNow
        };

        // Keep looping until the user chooses "Reboot Now" (snoozeMinutes == 0).
        // Cap consecutive failures so the outer update loop is not blocked indefinitely
        // when there is never an active user session (e.g. a headless/RDP-only server).
        const int maxConsecutiveFailures = 96; // 96 × 15 min ≈ 24 hours
        var consecutiveFailures = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await RunOneNotificationCycleAsync(message, cancellationToken);

            if (response is null)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= maxConsecutiveFailures)
                {
                    LogConfig.ServiceLog.Error(
                        "PipeServer: {N} consecutive notification failures (~24 h). " +
                        "Giving up — reboot must be triggered manually.", consecutiveFailures);
                    return;
                }

                LogConfig.ServiceLog.Warning(
                    "PipeServer: no response received ({N}/{Max}) — retrying in 15 minutes.",
                    consecutiveFailures, maxConsecutiveFailures);
                await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
                continue;
            }

            consecutiveFailures = 0; // reset on any successful interaction

            if (response.SnoozeMinutes == 0)
            {
                // User chose "Reboot Now".
                LogConfig.ServiceLog.Information("PipeServer: user accepted reboot — initiating shutdown.");
                InitiateReboot();
                return;
            }

            // User chose to snooze.
            LogConfig.ServiceLog.Information(
                "PipeServer: user snoozed for {Min} minutes.", response.SnoozeMinutes);
            await Task.Delay(TimeSpan.FromMinutes(response.SnoozeMinutes), cancellationToken);
            // Loop back to show the notification again.
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Launches the notifier, waits for it to connect, exchanges one
    /// request/response pair, and returns the user's decision.
    /// Returns null if communication fails.
    /// </summary>
    private async Task<PipeMessage?> RunOneNotificationCycleAsync(
        PipeMessage outbound, CancellationToken cancellationToken)
    {
        // Create the pipe first so the notifier can connect immediately after launch.
        using var pipeServer = new NamedPipeServerStream(
            AppConstants.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous);

        LogConfig.ServiceLog.Information("PipeServer: pipe created — launching notifier.");

        // Launch the notifier as the interactive user.
        if (!UserProcessLauncher.LaunchAsActiveUser(_notifierPath))
        {
            LogConfig.ServiceLog.Warning("PipeServer: could not launch notifier — no active user session.");
            return null;
        }

        // Wait for the notifier to connect (60-second window).
        LogConfig.ServiceLog.Debug("PipeServer: waiting for notifier to connect.");
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            await pipeServer.WaitForConnectionAsync(connectCts.Token);
        }
        catch (OperationCanceledException)
        {
            LogConfig.ServiceLog.Warning("PipeServer: notifier did not connect within 60 seconds.");
            return null;
        }

        LogConfig.ServiceLog.Information("PipeServer: notifier connected.");

        // ── Send RebootRequired message ──────────────────────────────────────
        try
        {
            await WriteMessageAsync(pipeServer, outbound, cancellationToken);
            LogConfig.ServiceLog.Debug("PipeServer: RebootRequired message sent.");
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "PipeServer: failed to send message to notifier.");
            return null;
        }

        // ── Wait for the user's response ─────────────────────────────────────
        try
        {
            using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            responseCts.CancelAfter(TimeSpan.FromHours(8)); // give the user plenty of time

            var response = await ReadMessageAsync(pipeServer, responseCts.Token);
            LogConfig.ServiceLog.Information(
                "PipeServer: received UserResponse — SnoozeMinutes={Min}", response?.SnoozeMinutes);
            return response;
        }
        catch (Exception ex)
        {
            LogConfig.ServiceLog.Error(ex, "PipeServer: error reading user response from notifier.");
            return null;
        }
    }

    /// <summary>
    /// Serialises a <see cref="PipeMessage"/> to JSON and writes it to the pipe
    /// using a 4-byte length prefix so the reader knows exactly how many bytes to expect.
    /// </summary>
    private static async Task WriteMessageAsync(
        PipeStream pipe, PipeMessage message, CancellationToken ct)
    {
        var json  = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var len   = BitConverter.GetBytes(bytes.Length); // 4-byte little-endian prefix

        await pipe.WriteAsync(len, ct);
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);
    }

    /// <summary>
    /// Reads a length-prefixed JSON message from the pipe and deserialises it.
    /// Rejects messages whose declared body length exceeds <see cref="AppConstants.MaxPipeMessageBytes"/>.
    /// </summary>
    private static async Task<PipeMessage?> ReadMessageAsync(PipeStream pipe, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await pipe.ReadExactlyAsync(lenBuf, ct);
        var bodyLen = BitConverter.ToInt32(lenBuf);

        if (bodyLen <= 0 || bodyLen > AppConstants.MaxPipeMessageBytes)
            throw new InvalidDataException(
                $"Pipe message body length {bodyLen} is outside the valid range " +
                $"[1, {AppConstants.MaxPipeMessageBytes}].");

        var bodyBuf = new byte[bodyLen];
        await pipe.ReadExactlyAsync(bodyBuf, ct);

        var json = Encoding.UTF8.GetString(bodyBuf);
        return JsonSerializer.Deserialize<PipeMessage>(json);
    }

    /// <summary>
    /// Calls shutdown.exe to reboot the machine in 30 seconds,
    /// giving processes time to save and close.
    /// </summary>
    private static void InitiateReboot()
    {
        LogConfig.ServiceLog.Warning("PipeServer: executing system reboot in 30 seconds.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "shutdown.exe",
            Arguments       = "/r /t 30 /c \"CapTG Update Service: Restarting to apply updates.\"",
            CreateNoWindow  = true,
            UseShellExecute = false
        });
    }
}