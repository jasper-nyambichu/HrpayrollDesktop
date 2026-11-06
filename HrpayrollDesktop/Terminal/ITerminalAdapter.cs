using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HrpayrollDesktop.Terminal;

public interface ITerminalAdapter
{
    string? DeviceSerial { get; }

    int EnrolledCount { get; }
    bool IsEnrolled(string terminalUserId);

    /// <summary>A usable fingerprint was detected and matching is starting (once per touch).</summary>
    event Action? FingerCaptured;

    /// <summary>The touch was matched against everyone and nobody matched (once per touch).</summary>
    event Action? FingerNotRecognized;

    /// <summary>The finger has been lifted; the reader is ready for the next scan.</summary>
    event Action? FingerRemoved;

    /// <summary>A finger is on the glass but the image is too weak to read (once per touch).</summary>
    event Action? FingerPoorQuality;

    /// <summary>Opens the reader. Safe to call again after a failure or after the reader was lost.</summary>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>True once the open reader has stopped answering for a while, as when its cable is pulled out.</summary>
    bool IsDeviceLost { get; }
    Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken);
    Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken);
    Task RemoveEnrollmentAsync(string terminalUserId);

    /// <summary>The stored template of one person, or null if this PC has none.</summary>
    byte[]? GetTemplate(string terminalUserId);

    /// <summary>Templates this PC holds that the company database has not confirmed having.</summary>
    IReadOnlyList<KeyValuePair<string, byte[]>> GetTemplatesNotBackedUp();

    Task MarkBackedUpAsync(string terminalUserId);

    /// <summary>
    /// Makes this PC match the server: adds and updates the given fingerprints, and removes those of anyone no
    /// longer on the roster. A template the server has not confirmed having is kept, as it may be the only copy.
    /// </summary>
    Task<FingerprintSyncSummary> ApplyServerFingerprintsAsync(
        IReadOnlyCollection<ServerFingerprint> fingerprints, IReadOnlyCollection<long> roster, CancellationToken cancellationToken);
}

public record ServerFingerprint(string TerminalUserId, long EmployeeId, byte[] Template);

public record FingerprintSyncSummary(int Added, int Updated, int Removed)
{
    public bool Changed => Added + Updated + Removed > 0;
}

public record TerminalEvent(
    string EventId,
    string TerminalUserId,
    DateTime Timestamp,
    string VerifyMethod
);