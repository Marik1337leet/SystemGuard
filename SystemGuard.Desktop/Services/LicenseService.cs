using System;
using System.IO;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class LicenseInfo
{
    public string Key { get; set; } = "";
    public string MachineId { get; set; } = "";
    public DateTime ActivatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string Tier { get; set; } = "Free"; // Free, Pro, Enterprise
    public string Plan { get; set; } = "";     // Monthly, HalfYearly, Yearly, Lifetime
    public DateTime LastSeenUtc { get; set; }  // анти-откат часов
    public bool IsValid { get; set; }
    public int DaysLeft => IsValid && !IsExpired ? Math.Max(0, (int)(ExpiresAt - DateTime.Now).TotalDays) : 0;
    public bool IsExpired => DateTime.Now > ExpiresAt;
    public bool IsTrial => Tier == "Free" && IsValid;
    public bool IsLifetime => Plan == "Lifetime" && IsValid && !IsExpired;
}

public class LicenseService
{
    private readonly string _licensePath;
    private readonly string _machineId;

    // Ключ шифрования — уникален для каждой сборки.
    // ВНИМАНИЕ: старые значения были битыми (невалидный base64) — из-за этого
    // шифрование license.dat никогда не работало. Заменены на корректные
    // 32 байта (AES-256) + 16 байт IV. Старые license.dat сбросятся (нужна
    // повторная активация тем же ключом — ключи валидны, HMAC не менялся).
    // В продакшне держать в dotnet user-secrets / переменной среды.
    private static readonly byte[] _aesKey = Convert.FromBase64String("ZtkQh4G5yTFypd1YaVyaaWkRN9QDc75ISwyuD8INd7U=");
    private static readonly byte[] _aesIv = Convert.FromBase64String("APpjnrgsdG7HnPOi2pPxbw==");

    public LicenseInfo CurrentLicense { get; private set; } = new();
    public string MachineId => _machineId; // HWID для показа в UI (привязка ключа)

