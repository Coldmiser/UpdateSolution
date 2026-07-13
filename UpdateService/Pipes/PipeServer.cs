// UpdateService/Pipes/PipeServer.cs
// The service acts as the named-pipe SERVER.
// When updates require a reboot it:
//   1. Launches UpdateNotifier.exe as the active desktop user.
//   2. Waits for the notifier to connect to the pipe.
//   3. Sends a RebootRequired message.
//   4. Receives the user's snooze/reboot-now response.
//   5. Either schedules the next notification or initiates a reboot.

using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Shared.Constants;
using Shared.Helpers;
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
    /// </summary>
    public async Task NotifyRebootRequiredAsync(
        List<UpdateResult> results, CancellationToken cancellationToken)
    {
        // Servers never show the notifier UI — they reboot silently at the
        // configured RebootTime. This runs in the service (SYSTEM) so it works
        // with no user logged in and always has shutdown privilege.
        if (IsServerInstall())
        {
            var rebootAt = GetNextServerRebootTime();
            LogConfig.ServiceLog.Information(
                "PipeServer: server install — silent reboot scheduled for {RebootAt}.", rebootAt);

            await Task.Delay(rebootAt - DateTime.Now, cancellationToken);

            LogConfig.ServiceLog.Information("PipeServer: server reboot time reached.");
            InitiateReboot();
            return;
        }

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
        int snoozeCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Tell the notifier how many times the user has already snoozed so it
            // can restore the correct remaining options on each relaunch.
            message.SnoozeCount = snoozeCount;

            var response = await RunOneNotificationCycleAsync(message, cancellationToken);

            if (response is null)
            {
                // Notifier did not respond (crashed, timed out, or no user session).
                // Retry after 15 minutes.
                LogConfig.ServiceLog.Warning(
                    "PipeServer: no response received — retrying notification in 15 minutes.");
                await Task.Delay(TimeSpan.FromMinutes(15), cancellationToken);
                continue;
            }

            if (response.SnoozeMinutes == AppConstants.ScheduledRebootSentinel)
            {
                // User chose "Reboot at 5:30 PM" — the service now owns the
                // schedule; no further input is required from the user.
                await RunScheduledRebootAsync(cancellationToken);
                return;
            }

            if (response.SnoozeMinutes == 0)
            {
                // User chose "Reboot Now".
                LogConfig.ServiceLog.Information("PipeServer: user accepted reboot — initiating shutdown.");
                InitiateReboot();
                return;
            }

            // User chose to snooze.
            snoozeCount++;
            LogConfig.ServiceLog.Information(
                "PipeServer: user snoozed for {Min} minutes (snoozeCount={Count}).",
                response.SnoozeMinutes, snoozeCount);
            await Task.Delay(TimeSpan.FromMinutes(response.SnoozeMinutes), cancellationToken);
            // Loop back to show the notification again.
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Handles the "Reboot at 5:30 PM" choice. Persists the target time to the
    /// registry (so sleep, power-off, or a service restart cannot lose it),
    /// waits until 5 minutes before the scheduled time, shows the short
    /// countdown warning, honours a single 5-minute delay if requested, then
    /// forces the reboot — no user interaction is required after selection.
    /// </summary>
    private async Task RunScheduledRebootAsync(CancellationToken cancellationToken)
    {
        // Next occurrence of 5:30 PM local time (today, or tomorrow if passed).
        var now    = DateTime.Now;
        var target = DateTime.Today.Add(AppConstants.ScheduledRebootTime);
        if (target <= now)
            target = target.AddDays(1);

        // Persist so the schedule survives sleep / power-off / service restart.
        // If the machine is off or asleep at the scheduled time, the service
        // finds this overdue value on wake/startup and reboots then.
        RegistryHelper.SetString(
            RegistryConstants.ScheduledRebootUtc, target.ToUniversalTime().ToString("o"));

        LogConfig.ServiceLog.Information(
            "PipeServer: user scheduled reboot for {Target} — persisted to registry.", target);

        await RunScheduledRebootCoreAsync(target, cancellationToken);
    }

    /// <summary>
    /// Checks the registry for a scheduled reboot that was persisted before a
    /// sleep, power-off, or service restart and has not happened yet. Called
    /// by <c>UpdateBackgroundService</c> at service start. When one exists,
    /// this resumes the countdown (overdue targets reboot after a fresh
    /// 5-minute warning) and does not return until the reboot is initiated.
    /// </summary>
    public async Task ResumeScheduledRebootIfPendingAsync(CancellationToken cancellationToken)
    {
        var raw = RegistryHelper.GetString(RegistryConstants.ScheduledRebootUtc, string.Empty);
        if (string.IsNullOrEmpty(raw))
            return;

        if (!DateTime.TryParse(raw, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var targetUtc))
        {
            LogConfig.ServiceLog.Warning(
                "PipeServer: ScheduledRebootUtc value '{Raw}' is unparseable — clearing it.", raw);
            RegistryHelper.DeleteValue(RegistryConstants.ScheduledRebootUtc);
            return;
        }

        var target = targetUtc.ToLocalTime();
        LogConfig.ServiceLog.Information(
            "PipeServer: pending scheduled reboot found for {Target} (machine was off, asleep, " +
            "or service restarted) — resuming.", target);

        await RunScheduledRebootCoreAsync(target, cancellationToken);
    }

    /// <summary>
    /// Core of the scheduled-reboot flow: waits for the warning time, shows the
    /// countdown popup, honours one 5-minute delay, then forces the reboot.
    /// If the target has already passed (machine was asleep or powered off at
    /// 5:30), the reboot runs now — after a fresh full-length warning so the
    /// user who just woke the machine isn't rebooted without notice.
    /// </summary>
    private async Task RunScheduledRebootCoreAsync(
        DateTime target, CancellationToken cancellationToken)
    {
        // Overdue (woke up / booted after the scheduled time): reboot now,
        // preceded by the standard 5-minute warning window.
        if (target <= DateTime.Now)
        {
            target = DateTime.Now.AddMinutes(AppConstants.ScheduledRebootWarningMinutes);
            RegistryHelper.SetString(
                RegistryConstants.ScheduledRebootUtc, target.ToUniversalTime().ToString("o"));
            LogConfig.ServiceLog.Information(
                "PipeServer: scheduled reboot time already passed — rebooting at {Target} " +
                "after the standard warning.", target);
        }

        var warnAt = target.AddMinutes(-AppConstants.ScheduledRebootWarningMinutes);

        // Wait until 5 minutes before the reboot. (If the machine sleeps during
        // this wait, the delay completes on wake and the overdue path above has
        // already ensured a sane target on service restart.)
        if (warnAt > DateTime.Now)
            await Task.Delay(warnAt - DateTime.Now, cancellationToken);

        // Show the countdown warning. The response window only lasts until the
        // reboot time — if the notifier fails to launch, no one is logged in,
        // or the user does nothing, the reboot proceeds regardless.
        var warnMessage = new PipeMessage
        {
            Type        = MessageType.RebootImminent,
            RebootAtUtc = target.ToUniversalTime(),
            Timestamp   = DateTime.UtcNow
        };

        var responseWindow = target - DateTime.Now;
        if (responseWindow < TimeSpan.FromSeconds(5))
            responseWindow = TimeSpan.FromSeconds(5);

        var response = await RunOneNotificationCycleAsync(
            warnMessage, cancellationToken, responseWindow);

        if (response is not null && response.SnoozeMinutes > 0)
        {
            // User clicked "Delay 5 Minutes" — one delay only, then force.
            target = target.AddMinutes(AppConstants.ScheduledRebootDelayMinutes);
            RegistryHelper.SetString(
                RegistryConstants.ScheduledRebootUtc, target.ToUniversalTime().ToString("o"));
            LogConfig.ServiceLog.Information(
                "PipeServer: user delayed scheduled reboot by {Min} minutes — new time {Target}.",
                AppConstants.ScheduledRebootDelayMinutes, target);
        }
        else
        {
            LogConfig.ServiceLog.Information(
                "PipeServer: no delay requested (response={Resp}) — reboot proceeds at {Target}.",
                response?.SnoozeMinutes.ToString() ?? "none", target);
        }

        var remaining = target - DateTime.Now;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, cancellationToken);

        // Clear the persisted schedule BEFORE rebooting so the machine does not
        // find a stale overdue entry (and reboot again) after it comes back up.
        RegistryHelper.DeleteValue(RegistryConstants.ScheduledRebootUtc);

        LogConfig.ServiceLog.Information("PipeServer: scheduled reboot time reached — initiating reboot.");
        InitiateReboot();
    }

    /// <summary>
    /// Launches the notifier, waits for it to connect, exchanges one
    /// request/response pair, and returns the user's decision.
    /// Returns null if communication fails.
    /// </summary>
    /// <param name="responseTimeout">
    /// How long to wait for the user's response. Defaults to 8 hours for the
    /// normal reboot prompt; the pre-reboot countdown passes its short window.
    /// </param>
    private async Task<PipeMessage?> RunOneNotificationCycleAsync(
        PipeMessage outbound, CancellationToken cancellationToken,
        TimeSpan? responseTimeout = null)
    {
        // Create the pipe with a security descriptor that allows any authenticated
        // user to connect. Without this, the default DACL only permits SYSTEM,
        // and the notifier (running as the logged-in user) gets UnauthorizedAccessException.
        var pipeSecurity = new PipeSecurity();
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        pipeSecurity.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        using var pipeServer = NamedPipeServerStreamAcl.Create(
            AppConstants.PipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity);

        LogConfig.ServiceLog.Information("PipeServer: pipe created — launching notifier.");

        // Kill any stale notifier processes before launching a fresh one,
        // so the user never sees more than one popup at a time.
        foreach (var stale in System.Diagnostics.Process.GetProcessesByName("UpdateNotifier"))
        {
            try
            {
                stale.Kill();
                LogConfig.ServiceLog.Warning("PipeServer: killed stale notifier process (PID {Pid}).", stale.Id);
            }
            catch { /* already exited — ignore */ }
            finally { stale.Dispose(); }
        }

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

        // ── Send outbound message ────────────────────────────────────────────
        try
        {
            await WriteMessageAsync(pipeServer, outbound, cancellationToken);
            LogConfig.ServiceLog.Debug("PipeServer: {Type} message sent.", outbound.Type);
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
            // Default 8 hours gives the user plenty of time on the normal prompt;
            // the pre-reboot countdown passes its own short window instead.
            responseCts.CancelAfter(responseTimeout ?? TimeSpan.FromHours(8));

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
    /// </summary>
    private static async Task<PipeMessage?> ReadMessageAsync(PipeStream pipe, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await pipe.ReadExactlyAsync(lenBuf, ct);
        var bodyLen = BitConverter.ToInt32(lenBuf);

        var bodyBuf = new byte[bodyLen];
        await pipe.ReadExactlyAsync(bodyBuf, ct);

        var json = Encoding.UTF8.GetString(bodyBuf);
        return JsonSerializer.Deserialize<PipeMessage>(json);
    }

    /// <summary>
    /// Returns true when HKLM\SOFTWARE\CapTG\InstType is "Server".
    /// </summary>
    private static bool IsServerInstall()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryConstants.CapTgKeyPath);
            return key?.GetValue(RegistryConstants.InstType) is string s &&
                   s.Equals("Server", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Reads RebootTime (e.g. "4:00am") from HKLM\SOFTWARE\CapTG and returns
    /// the next occurrence of that local time of day (today or tomorrow).
    /// Defaults to 4:00 AM when the value is missing or unparseable.
    /// </summary>
    private static DateTime GetNextServerRebootTime()
    {
        var raw = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryConstants.CapTgKeyPath);
            raw = key?.GetValue(RegistryConstants.RebootTime) as string ?? string.Empty;
        }
        catch { /* fall through to the default below */ }

        // Parse with the invariant culture so "4:00am" works on any system locale.
        string[] formats = ["h:mmtt", "h:mm tt", "hh:mmtt", "hh:mm tt", "H:mm", "HH:mm"];
        if (!DateTime.TryParseExact(raw.Trim().ToUpperInvariant(), formats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            LogConfig.ServiceLog.Warning(
                "PipeServer: RebootTime '{Raw}' is missing or unparseable — defaulting to 4:00 AM.",
                raw);
            parsed = new DateTime(1, 1, 1, 4, 0, 0);
        }

        var now  = DateTime.Now;
        var next = DateTime.Today.Add(parsed.TimeOfDay);
        if (next <= now)
            next = next.AddDays(1);

        return next;
    }

    /// <summary>
    /// Calls shutdown.exe to reboot the machine in 5 seconds,
    /// giving processes a moment to save and close.
    /// </summary>
    private static void InitiateReboot()
    {
        LogConfig.ServiceLog.Warning("PipeServer: executing system reboot in 5 seconds.");
        RegistryHelper.SetString(RegistryConstants.LastRebootUtc, DateTime.UtcNow.ToString("o"));
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = "shutdown.exe",
            Arguments       = "/r /t 5 /c \"CapTG Update Service: Restarting to apply updates.\"",
            CreateNoWindow  = true,
            UseShellExecute = false
        });
    }
}