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

public record LoginResult(bool Succeeded, AuthResponse? Session, string? Error)
{
    public static LoginResult Ok(AuthResponse session) => new(true, session, null);
    public static LoginResult Failure(string error) => new(false, null, error);
}