    public LicenseService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(appData, "SystemGuard");
        Directory.CreateDirectory(folder);
        _licensePath = Path.Combine(folder, "license.dat");
        _machineId = GetMachineId();
        LoadLicense();
    }

    // ── Machine ID ──────────────────────────────────────────────────────────

    private string GetMachineId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_ComputerSystemProduct");
            foreach (ManagementObject obj in searcher.Get())
            {
                var uuid = obj["UUID"]?.ToString() ?? "";
                if (!string.IsNullOrWhiteSpace(uuid) && uuid != "FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF")
                {
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uuid + "SG_SALT_V1"));
                    return Convert.ToHexString(hash)[..32].ToLower();
                }
            }
        }
        catch { }

        // Fallback: комбинация из нескольких источников
        var fallback = $"{Environment.MachineName}_{Environment.ProcessorCount}_{Environment.OSVersion.Version}";
        var fallbackHash = SHA256.HashData(Encoding.UTF8.GetBytes(fallback + "SG_SALT_V1"));
        return Convert.ToHexString(fallbackHash)[..32].ToLower();
    }

    // ── Trial ────────────────────────────────────────────────────────────────

    public bool ActivateTrial()
    {
        // Не давать второй trial если уже был — метка переживает удаление license.dat
        if (CurrentLicense.IsValid && CurrentLicense.Tier != "Free")
            return false;
        if (WasTrialTaken())
            return false;

        CurrentLicense = new LicenseInfo
        {
            Key = $"TRIAL-{GenerateRandomSuffix(8)}",
            MachineId = _machineId,
            ActivatedAt = DateTime.Now,
            ExpiresAt = DateTime.Now.AddDays(14),
            Tier = "Free",
            Plan = "Trial",
            LastSeenUtc = DateTime.UtcNow,
            IsValid = true
        };
        SaveLicense();
        MarkTrialTaken();
        return true;
    }

    // ── Trial anti-rearm: метка в HKCU + fallback-файл ──────────────────────
    // Удаление license.dat триал не сбрасывает.

    private static string TrialMarkerPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SystemGuard");
        return Path.Combine(dir, ".sgcfg");
    }

    private bool WasTrialTaken()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\SystemGuard");
            var v = key?.GetValue("TrialTaken")?.ToString();
            if (v == ComputeTrialMarker()) return true;
        }
        catch { }
        try
        {
            var p = TrialMarkerPath();
            if (File.Exists(p) && File.ReadAllText(p).Trim() == ComputeTrialMarker()) return true;
        }
        catch { }
        return false;
    }

    private void MarkTrialTaken()
    {
        var marker = ComputeTrialMarker();
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\SystemGuard");
            key?.SetValue("TrialTaken", marker, Microsoft.Win32.RegistryValueKind.String);
        }
        catch { }
        try
        {
            var p = TrialMarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, marker);
            try { File.SetAttributes(p, FileAttributes.Hidden); } catch { }
        }
        catch { }
    }

    private string ComputeTrialMarker()
    {
        var raw = _machineId + "|SG_TRIAL_V1";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLower();
    }

    // ── Pro Activation ───────────────────────────────────────────────────────

    public (bool Success, string Message) ActivatePro(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (false, "Key cannot be empty");

        key = key.Trim(); // ВАЖНО: без ToUpper — base64 payload регистрозависим!

        // Формат: SG-PRO-... / SG-ENT-... (проверка HMAC внутри payload)
        if (key.StartsWith("SG-PRO-", StringComparison.OrdinalIgnoreCase) && key.Length >= 20)
        {
            var payload = key["SG-PRO-".Length..];
            var result = ValidateAndDecodeKey(payload, "Pro");
            if (result.Success)
            {
                CurrentLicense = new LicenseInfo
                {
                    Key = key,
                    MachineId = _machineId,
                    ActivatedAt = DateTime.Now,
                    ExpiresAt = result.ExpiresAt,
                    Tier = "Pro",
                    Plan = result.Plan,
                    LastSeenUtc = DateTime.UtcNow,
                    IsValid = true
                };
                SaveLicense();
                // Владелец видит в БД, какой ПК каким ключом пользуется.
                SupabaseLicenseClient.ReportActivation(_machineId, Environment.MachineName, key, "Pro", result.Plan, force: true);
                return (true, $"Pro {result.Plan} activated! Valid until {result.ExpiresAt:dd MMM yyyy}");
            }
            return (false, result.Error);
        }

        if (key.StartsWith("SG-ENT-", StringComparison.OrdinalIgnoreCase) && key.Length >= 20)
        {
            var payload = key["SG-ENT-".Length..];
            var result = ValidateAndDecodeKey(payload, "Enterprise");
            if (result.Success)
            {
                CurrentLicense = new LicenseInfo
                {
                    Key = key,
                    MachineId = _machineId,
                    ActivatedAt = DateTime.Now,
                    ExpiresAt = result.ExpiresAt,
                    Tier = "Enterprise",
                    Plan = result.Plan,
                    LastSeenUtc = DateTime.UtcNow,
                    IsValid = true
                };
                SaveLicense();
                SupabaseLicenseClient.ReportActivation(_machineId, Environment.MachineName, key, "Enterprise", result.Plan, force: true);
                return (true, $"Enterprise {result.Plan} activated! Valid until {result.ExpiresAt:dd MMM yyyy}");
            }
            return (false, result.Error);
        }

        return (false, "Invalid key format. Expected SG-PRO-... or SG-ENT-...");
    }

    // ── Key Validation (HMAC-SHA256) ─────────────────────────────────────────

    private static readonly byte[] _hmacSecret = SHA256.HashData(
        Encoding.UTF8.GetBytes("SystemGuard_License_HMAC_Secret_2025"));

    private (bool Success, DateTime ExpiresAt, string Plan, string Error) ValidateAndDecodeKey(string payload, string expectedTier)
    {
        try
        {
            // payload = base64url(tier|expiryDate[|plan[|hwid]]) + "." + base64url(hmac)
            // Старые ключи tier|expiry тоже принимаются (plan="Legacy").
            var parts = payload.Split('.');
            if (parts.Length != 2)
                return (false, DateTime.MinValue, "", "Malformed key");

            var dataBytes = Convert.FromBase64String(PadBase64(parts[0]));
            var signatureBytes = Convert.FromBase64String(PadBase64(parts[1]));

            // Проверка HMAC
            using var hmac = new HMACSHA256(_hmacSecret);
            var expectedSig = hmac.ComputeHash(dataBytes);
            if (!CryptographicOperations.FixedTimeEquals(expectedSig, signatureBytes))
                return (false, DateTime.MinValue, "", "Invalid key signature");

            // Декодирование данных
            var data = Encoding.UTF8.GetString(dataBytes);
            var fields = data.Split('|');
            if (fields.Length < 2)
                return (false, DateTime.MinValue, "", "Malformed key data");

            var tier = fields[0];
            if (!tier.Equals(expectedTier, StringComparison.OrdinalIgnoreCase))
                return (false, DateTime.MinValue, "", $"Key is for {tier}, not {expectedTier}");

            if (!DateTime.TryParse(fields[1], out var expiry))
                return (false, DateTime.MinValue, "", "Invalid expiry date in key");

            if (expiry < DateTime.Now)
                return (false, DateTime.MinValue, "", $"Key expired on {expiry:dd MMM yyyy}");

            var plan = fields.Length >= 3 && !string.IsNullOrWhiteSpace(fields[2]) ? fields[2] : "Legacy";

            // Привязка к железу: если ключ выпущен под конкретный HWID — сверяем
            if (fields.Length >= 4 && !string.IsNullOrWhiteSpace(fields[3]))
            {
                if (!fields[3].Equals(_machineId, StringComparison.OrdinalIgnoreCase))
                    return (false, DateTime.MinValue, "", "Key is bound to a different PC (HWID mismatch)");
            }

            return (true, expiry, plan, "");
        }
        catch (Exception ex)
        {
            return (false, DateTime.MinValue, "", $"Key validation error: {ex.Message}");
        }
    }

    private static string PadBase64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
    }

    // ── Deactivate ───────────────────────────────────────────────────────────

    public void Deactivate()
    {
        CurrentLicense = new LicenseInfo();
        try { File.Delete(_licensePath); } catch { }
    }

    // ── Receipt (чек) ────────────────────────────────────────────────────────
    // Текстовый чек после активации: ключ (маскированный), тариф, срок, HWID, дата.
    // Сохраняется рядом с license.dat как receipt_<tier>_<yyyyMMdd>.txt.

    public string BuildReceiptText()
    {
        var l = CurrentLicense;
        var sb = new StringBuilder();
        sb.AppendLine("SystemGuard — receipt");
        sb.AppendLine($"Date: {DateTime.Now:G}");
        sb.AppendLine($"Tier: {l.Tier}");
        sb.AppendLine($"Plan: {l.Plan}");
        sb.AppendLine($"Activated: {l.ActivatedAt:G}");
        sb.AppendLine($"Expires: {(l.Plan == "Lifetime" ? "never" : l.ExpiresAt.ToString("G"))}");
        sb.AppendLine($"Days left: {l.DaysLeft}");
        sb.AppendLine($"Device (HWID): {l.MachineId}");
        sb.AppendLine($"Key: {MaskKey(l.Key)}");
        sb.AppendLine($"Valid: {(l.IsValid && !l.IsExpired ? "yes" : "no")}");
        return sb.ToString();
    }

    public string SaveReceipt()
    {
        var dir = Path.GetDirectoryName(_licensePath) ?? ".";
        var path = Path.Combine(dir, $"receipt_{CurrentLicense.Tier}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        File.WriteAllText(path, BuildReceiptText(), Encoding.UTF8);
        return path;
    }

    private static string MaskKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return "—";
        if (key.Length <= 12) return key;
        return key[..7] + "…" + key[^4..];
    }

    // ── Feature Gating ───────────────────────────────────────────────────────

    public bool IsFeatureAvailable(string feature)
    {
        if (CurrentLicense.IsExpired || !CurrentLicense.IsValid)
        {
            var freeFeatures = new[] { "Dashboard", "Processes", "Power", "Settings" };
            return Array.IndexOf(freeFeatures, feature) >= 0;
        }

        return CurrentLicense.Tier switch
        {
            "Enterprise" => true,
            "Pro" => feature != "EnterpriseOnly",
            _ => new[] { "Dashboard", "Processes", "Power", "Settings" }.Contains(feature)
        };
    }

    // ── Persistence (AES-256-CBC) ────────────────────────────────────────────

    private void LoadLicense()
    {
        try
        {
            if (!File.Exists(_licensePath))
            {
                CurrentLicense = new LicenseInfo();
                return;
            }

            var encrypted = File.ReadAllBytes(_licensePath);
            var json = Decrypt(encrypted);
            var license = JsonSerializer.Deserialize<LicenseInfo>(json);

            if (license == null)
            {
                CurrentLicense = new LicenseInfo();
                return;
            }

            // Проверка привязки к машине
            if (license.Tier != "Free" && license.MachineId != _machineId)
            {
                CurrentLicense = new LicenseInfo { IsValid = false };
                return;
            }

            // Анти-откат часов: системное время не может быть сильно раньше
            // последнего зафиксированного запуска или даты активации.
            var now = DateTime.UtcNow;
            if (license.IsValid && license.Tier != "Free")
            {
                if ((license.LastSeenUtc != default && now < license.LastSeenUtc - TimeSpan.FromDays(2)) ||
                    (license.ActivatedAt != default && now < license.ActivatedAt.ToUniversalTime() - TimeSpan.FromDays(1)))
                {
                    license.IsValid = false; // clock tampering — снимаем валидность до исправления часов
                    CurrentLicense = license;
                    return;
                }
            }

            // Проверка срока
            if (license.IsExpired)
                license.IsValid = false;

            // Фиксируем последний запуск (для анти-отката)
            if (license.IsValid)
            {
                license.LastSeenUtc = now;
                CurrentLicense = license;
                try { SaveLicense(); } catch { }
                // Живая Pro/Enterprise раз в сутки маякует в БД (троттлинг внутри):
                // владелец видит, что ПК с ключом ещё в строю.
                if (license.Tier is "Pro" or "Enterprise" && !string.IsNullOrWhiteSpace(license.Key))
                    SupabaseLicenseClient.ReportActivation(_machineId, Environment.MachineName, license.Key, license.Tier, license.Plan, force: false);
            }
            else
            {
                CurrentLicense = license;
            }
        }
        catch
        {
            CurrentLicense = new LicenseInfo();
            // Не удаляем файл — может быть повреждён временно
        }
    }

    private void SaveLicense()
    {
        try
        {
            var json = JsonSerializer.Serialize(CurrentLicense, new JsonSerializerOptions
            {
                WriteIndented = false // компактно для шифрования
            });
            var encrypted = Encrypt(json);
            File.WriteAllBytes(_licensePath, encrypted);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"License save error: {ex.Message}");
        }
    }

    private static byte[] Encrypt(string plainText)
    {
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.IV = _aesIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

        // Добавляем HMAC для integrity check
        using var hmac = new HMACSHA256(_hmacSecret);
        var mac = hmac.ComputeHash(plainBytes);

        // Формат: [4 bytes data length][data][32 bytes hmac]
        using var ms = new MemoryStream();
        var lenBytes = BitConverter.GetBytes(plainBytes.Length);
        ms.Write(lenBytes, 0, 4);
        ms.Write(plainBytes, 0, plainBytes.Length);
        ms.Write(mac, 0, mac.Length);

        var combined = ms.ToArray();
        return encryptor.TransformFinalBlock(combined, 0, combined.Length);
    }

    private static string Decrypt(byte[] cipherBytes)
    {
        using var aes = Aes.Create();
        aes.Key = _aesKey;
        aes.IV = _aesIv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var combined = decryptor.TransformFinalBlock(cipherBytes, 0, cipherBytes.Length);

        // Читаем длину данных
        var dataLen = BitConverter.ToInt32(combined, 0);
        if (dataLen <= 0 || dataLen > combined.Length - 4 - 32)
            throw new CryptographicException("Invalid license file structure");

        var plainBytes = combined[4..(4 + dataLen)];
        var storedMac = combined[(4 + dataLen)..];

        // Проверяем HMAC
        using var hmac = new HMACSHA256(_hmacSecret);
        var expectedMac = hmac.ComputeHash(plainBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, storedMac))
            throw new CryptographicException("License file integrity check failed");

        return Encoding.UTF8.GetString(plainBytes);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string GenerateRandomSuffix(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var result = new char[length];
        using var rng = RandomNumberGenerator.Create();
        var buffer = new byte[length];
        rng.GetBytes(buffer);
        for (int i = 0; i < length; i++)
            result[i] = chars[buffer[i] % chars.Length];
        return new string(result);
    }
}