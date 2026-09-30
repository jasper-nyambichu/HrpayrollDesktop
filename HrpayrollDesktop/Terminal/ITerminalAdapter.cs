using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HrpayrollDesktop.Terminal;

public interface ITerminalAdapter
{
    string? DeviceSerial { get; }

    /// <summary>Number of fingerprint templates enrolled on this terminal.</summary>
    int EnrolledCount { get; }
    bool IsEnrolled(string terminalUserId);

    /// <summary>A usable fingerprint image was captured and matching is starting.</summary>
    event Action? FingerCaptured;

    /// <summary>A fingerprint was captured but matched nobody.</summary>
    event Action? FingerNotRecognized;

    Task ConnectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken);
    Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken);
    Task RemoveEnrollmentAsync(string terminalUserId);
}

// Deliberately no EventType — the device layer only identifies WHO scanned.
// AttendanceEngine decides CLOCK_IN vs CLOCK_OUT using restart-safe persisted state.
public record TerminalEvent(
    string EventId,
    string TerminalUserId,
    DateTime Timestamp,
    string VerifyMethod
);