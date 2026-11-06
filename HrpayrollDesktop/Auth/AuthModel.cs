using System.Collections.Generic;
using System;
using System.Text.Json.Serialization;

namespace HrpayrollDesktop.Auth;

public record LoginRequest(string Username, string Password);

// Mirrors AuthResponse.java field-for-field. Nullable longs match Java's
// nullable Long — not every login surface populates employeeId/agentId/branchId.
public record AuthResponse(
    string AccessToken,
    string RefreshToken,
    long UserId,
    string Username,
    string Role,
    string DisplayName,
    long? EmployeeId,
    long? AgentId,
    long? BranchId,
    bool MustChangePassword

);

public record EmployeeSummary(
    long Id,
    string FullName,
    string EmployeeNumber,
    string? BranchName,
    string? Position,
    string EmploymentStatus
);

public record AttendanceDayPage(List<AttendanceDayRecord> Content);

public record AttendanceDayRecord(long EmployeeId, List<AttendanceBreak>? Checkouts);

/// <summary>One lunch break or temporary check-out. ReturnStatus is OUT, ON_TIME, LATE or RETURNED.</summary>
public record AttendanceBreak(
    string Type,
    DateTime CheckedOutAt,
    DateTime? ExpectedBackAt,
    DateTime? CheckedInAt,
    string ReturnStatus,
    int? MinutesLate,
    string? Reason
);

public record AttendanceRecord(
    long Id,
    long EmployeeId,
    string? EmployeeName,
    long BranchId,
    DateTime AttendanceDate,
    DateTime? ClockIn,
    DateTime? ClockOut,
    string Source,
    string Status
);

public record ManualCheckInBody(IReadOnlyCollection<long> EmployeeIds);

/// <summary>What happened to one employee a Branch Manager ticked. ClockInUtc is the instant they were checked in.</summary>
public record ManualCheckInResult(
    long EmployeeId,
    string? EmployeeName,
    bool CheckedIn,
    long? AttendanceId,
    DateTime? ClockIn,
    DateTime? ClockInUtc,
    string? Message
);

public record LoginResult(bool Succeeded, AuthResponse? Session, string? Error)
{
    public static LoginResult Ok(AuthResponse session) => new(true, session, null);
    public static LoginResult Failure(string error) => new(false, null, error);
}