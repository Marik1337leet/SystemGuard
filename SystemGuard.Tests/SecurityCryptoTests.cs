// Шифратор файлов (AES-256) + родительский контроль: roundtrip и краевые случаи.
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class SecurityCryptoTests
{
    [Fact]
    public async Task Encrypt_Decrypt_Roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sg_crypto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "doc.txt");
            var enc = src + ".sgenc";
            var dec = Path.Combine(dir, "doc.dec.txt");
            await File.WriteAllTextAsync(src, "Hello, SystemGuard! Привет 123", Encoding.UTF8);
            await SecurityService.EncryptFileAsync(src, enc, "correct horse");
            Assert.True(new FileInfo(enc).Length > 32);
            await SecurityService.DecryptFileAsync(enc, dec, "correct horse");
            Assert.Equal(await File.ReadAllTextAsync(src, Encoding.UTF8),
                         await File.ReadAllTextAsync(dec, Encoding.UTF8));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Encrypt_Decrypt_LargeFile_Roundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sg_crypto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "big.bin");
            var enc = src + ".sgenc";
            var dec = src + ".dec";
            var rnd = new Random(42);
            var data = new byte[2 * 1024 * 1024];
            rnd.NextBytes(data);
            await File.WriteAllBytesAsync(src, data);
            await SecurityService.EncryptFileAsync(src, enc, "pw");
            await SecurityService.DecryptFileAsync(enc, dec, "pw");
            Assert.Equal(data, await File.ReadAllBytesAsync(dec));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Decrypt_WrongPassword_Throws_NotEmptyFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sg_crypto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "doc.txt");
            var enc = src + ".sgenc";
            var dec = src + ".dec";
            await File.WriteAllTextAsync(src, "secret data here");
            await SecurityService.EncryptFileAsync(src, enc, "right");
            await Assert.ThrowsAnyAsync<Exception>(() =>
                SecurityService.DecryptFileAsync(enc, dec, "wrong"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task Decrypt_PlainFile_ThrowsInvalidData()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"sg_crypto_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var src = Path.Combine(dir, "plain.txt");
            await File.WriteAllTextAsync(src, "not encrypted");
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                SecurityService.DecryptFileAsync(src, src + ".dec", "pw"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Parental_Block_AddsWwwVariant_And_UnblockRemovesBoth()
    {
        var hosts = Path.Combine(Path.GetTempPath(), $"sg_hosts_{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(hosts, "127.0.0.1 localhost" + Environment.NewLine);
            var svc = new ParentalControlService(hosts);
            Assert.Equal("example.com blocked", svc.Block("https://Example.com/some/page"));
            var blocked = svc.ListBlocked();
            Assert.Contains("example.com", blocked);
            Assert.Contains("www.example.com", blocked);
            // Двойная блокировка — без дублей.
            svc.Block("example.com");
            Assert.Equal(2, svc.ListBlocked().Count);
            svc.Unblock("example.com");
            Assert.Empty(svc.ListBlocked());
            // Чужой контент hosts не тронут.
            Assert.Contains("127.0.0.1 localhost", File.ReadAllText(hosts));
        }
        finally { try { File.Delete(hosts); } catch { } }
    }
}
