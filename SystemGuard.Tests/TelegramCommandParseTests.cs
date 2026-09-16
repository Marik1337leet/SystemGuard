// Разбор команд бота: аргумент в той же строке ("/ls C:\\Games").
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class TelegramCommandParseTests
{
    [Theory]
    [InlineData("/ls C:\\Games", "/ls")]
    [InlineData("/cmd ipconfig", "/cmd")]
    [InlineData("/volume 70", "/volume")]
    [InlineData("/start@MyBot", "/start")]
    [InlineData("Status", "status")]
    public void ExtractCommand_Cases(string text, string expected)
    {
        Assert.Equal(expected, TelegramBotService.ExtractCommand(text));
    }

    [Theory]
    [InlineData("/ls C:\\Games", "C:\\Games")]
    [InlineData("/cmd ipconfig /all", "ipconfig /all")]
    [InlineData("/status", "")]
    [InlineData("/open \"my app\"", "my app")]
    public void ExtractArg_Cases(string text, string expected)
    {
        Assert.Equal(expected, TelegramBotService.ExtractArg(text));
    }
}
