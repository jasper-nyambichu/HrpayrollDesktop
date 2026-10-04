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

    Task ConnectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken);
    Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken);
    Task RemoveEnrollmentAsync(string terminalUserId);
}

public record TerminalEvent(
    string EventId,
    string TerminalUserId,
    DateTime Timestamp,
    string VerifyMethod
);