using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace HrpayrollDesktop.Auth;

public record DeviceCredentials(string DeviceSerial, string DeviceToken);

public static class DeviceCredentialStore
{
    private static readonly string StorePath =
        Path.Combine(AppContext.BaseDirectory, "device-credentials.dat");

    public static DeviceCredentials? TryLoad()
    {
        if (!File.Exists(StorePath)) return null;

        try
        {
            var encrypted = File.ReadAllBytes(StorePath);
            var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<DeviceCredentials>(json);
        }
        catch
        {
            return null;   // corrupt/unreadable — treat as "not set up"
        }
    }

    public static void Save(DeviceCredentials credentials)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(credentials);
        var encrypted = ProtectedData.Protect(json, null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(StorePath, encrypted);
    }
}