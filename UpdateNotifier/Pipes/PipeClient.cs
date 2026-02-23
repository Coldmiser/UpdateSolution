// UpdateNotifier/Pipes/PipeClient.cs
// The WPF notifier acts as the named-pipe CLIENT.
// Connects to the service pipe, receives the RebootRequired message,
// and sends back the user's snooze/reboot decision.

using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Shared.Constants;
using Shared.Models;
using UpdateNotifier.Logging;

namespace UpdateNotifier.Pipes;

/// <summary>
/// Manages the client side of the named-pipe conversation with the service.
/// </summary>
public sealed class PipeClient : IDisposable
{
    // ── Fields ───────────────────────────────────────────────────────────────

    private NamedPipeClientStream? _pipe;
    private bool                   _disposed;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to the service pipe and returns the <see cref="PipeMessage"/>
    /// sent by the service, or null if connection or reading fails.
    /// </summary>
    public async Task<PipeMessage?> ConnectAndReceiveAsync(CancellationToken ct)
    {
        LogConfig.Log.Information("PipeClient: connecting to pipe '{Name}'.", AppConstants.PipeName);

        _pipe = new NamedPipeClientStream(
            serverName: ".",                  // local machine
            pipeName: AppConstants.PipeName,
            direction: PipeDirection.InOut,
            options: PipeOptions.Asynchronous);

        try
        {
            await _pipe.ConnectAsync(AppConstants.PipeConnectTimeoutMs, ct);
            LogConfig.Log.Information("PipeClient: connected.");
        }
        catch (TimeoutException)
        {
            LogConfig.Log.Error("PipeClient: connection timed out.");
            return null;
        }
        catch (Exception ex)
        {
            LogConfig.Log.Error(ex, "PipeClient: connection failed.");
            return null;
        }

        return await ReadMessageAsync(ct);
    }

    /// <summary>
    /// Sends the user's decision (snooze or reboot-now) back to the service.
    /// </summary>
    public async Task SendResponseAsync(int snoozeMinutes, CancellationToken ct)
    {
        if (_pipe is null || !_pipe.IsConnected)
        {
            LogConfig.Log.Error("PipeClient: cannot send response — pipe is not connected.");
            return;
        }

        var response = new PipeMessage
        {
            Type          = MessageType.UserResponse,
            SnoozeMinutes = snoozeMinutes,
            Timestamp     = DateTime.UtcNow
        };

        LogConfig.Log.Information(
            "PipeClient: sending UserResponse — SnoozeMinutes={Min}.", snoozeMinutes);

        await WriteMessageAsync(_pipe, response, ct);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Reads a length-prefixed JSON message from the pipe.
    /// </summary>
    private async Task<PipeMessage?> ReadMessageAsync(CancellationToken ct)
    {
        try
        {
            if (_pipe is null) return null;

            var lenBuf = new byte[4];
            await _pipe.ReadExactlyAsync(lenBuf, ct);
            var bodyLen = BitConverter.ToInt32(lenBuf);

            var bodyBuf = new byte[bodyLen];
            await _pipe.ReadExactlyAsync(bodyBuf, ct);

            var json    = Encoding.UTF8.GetString(bodyBuf);
            var message = JsonSerializer.Deserialize<PipeMessage>(json);

            LogConfig.Log.Information(
                "PipeClient: received message type={Type}.", message?.Type);

            return message;
        }
        catch (Exception ex)
        {
            LogConfig.Log.Error(ex, "PipeClient: error reading message.");
            return null;
        }
    }

    /// <summary>
    /// Writes a length-prefixed JSON message to the pipe.
    /// </summary>
    private static async Task WriteMessageAsync(PipeStream pipe, PipeMessage message, CancellationToken ct)
    {
        var json  = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        var len   = BitConverter.GetBytes(bytes.Length);

        await pipe.WriteAsync(len, ct);
        await pipe.WriteAsync(bytes, ct);
        await pipe.FlushAsync(ct);

        LogConfig.Log.Debug("PipeClient: wrote {Bytes} bytes to pipe.", bytes.Length);
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _pipe?.Dispose();
        _disposed = true;
        LogConfig.Log.Debug("PipeClient: disposed.");
    }
}
