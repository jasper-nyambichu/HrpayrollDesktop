using System;
using System.Collections.Generic;

namespace HrpayrollDesktop.Sync;

// Mirrors BranchResponse.java (only the fields the console needs).
public record BranchOption(long Id, string Name, string? Code, string? Status);

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
    bool Overridden);

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

// Body of POST /days-worked/lock (DaysWorkedLockRequest.java).
public record DaysWorkedAdjustmentBody(long EmployeeId, int DaysWorked, string Reason);
public record DaysWorkedLockBody(long BranchId, string PeriodStart, string PeriodEnd, List<DaysWorkedAdjustmentBody> Adjustments);

// Body and result of POST /attendance/devices (DeviceRegistrationRequest / DeviceRegistrationResponse).
public record DeviceRegistrationBody(long BranchId, string DeviceSerial);
public record DeviceRegistration(long Id, string DeviceSerial, string PlaintextToken);