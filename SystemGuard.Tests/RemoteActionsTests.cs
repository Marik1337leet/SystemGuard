// RemoteActions: единая точка исполнения команд (бот + Live API + Relay).
// Тестируем только безопасные пути: read-only и validation-fail.
// Пути, меняющие железо (volume 70, brightness, shutdown), НЕ дёргаем.
using System.Text.Json;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class RemoteActionsTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);
    private static JsonElement El(object o) => JsonDocument.Parse(Json(o)).RootElement;
    private static bool Ok(object o) => El(o).TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.True;
    private static string Prop(object o, string name)
    {
        var e = El(o);
        return e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
    }

    [Fact]
    public async Task Status_Ok_Shape()
    {
        var r = await RemoteActions.ExecuteAsync("status", "");
        Assert.True(Ok(r));
        Assert.NotEmpty(Prop(r, "machine"));
    }

    [Fact]
    public async Task Unknown_Action_Fails_With_Name()
    {
        var r = await RemoteActions.ExecuteAsync("no_such_action_xyz", "");
        Assert.False(Ok(r));
        Assert.Contains("no_such_action_xyz", Prop(r, "error"));
    }

    [Fact]
    public async Task Slash_Prefix_Normalized()
    {
        var r = await RemoteActions.ExecuteAsync("/uptime", "");
        Assert.True(Ok(r));
    }

    [Theory]
    [InlineData("uptime")]
    [InlineData("battery")]
    [InlineData("free")]
    [InlineData("perf")]
    [InlineData("sysinfo")]
    public async Task ReadOnly_Info_Actions_Ok(string action)
    {
        var r = await RemoteActions.ExecuteAsync(action, "");
        Assert.True(Ok(r), $"{action}: {Json(r)}");
        Assert.NotEmpty(Prop(r, "message"));
    }

    [Fact]
    public async Task Processes_Returns_Items()
    {
        var r = await RemoteActions.ExecuteAsync("processes", "");
        Assert.True(Ok(r));
        Assert.True(El(r).TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task Volume_Garbage_Fails_Without_Touching_Hardware()
    {
        var r = await RemoteActions.ExecuteAsync("volume", "blah-blah");
        Assert.False(Ok(r));
        Assert.Contains("0-100", Prop(r, "error"));
    }

    [Fact]
    public async Task Brightness_Garbage_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("brightness", "blah");
        Assert.False(Ok(r));
    }

    [Fact]
    public async Task Mouse_Move_Garbage_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("mouse_move", "blah");
        Assert.False(Ok(r));
    }

    [Fact]
    public async Task Mouse_MoveTo_Garbage_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("mouse_move_to", "0.5");
        Assert.False(Ok(r));
    }

    [Fact]
    public async Task Key_Empty_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("key", "");
        Assert.False(Ok(r));
    }

    [Fact]
    public async Task Type_Empty_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("type", "   ");
        Assert.False(Ok(r));
    }

    [Fact]
    public async Task Wol_Empty_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("wol", "");
        Assert.False(Ok(r));
        Assert.Contains("MAC", Prop(r, "error"));
    }

    [Fact]
    public async Task Unlock_Empty_Fails_And_Never_Echoes_Password()
    {
        var r = await RemoteActions.ExecuteAsync("unlock", "");
        Assert.False(Ok(r));
        Assert.DoesNotContain("secret", Json(r));
    }

    [Fact]
    public async Task Cmd_Empty_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("cmd", "   ");
        Assert.False(Ok(r));
    }

    [Fact]
    public async Task Ls_Missing_Folder_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("ls", "Z:\\definitely\\not\\here\\sgtest");
        Assert.False(Ok(r));
    }

    [Fact]
    public async Task Kill_Empty_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("close", "");
        // OpenAppAsync/KillProcessAsync возвращают Ok(сообщение), не Fail
        Assert.True(Ok(r));
        Assert.Contains("Enter", Prop(r, "message"));
    }

    [Fact]
    public void GetStatus_Shape()
    {
        var e = El(RemoteActions.GetStatus());
        Assert.True(e.TryGetProperty("ok", out _));
        Assert.True(e.TryGetProperty("machine", out _));
        Assert.True(e.TryGetProperty("disks", out _));
    }
}
