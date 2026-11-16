using System;
using System.Collections.Generic;

namespace HrpayrollDesktop.Sync;

// Mirrors DamageResponse.java (only the fields the console needs).
public record DamageItem(
    long Id,
    long EmployeeId,
    string? EmployeeName,
    string? EmployeeNumber,
    long BranchId,
    decimal Amount,
    string Description,
    DateTime DamageDate,
    string Status,
    bool Overridden,
    long? PayrollRunId = null,
    DateTime? DeductedAt = null);

// Bodies sent to DamageController / BiometricSyncController.
public record DamageCreateRequest(long EmployeeId, decimal Amount, string Description);
public record DamageOverrideBody(string Reason);
public record ConsentRequestBody(string EventId, string DeviceSerial, string TerminalUserId, long DamageId, DateTime OccurredAt);

// Mirrors DaysWorkedResponse.java. Dates stay as "yyyy-MM-dd" strings, exactly as the server sends them.
public record DaysWorkedItem(
    long EmployeeId,
    string EmployeeName,
    string? EmployeeNumber,
    long BranchId,
    string PeriodStart,
    string PeriodEnd,
    int SystemDays,
    int DaysWorked,
    bool Adjusted,
    string? AdjustmentReason,
    bool Locked,
    DateTime? LockedAt);

// Mirrors BiometricDeviceResponse.java (only the fields the console needs).
public record BranchDevice(long Id, long BranchId, string DeviceSerial, string? Status);

// Body of POST /attendance/enrollment/fingerprints (FingerprintEnrollRequest.java).
public record FingerprintSaveBody(long EmployeeId, string DeviceSerial, string TerminalUserId, short FingerSlot, string TemplateData, int? QualityScore);

// Mirrors FingerprintSyncResponse.java. EmployeeIds is the branch's whole roster, enrolled or not.
public record FingerprintSyncItem(long EmployeeId, string TerminalUserId, string? EmployeeName, string? EmployeeNumber, short FingerSlot, string TemplateData);
public record FingerprintSyncBundle(List<FingerprintSyncItem> Templates, List<long> EmployeeIds, List<StaffFingerprintItem>? StaffTemplates = null);

// The sign-in finger of one of this branch's Branch Manager / Back Office logins ("staff-{userId}"); never attendance.
public record StaffFingerprintItem(long UserId, string TerminalUserId, string? DisplayName, string? Role, string TemplateData);

// Body of POST /attendance/enrollment/my-fingerprint, and what GET answers.
public record LoginFingerprintBody(string DeviceSerial, string TemplateData, int? QualityScore);
public record LoginFingerprintStatus(bool Registered);

// Body and result of POST /attendance/devices (DeviceRegistrationRequest / DeviceRegistrationResponse).
public record DeviceRegistrationBody(long BranchId, string DeviceSerial);
public record DeviceRegistration(long Id, string DeviceSerial, string PlaintextToken);

/// <summary>One step of an employee's day at this PC's branch (GET /attendance/biometric/today).</summary>
public record DayEvent(long EmployeeId, string? TerminalUserId, string EventId, string EventType, DateTime At, DateTime? ExpectedBackAt, string? Reason);
