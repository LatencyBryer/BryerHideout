using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BryersHideoutPlugin.Models;

public sealed record StaffIdentityModel(
    string Id,
    string Email,
    string DisplayName,
    bool IsMaster,
    string RoleId,
    string RoleName,
    string RoleProfileId,
    string RoleProfileName,
    IReadOnlyList<string> Permissions,
    bool MustChangePassword)
{
    public bool Can(string permission) =>
        IsMaster || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public static StaffIdentityModel ParseMeResponse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("authenticated", out var authenticated) || authenticated.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Staff authentication was not accepted.");
        if (!root.TryGetProperty("staff", out var staff) || staff.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Staff identity is missing from the server response.");

        var permissions = new List<string>();
        if (staff.TryGetProperty("permissions", out var permissionArray) && permissionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in permissionArray.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.String) continue;
                var permission = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(permission)) permissions.Add(permission);
            }
        }

        return new StaffIdentityModel(
            ReadString(staff, "id"),
            ReadString(staff, "email"),
            ReadString(staff, "displayName"),
            ReadBool(staff, "isMaster"),
            ReadString(staff, "roleId"),
            ReadString(staff, "roleName"),
            ReadString(staff, "roleProfileId"),
            ReadString(staff, "roleProfileName"),
            permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ReadBool(staff, "mustChangePassword"));
    }

    private static string ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.TryGetInt64(out var number) && number != 0,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) && parsed,
            _ => false,
        };
    }
}
