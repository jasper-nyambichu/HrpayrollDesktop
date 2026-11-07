using System.Collections.Generic;
using System.Linq;

namespace HrpayrollDesktop.Sync;

/// <summary>
/// Which of the branch's employees are also its Branch Manager or Back Office. The Admin links those logins to
/// the person's employee record on the web; the server sends the result with the employee mappings, and the
/// engine refreshes it here every few minutes. Kept in memory only: until the first refresh nobody is marked.
/// </summary>
public static class StaffRoles
{
    private static volatile IReadOnlyDictionary<long, string> _roles = new Dictionary<long, string>();

    public static void Update(IEnumerable<(long EmployeeId, string? Role)> mappings) =>
        _roles = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.Role))
            .GroupBy(m => m.EmployeeId)
            .ToDictionary(g => g.Key, g => g.First().Role!);

    /// <summary>"Branch Manager", "Back Office" or null.</summary>
    public static string? Label(long employeeId) =>
        _roles.TryGetValue(employeeId, out var role)
            ? role switch { "BRANCH_MANAGER" => "Branch Manager", "BACK_OFFICE" => "Back Office", _ => null }
            : null;

    /// <summary>The same for a reader id such as "emp-12".</summary>
    public static string? LabelForTerminal(string terminalUserId) =>
        terminalUserId.StartsWith("emp-") && long.TryParse(terminalUserId[4..], out var id) ? Label(id) : null;

    /// <summary>"Jane Wanjiru (Branch Manager)", or the name alone.</summary>
    public static string WithLabel(string name, long employeeId) =>
        Label(employeeId) is { } label ? $"{name} ({label})" : name;
}
