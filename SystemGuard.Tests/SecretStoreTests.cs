// SecretStore (DPAPI) + jail чтения файлов.
using System;
using System.IO;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class SecretStoreTests
{
    [Fact]
    public void Dpapi_Roundtrip()
    {
        var p = Path.Combine(Path.GetTempPath(), $"sg_secret_{Guid.NewGuid():N}.dat");
        try
        {
            SecretStore.WriteText(p, "123456:SECRET-TOKEN");
            var raw = File.ReadAllText(p);
            Assert.StartsWith("SGDPAPI1:", raw);
            Assert.DoesNotContain("SECRET-TOKEN", raw);
            Assert.Equal("123456:SECRET-TOKEN", SecretStore.ReadText(p));
        }
        finally { try { File.Delete(p); } catch { } }
    }

    [Fact]
    public void Plaintext_Migrates_To_Cipher()
    {
        var p = Path.Combine(Path.GetTempPath(), $"sg_secret_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(p, "{\"Token\":\"ABC\"}");
            Assert.Equal("{\"Token\":\"ABC\"}", SecretStore.ReadText(p));
            Assert.StartsWith("SGDPAPI1:", File.ReadAllText(p));
        }
        finally { try { File.Delete(p); } catch { } }
    }

    [Fact]
    public void Jail_Blocks_Data_Windows_ForeignProfiles()
    {
        var dataFile = Path.Combine(SelfProtection.DataDirectory, "telegram.json");
        Assert.NotNull(SelfProtection.BlockedForRemoteRead(dataFile));

        var win = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        Assert.NotNull(SelfProtection.BlockedForRemoteRead(win));

        // Свой файл — можно.
        var own = Path.Combine(Path.GetTempPath(), "sg_ok.txt");
        Assert.Null(SelfProtection.BlockedForRemoteRead(own));

        // Обход через .. — тоже блокируется (каноникализация).
        var traversal = Path.Combine(SelfProtection.DataDirectory, "..", "SystemGuard", "remote.json");
        Assert.NotNull(SelfProtection.BlockedForRemoteRead(traversal));
    }
}
