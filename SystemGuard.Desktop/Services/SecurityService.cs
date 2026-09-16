using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Безопасность: Defender-скан (реальный MpCmdRun), AES-шифрование файлов, SHA256.
public static class SecurityService
{
    public static string DefenderQuickScan()
    {
        try
        {
            var mp = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
            if (!File.Exists(mp)) return "Windows Defender not found";
            var psi = new ProcessStartInfo
            {
                FileName = mp, Arguments = "-Scan -ScanType 1",
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "Cannot start Defender scan";
            p.WaitForExit(1000);
            return "Defender quick scan started (watch Defender UI for results)";
        }
        catch (Exception ex) { return $"Defender scan failed: {ex.Message}"; }
    }

    public static string DefenderFullScan()
    {
        try
        {
            var mp = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
            if (!File.Exists(mp)) return "Windows Defender not found";
            var psi = new ProcessStartInfo
            {
                FileName = mp, Arguments = "-Scan -ScanType 2",
                UseShellExecute = false, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "Cannot start Defender scan";
            p.WaitForExit(1000);
            return "Defender full scan started in background (takes a while — watch Defender UI)";
        }
        catch (Exception ex) { return $"Defender scan failed: {ex.Message}"; }
    }

    public static string DefenderUpdateSignatures()
    {
        try
        {
            var mp = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
            if (!File.Exists(mp)) return "Windows Defender not found";
            var psi = new ProcessStartInfo
            {
                FileName = mp, Arguments = "-SignatureUpdate",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                StandardOutputEncoding = CmdEncoding.Oem
            };
            using var p = Process.Start(psi);
            if (p == null) return "Cannot start signature update";
            var log = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return "Signature update timed out"; }
            var tail = (log ?? "").Trim();
            if (tail.Length > 500) tail = tail[^500..];
            return p.ExitCode == 0
                ? "Signatures updated" + (tail.Length > 0 ? $"\n{tail}" : "")
                : $"Update finished with code {p.ExitCode}" + (tail.Length > 0 ? $"\n{tail}" : "");
        }
        catch (Exception ex) { return $"Update failed: {ex.Message}"; }
    }

    public static string DefenderThreats()
    {
        try
        {
            var mp = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
            if (!File.Exists(mp)) return "Windows Defender not found";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"Get-MpThreatDetection | Select-Object -First 10 ThreatID,DomainUser,@{n='Threat';e={$_.ThreatName}},@{n='Detected';e={$_.InitialDetectionTime}} | Format-Table -AutoSize | Out-String -Width 200\"",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                StandardOutputEncoding = CmdEncoding.Oem
            };
            using var p = Process.Start(psi);
            if (p == null) return "Cannot query threats";
            var log = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30000);
            log = (log ?? "").Trim();
            if (log.Length > 2000) log = log[^2000..];
            return string.IsNullOrWhiteSpace(log) ? "No threats detected — system is clean" : log;
        }
        catch (Exception ex) { return $"Threat query failed: {ex.Message}"; }
    }

    public static string DefenderStatus()
    {
        try
        {
            var mp = @"C:\Program Files\Windows Defender\MpCmdRun.exe";
            if (!File.Exists(mp)) return "Defender not installed";
            var psi = new ProcessStartInfo
            {
                FileName = "powershell", Arguments = "-NoProfile -Command \"Get-MpComputerStatus | Select-Object -Property AntivirusEnabled,RealTimeProtectionEnabled,AntispywareEnabled | Format-List\"",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "Cannot query Defender";
            var out1 = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            return string.IsNullOrWhiteSpace(out1) ? "No Defender data" : out1.Trim();
        }
        catch (Exception ex) { return $"Defender status failed: {ex.Message}"; }
    }

    public static string Sha256OfFile(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    // AES-256 шифрование файла по паролю (PBKDF2 + случайная соль/IV в заголовке).
    public static async Task EncryptFileAsync(string inputPath, string outputPath, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        using var kdf = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        using var aes = Aes.Create();
        aes.Key = kdf.GetBytes(32);
        aes.GenerateIV();
        using var out1 = File.Create(outputPath);
        await out1.WriteAsync(salt);
        await out1.WriteAsync(aes.IV);
        using var crypto = new CryptoStream(out1, aes.CreateEncryptor(), CryptoStreamMode.Write);
        using var in1 = File.OpenRead(inputPath);
        await in1.CopyToAsync(crypto);
    }

    public static async Task DecryptFileAsync(string inputPath, string outputPath, string password)
    {
        using var in1 = File.OpenRead(inputPath);
        var salt = new byte[16];
        var iv = new byte[16];
        if (await in1.ReadAsync(salt) != 16 || await in1.ReadAsync(iv) != 16)
            throw new InvalidDataException("Not a SystemGuard encrypted file");
        using var kdf = new Rfc2898DeriveBytes(password, salt, 100_000, HashAlgorithmName.SHA256);
        using var aes = Aes.Create();
        aes.Key = kdf.GetBytes(32);
        aes.IV = iv;
        using var crypto = new CryptoStream(in1, aes.CreateDecryptor(), CryptoStreamMode.Read);
        using var out1 = File.Create(outputPath);
        await crypto.CopyToAsync(out1);
    }
}
