using System;
using System.Security.Cryptography;
using System.Text;

// SystemGuard.KeyGenerator
// Запускать ТОЛЬКО на сервере / локально у разработчика
// Никогда не включать в дистрибутив
//
// Формат ключа: SG-PRO-<base64url(tier|expiry|plan|hwid)>.<base64url(hmac)>
// hwid пустой = ключ переносимый (привяжется к первому ПК при активации)
// hwid заполнен = ключ сработает ТОЛЬКО на этом ПК (максимальная защита)

Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║   SystemGuard License Key Generator  ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.WriteLine();

while (true)
{
    Console.WriteLine("Select tier:");
    Console.WriteLine("  1. Pro");
    Console.WriteLine("  2. Enterprise");
    Console.WriteLine("  3. Exit");
    Console.Write("> ");

    var choice = Console.ReadLine()?.Trim();
    if (choice == "3") break;

    string tier = choice switch
    {
        "1" => "Pro",
        "2" => "Enterprise",
        _ => ""
    };

    if (string.IsNullOrEmpty(tier))
    {
        Console.WriteLine("Invalid choice.\n");
        continue;
    }

    Console.WriteLine("\nSelect plan:");
    Console.WriteLine("  1. Monthly   (30 days)");
    Console.WriteLine("  2. HalfYear  (182 days)");
    Console.WriteLine("  3. Yearly    (365 days)");
    Console.WriteLine("  4. Lifetime  (99 years)");
    Console.Write("> ");

    var planChoice = Console.ReadLine()?.Trim();
    (int days, string plan) = planChoice switch
    {
        "1" => (30, "Monthly"),
        "2" => (182, "HalfYearly"),
        "3" => (365, "Yearly"),
        "4" => (99 * 365, "Lifetime"),
        _ => (0, "")
    };

    if (days == 0)
    {
        Console.WriteLine("Invalid choice.\n");
        continue;
    }

    Console.WriteLine("\nBind to HWID? (paste Machine ID from License tab, or Enter = portable key)");
    Console.Write("> ");
    var hwid = (Console.ReadLine() ?? "").Trim().ToLower();
    if (hwid.Length != 0 && hwid.Length != 32)
    {
        Console.WriteLine("HWID must be 32 hex chars or empty.\n");
        continue;
    }

    var expiry = DateTime.Now.AddDays(days);
    var key = GenerateKey(tier, expiry, plan, hwid);

    Console.WriteLine();
    Console.WriteLine($"╔══════════════════════════════════════════════════╗");
    Console.WriteLine($"  Tier:    {tier} {plan}");
    Console.WriteLine($"  Expires: {expiry:dd MMM yyyy}");
    Console.WriteLine($"  HWID:    {(string.IsNullOrEmpty(hwid) ? "portable (binds on first activation)" : hwid)}");
    Console.WriteLine($"  Key:");
    Console.WriteLine($"  {key}");
    Console.WriteLine($"╚══════════════════════════════════════════════════╝");
    Console.WriteLine();
}

static string GenerateKey(string tier, DateTime expiry, string plan, string hwid)
{
    // Должен совпадать с _hmacSecret в LicenseService
    var hmacSecret = SHA256.HashData(
        Encoding.UTF8.GetBytes("SystemGuard_License_HMAC_Secret_2025"));

    // Данные: tier|expiry|plan|hwid
    var data = $"{tier}|{expiry:yyyy-MM-dd}|{plan}|{hwid}";
    var dataBytes = Encoding.UTF8.GetBytes(data);

    // Подпись HMAC-SHA256
    using var hmac = new HMACSHA256(hmacSecret);
    var signature = hmac.ComputeHash(dataBytes);

    // Кодирование в base64url
    var dataPart = Convert.ToBase64String(dataBytes)
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    var sigPart = Convert.ToBase64String(signature)
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    var payload = $"{dataPart}.{sigPart}";
    var prefix = tier == "Enterprise" ? "SG-ENT-" : "SG-PRO-";

    return $"{prefix}{payload}";
}
