// Свич клавиатуры WebApp: выкл — key/type отклоняются, вкл — валидация как раньше.
// unlock НЕ гейтится (отдельный сценарий с подтверждением).
using System.Text.Json;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class RemoteKbdTests
{
    [Fact]
    public async Task KbdSwitch_Off_Blocks_Key_And_Type_On_Restores()
    {
        var prev = RemoteInputService.KeyboardEnabled;
        try
        {
            RemoteInputService.KeyboardEnabled = false;
            var deniedKey = await RemoteActions.ExecuteAsync("key", "enter");
            Assert.Contains("Keyboard control is off", JsonSerializer.Serialize(deniedKey));

            var deniedType = await RemoteActions.ExecuteAsync("type", "hello");
            Assert.Contains("Keyboard control is off", JsonSerializer.Serialize(deniedType));

            var on = await RemoteActions.ExecuteAsync("kbd_enable", "on");
            Assert.Contains("ON", JsonSerializer.Serialize(on));
            Assert.True(RemoteInputService.KeyboardEnabled);

            var bad = await RemoteActions.ExecuteAsync("kbd_enable", "maybe");
            Assert.Contains("on|off", JsonSerializer.Serialize(bad));
        }
        finally
        {
            RemoteInputService.KeyboardEnabled = prev;
        }
    }

    [Fact]
    public async Task Status_Reports_Input_Switches()
    {
        var e = JsonDocument.Parse(JsonSerializer.Serialize(RemoteActions.GetStatus())).RootElement;
        Assert.True(e.TryGetProperty("kbdOn", out _));
        Assert.True(e.TryGetProperty("mouseOn", out _));
    }
}
