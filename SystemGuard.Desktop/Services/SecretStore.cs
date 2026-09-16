using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace SystemGuard.Desktop.Services;

// Секреты на диске (bot-токен, live-токен): шифруем DPAPI текущего
// пользователя. Украденный файл бесполезен на другом ПК/под другим юзером.
// Старые открытые файлы мигрируют сами при первом чтении (перезаписываются
// шифром). Формат: "SGDPAPI1:" + base64(Protect(utf8)).
public static class SecretStore
{
    private const string Prefix = "SGDPAPI1:";

    public static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var bytes = Encoding.UTF8.GetBytes(text ?? "");
        var prot = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        File.WriteAllText(path, Prefix + Convert.ToBase64String(prot));
    }

    public static string? ReadText(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var raw = File.ReadAllText(path).Trim();
            if (raw.Length == 0) return null;
            if (raw.StartsWith(Prefix, StringComparison.Ordinal))
            {
                var prot = Convert.FromBase64String(raw[Prefix.Length..]);
                var bytes = ProtectedData.Unprotect(prot, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            // Миграция: открытый текст -> шифруем на месте.
            if (raw.Length >= 8)
            {
                try { WriteText(path, raw); } catch { }
                return raw;
            }
            return null;
        }
        catch { return null; }
    }
}
