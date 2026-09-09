using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class VaultEntry
{
    public string Title { get; set; } = "";
    public string Login { get; set; } = "";
    public string Password { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
}

// Менеджер паролей: AES-хранилище с ключом, привязанным к машине (как аккаунты).
public class PasswordVaultService
{
    private readonly string _path;

    public PasswordVaultService(string? baseDir = null)
    {
        var dir = baseDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "vault.dat");
    }

    public List<VaultEntry> List()
    {
        try
        {
            if (!File.Exists(_path)) return new List<VaultEntry>();
            var json = Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(_path)));
            return JsonSerializer.Deserialize<List<VaultEntry>>(json) ?? new List<VaultEntry>();
        }
        catch { return new List<VaultEntry>(); }
    }

    public void Add(VaultEntry entry)
    {
        var list = List();
        list.Add(entry);
        Save(list);
    }

    public void Delete(string title)
    {
        var list = List();
        list.RemoveAll(e => e.Title.Equals(title, StringComparison.OrdinalIgnoreCase));
        Save(list);
    }

    private void Save(List<VaultEntry> list)
    {
        var json = JsonSerializer.Serialize(list);
        File.WriteAllBytes(_path, Protect(Encoding.UTF8.GetBytes(json)));
    }

    private static byte[] MachineKey()
    {
        var hwid = Environment.MachineName + "|" + Environment.ProcessorCount;
        return SHA256.HashData(Encoding.UTF8.GetBytes(hwid + "|SG_VAULT_V1"));
    }

    private static byte[] Protect(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = MachineKey();
        aes.IV = new byte[16];
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] Unprotect(byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = MachineKey();
        aes.IV = new byte[16];
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }
}
