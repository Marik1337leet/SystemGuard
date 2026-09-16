// LicenseGate: триал = Pro, истёкшая = Free (функции закрываются без перезапуска).
using System;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class LicenseGateTests
{
    private static LicenseInfo Lic(string tier, string plan, int daysOffset, bool valid = true) => new()
    {
        Tier = tier,
        Plan = plan,
        IsValid = valid,
        ActivatedAt = DateTime.Now.AddDays(-1),
        ExpiresAt = DateTime.Now.AddDays(daysOffset),
        MachineId = "test",
        Key = "TEST"
    };

    [Fact]
    public void Trial_Counts_As_Pro()
    {
        var trial = Lic("Free", "Trial", 13);
        Assert.True(LicenseGate.IsTrial(trial));
        Assert.True(LicenseGate.IsPro(trial));
    }

    [Fact]
    public void Pro_And_Enterprise_Are_Pro()
    {
        Assert.True(LicenseGate.IsPro(Lic("Pro", "Monthly", 30)));
        Assert.True(LicenseGate.IsPro(Lic("Enterprise", "Yearly", 300)));
        Assert.True(LicenseGate.IsEnterprise(Lic("Enterprise", "Yearly", 300)));
        Assert.False(LicenseGate.IsEnterprise(Lic("Pro", "Monthly", 30)));
    }

    [Fact]
    public void Expired_Subscription_Closes_Pro_Features()
    {
        var expiredPro = Lic("Pro", "Monthly", -1);
        var expiredTrial = Lic("Free", "Trial", -1);
        Assert.True(expiredPro.IsExpired);
        Assert.False(LicenseGate.IsPro(expiredPro));
        Assert.False(LicenseGate.IsPro(expiredTrial));
        Assert.False(LicenseGate.IsTrial(expiredTrial));
    }

    [Fact]
    public void Invalid_License_Is_Free()
    {
        Assert.False(LicenseGate.IsPro(Lic("Pro", "Monthly", 30, valid: false)));
        Assert.False(LicenseGate.IsPro(new LicenseInfo()));
    }
}
