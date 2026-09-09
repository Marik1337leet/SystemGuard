using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace SystemGuard.Desktop.Services;

// Редактор реестра: только HKCU (безопасный хайв), чтение/запись/удаление значений.
public static class RegistryToolService
{
    public static List<string> ListValues(string subKey)
    {
        var list = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            if (key == null) return list;
            foreach (var name in key.GetValueNames())
                list.Add($"{name} = {key.GetValue(name)}");
        }
        catch (Exception ex) { list.Add($"Error: {ex.Message}"); }
        return list;
    }

    public static List<string> ListSubKeys(string subKey)
    {
        var list = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            if (key == null) return list;
            list.AddRange(key.GetSubKeyNames());
        }
        catch (Exception ex) { list.Add($"Error: {ex.Message}"); }
        return list;
    }

    public static string SetValue(string subKey, string name, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey);
            if (key == null) return "Cannot open key";
            key.SetValue(name, value);
            return $"Set {subKey}\\{name}";
        }
        catch (Exception ex) { return $"Registry write failed: {ex.Message}"; }
    }

    public static string DeleteValue(string subKey, string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
            if (key == null) return "Key not found";
            key.DeleteValue(name, throwOnMissingValue: false);
            return $"Deleted {name}";
        }
        catch (Exception ex) { return $"Registry delete failed: {ex.Message}"; }
    }
}
