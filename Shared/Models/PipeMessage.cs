// Shared/Models/PipeMessage.cs
// Defines all message types and the message contract exchanged over the named pipe.
// Both UpdateService (server) and UpdateNotifier (client) reference this model.

using System.Text.Json.Serialization;

namespace Shared.Models;

/// <summary>
/// Discriminates the purpose of each pipe message.
/// </summary>
public enum MessageType
{
    /// <summary>Service → UI: updates were applied and a reboot is required.</summary>
    RebootRequired,

    /// <summary>UI → Service: the user's snooze or reboot-now decision.</summary>
    UserResponse,

    /// <summary>Service → UI: keep-alive ping.</summary>
    Ping,

    /// <summary>UI → Service: keep-alive reply.</summary>
    Pong
}

/// <summary>
/// The single wire-format type for all named pipe communication.
/// Serialised as JSON (UTF-8, newline-delimited) over the pipe stream.
/// </summary>
public sealed class PipeMessage
{
    /// <summary>Identifies what this message means.</summary>
    [JsonPropertyName("type")]
    public MessageType Type { get; set; }

    /// <summary>
    /// Human-readable paragraph describing what was updated.
    /// Populated only for <see cref="MessageType.RebootRequired"/>.
    /// </summary>
    [JsonPropertyName("updateSummary")]
    public string UpdateSummary { get; set; } = string.Empty;

    /// <summary>
    /// KB article numbers installed during this update cycle.
    /// Populated only for <see cref="MessageType.RebootRequired"/>.
    /// </summary>
    [JsonPropertyName("kbNumbers")]
    public List<string> KbNumbers { get; set; } = [];

    /// <summary>
    /// Names of winget packages updated this cycle.
    /// Populated only for <see cref="MessageType.RebootRequired"/>.
    /// </summary>
    [JsonPropertyName("updatedPackages")]
    public List<string> UpdatedPackages { get; set; } = [];

    /// <summary>
    /// User's snooze choice in minutes.
    /// 0 = "Reboot Now"; any positive value = snooze duration.
    /// Populated only for <see cref="MessageType.UserResponse"/>.
    /// </summary>
    [JsonPropertyName("snoozeMinutes")]
    public int SnoozeMinutes { get; set; }

    /// <summary>
    /// Number of times the user has snoozed in the current notification cycle.
    /// Used by the notifier to restore the correct remaining options on relaunch.
    /// Populated only for <see cref="MessageType.RebootRequired"/>.
    /// </summary>
    [JsonPropertyName("snoozeCount")]
    public int SnoozeCount { get; set; }

    /// <summary>UTC time this message was created.</summary>
    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
