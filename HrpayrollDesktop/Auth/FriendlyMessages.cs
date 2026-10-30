using System.Text.Json;

namespace HrpayrollDesktop.Auth;

public static class FriendlyMessages
{
    public static string Translate(string? rawBackendMessage)
    {
        if (string.IsNullOrWhiteSpace(rawBackendMessage))
            return "Something went wrong. Please try again.";

        string? extracted = TryExtractMessage(rawBackendMessage);
        var text = extracted ?? rawBackendMessage;

        return text switch
        {
            var t when t.Contains("already mapped") => "This employee is already enrolled on this device.",
            var t when t.Contains("Employee not found") => "That employee could not be found. Please refresh the list and try again.",
            var t when t.Contains("No employee mapped") => "This fingerprint isn't linked to anyone yet. Please enroll this employee first.",
            var t when t.Contains("already has an open clock-in") => "This employee is already clocked in today.",
            var t when t.Contains("No open clock-in") => "This employee hasn't clocked in yet today, so they can't clock out.",
            var t when t.Contains("on approved leave") => "This employee is on approved leave today.",
            var t when t.Contains("not active") => "This employee's account is not active.",
            var t when t.Contains("Device not registered") => "This device isn't registered yet. Contact your administrator.",
            var t when t.Contains("Invalid credentials") => "Incorrect username or password.",
            var t when t.Contains("does not belong to the employee") => "That fingerprint belongs to someone else. Ask the right employee to place their finger.",
            var t when t.Contains("not awaiting consent") => "This damage is no longer waiting for consent. Refresh the list.",
            var t when t.Contains("Only a damage awaiting consent") => "This damage is no longer waiting for consent. Refresh the list.",
            var t when t.Contains("Unknown terminal user") => "This employee's fingerprint isn't registered on this reader. Enroll them first, or use Override.",
            var t when t.Contains("Damage not found") => "That damage could not be found. Refresh the list.",
            var t when t.Contains("already been deducted") => "This damage has already been deducted from pay, so it can't be cancelled.",
            var t when t.Contains("reason is required") => "Give a reason for the override.",
            var t when t.Contains("Days are already locked") => "The days for this period are already locked. Only an Administrator can reopen them.",
            var t when t.Contains("No locked days found") => "There are no locked days for this period.",
            var t when t.Contains("no active employees to lock") => "This branch has no active employees to lock days for.",
            var t when t.Contains("already registered") || t.Contains("already exists") => "This reader is already registered.",
            var t when t.Contains("Too many failed attempts") => "This account is temporarily locked due to repeated failed logins.",
            _ => text
        };
    }

    private static string? TryExtractMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString();
        }
        catch
        {
            // Not JSON — fall through and use the raw text as-is.
        }
        return null;
    }
}