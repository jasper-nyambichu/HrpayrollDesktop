using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;


namespace HrpayrollDesktop.Terminal;

public interface ITerminalAdapter
{
    string? DeviceSerial { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TerminalEvent>> PollEventsAsync(CancellationToken cancellationToken);
    Task<bool> EnrollAsync(string terminalUserId, CancellationToken cancellationToken);
    Task RemoveEnrollmentAsync(string terminalUserId);   // ADD
}

// Deliberately no EventType — the device layer only identifies WHO scanned.
// Worker (here: AttendanceEngine) decides CLOCK_IN vs CLOCK_OUT using
// restart-safe persisted state, not the device layer.
public record TerminalEvent(
    string EventId,
    string TerminalUserId,
    DateTime Timestamp,
    string VerifyMethod
);