// Свич мыши WebApp: выкл — команды отклоняются, вкл — работают.
using System.Text.Json;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class RemoteMouseTests
{
    [Fact]
    public async Task MouseSwitch_Off_Blocks_Move_And_On_Restores()
    {
        var prev = RemoteInputService.MouseEnabled;
        try
        {
            RemoteInputService.MouseEnabled = false;
            var denied = await RemoteActions.ExecuteAsync("mouse_move", "10,10");
            Assert.Contains("Mouse control is off", JsonSerializer.Serialize(denied));

            var deniedClick = await RemoteActions.ExecuteAsync("mouse_click", "left");
            Assert.Contains("Mouse control is off", JsonSerializer.Serialize(deniedClick));

            var on = await RemoteActions.ExecuteAsync("mouse_enable", "on");
            Assert.Contains("ON", JsonSerializer.Serialize(on));
            Assert.True(RemoteInputService.MouseEnabled);

            var bad = await RemoteActions.ExecuteAsync("mouse_enable", "maybe");
            Assert.Contains("on|off", JsonSerializer.Serialize(bad));
        }
        finally
        {
            RemoteInputService.MouseEnabled = prev;
        }
    }
}
