// Мэппинг WebApp sendData → команда бота. Через него идёт fallback WebApp,
// когда Live-туннель мёртв: ответ приходит обычным сообщением в чат.
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class WebAppDataParseTests
{
    [Fact]
    public void Json_Action_Arg()
    {
        Assert.True(TelegramBotService.TryParseWebAppData(
            "{\"action\":\"volume\",\"arg\":\"70\"}", out var action, out var arg));
        Assert.Equal("volume", action);
        Assert.Equal("70", arg);
    }

    [Fact]
    public void Value_Field_As_Arg()
    {
        Assert.True(TelegramBotService.TryParseWebAppData(
            "{\"action\":\"volume\",\"value\":\"80\"}", out var action, out var arg));
        Assert.Equal("volume", action);
        Assert.Equal("80", arg);
    }

    [Theory]
    [InlineData("dashboard", "status")]
    [InlineData("info", "status")]
    [InlineData("power", "status")]
    [InlineData("pause", "play")]
    [InlineData("/CMD", "cmd")]
    public void Aliases_Normalized(string input, string expected)
    {
        Assert.True(TelegramBotService.TryParseWebAppData(
            "{\"action\":\"" + input + "\"}", out var action, out _));
        Assert.Equal(expected, action);
    }

    [Fact]
    public void Plain_Text_Action()
    {
        Assert.True(TelegramBotService.TryParseWebAppData("status", out var action, out _));
        Assert.Equal("status", action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{\"arg\":\"x\"}")]
    public void Broken_Rejected(string? data)
    {
        Assert.False(TelegramBotService.TryParseWebAppData(data, out _, out _));
    }

    [Fact]
    public void Unicode_Arg_Survives()
    {
        Assert.True(TelegramBotService.TryParseWebAppData(
            "{\"action\":\"type\",\"arg\":\"Привет!\"}", out var action, out var arg));
        Assert.Equal("type", action);
        Assert.Equal("Привет!", arg);
    }
}
