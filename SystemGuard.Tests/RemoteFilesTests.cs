// Файловые экшены WebApp: листинг, диски, создание/удаление, загрузка.
// Трогаем только временные каталоги в %TEMP%; системные пути — лишь
// для проверки отказа (ничего не удаляем).
using System.Text;
using System.Text.Json;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class RemoteFilesTests
{
    private static string Json(object o) => JsonSerializer.Serialize(o);
    private static JsonElement El(object o) => JsonDocument.Parse(Json(o)).RootElement;
    private static bool Ok(object o) => El(o).TryGetProperty("ok", out var v) && v.ValueKind == JsonValueKind.True;
    private static string Prop(object o, string name)
    {
        var e = El(o);
        return e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";
    }

    private static string TempRoot()
    {
        var d = Path.Combine(Path.GetTempPath(), "sgtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task Drives_Returns_Items()
    {
        var r = await RemoteActions.ExecuteAsync("drives", "");
        Assert.True(Ok(r), Json(r));
        Assert.True(El(r).TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array);
    }

    [Fact]
    public async Task Ls_Puts_Dirs_First_And_Returns_Parent()
    {
        var root = TempRoot();
        try
        {
            File.WriteAllText(Path.Combine(root, "zfile.txt"), "x");
            Directory.CreateDirectory(Path.Combine(root, "adir"));
            var r = await RemoteActions.ExecuteAsync("ls", root);
            Assert.True(Ok(r), Json(r));
            var items = El(r).GetProperty("items").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal("dir", items[0].GetProperty("type").GetString());
            Assert.Equal("adir", items[0].GetProperty("name").GetString());
            Assert.Equal("file", items[1].GetProperty("type").GetString());
            Assert.False(string.IsNullOrEmpty(Prop(r, "parent")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Mkdir_And_Delete_File_Roundtrip()
    {
        var root = TempRoot();
        try
        {
            var sub = Path.Combine(root, "newdir");
            var mk = await RemoteActions.ExecuteAsync("file_mkdir", sub);
            Assert.True(Ok(mk), Json(mk));
            Assert.True(Directory.Exists(sub));

            var dup = await RemoteActions.ExecuteAsync("file_mkdir", sub);
            Assert.False(Ok(dup));

            var f = Path.Combine(sub, "a.txt");
            File.WriteAllText(f, "hello");
            var del = await RemoteActions.ExecuteAsync("file_delete", f);
            Assert.True(Ok(del), Json(del));
            Assert.False(File.Exists(f));

            var delDir = await RemoteActions.ExecuteAsync("file_delete", sub);
            Assert.True(Ok(delDir), Json(delDir));
            Assert.False(Directory.Exists(sub));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task Delete_Blocked_Paths_Fail()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var r = await RemoteActions.ExecuteAsync("file_delete", win);
        Assert.False(Ok(r), Json(r));

        var empty = await RemoteActions.ExecuteAsync("file_delete", "");
        Assert.False(Ok(empty));

        var missing = await RemoteActions.ExecuteAsync("file_delete",
            Path.Combine(Path.GetTempPath(), "sg_nope_" + Guid.NewGuid().ToString("N")));
        Assert.False(Ok(missing));
    }

    [Fact]
    public void SaveUpload_Roundtrip_And_Traversal_Sanitized()
    {
        var root = TempRoot();
        try
        {
            var data = Encoding.UTF8.GetBytes("phone photo bytes");
            var (ok, _, saved) = RemoteActions.SaveUploadFile(root, "pic.jpg", data);
            Assert.True(ok);
            Assert.NotNull(saved);
            Assert.Equal(data, File.ReadAllBytes(saved!));

            // Коллизия имени — второй файл не затирает первый
            var (ok2, _, saved2) = RemoteActions.SaveUploadFile(root, "pic.jpg", data);
            Assert.True(ok2);
            Assert.NotEqual(saved, saved2);

            // Траверс каталога — файл остаётся внутри выбранной папки
            var (ok3, _, saved3) = RemoteActions.SaveUploadFile(root, "..\\evil.txt", data);
            Assert.True(ok3);
            Assert.NotNull(saved3);
            Assert.Equal(root, Path.GetDirectoryName(Path.GetFullPath(saved3!)));
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "evil.txt")));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public void SaveUpload_Rejects_Empty_Oversize_And_BlockedDir()
    {
        var root = TempRoot();
        try
        {
            var (okEmpty, _, _) = RemoteActions.SaveUploadFile(root, "a.bin", Array.Empty<byte>());
            Assert.False(okEmpty);

            var (okBig, msgBig, _) = RemoteActions.SaveUploadFile(root, "a.bin", new byte[10], maxBytes: 4);
            Assert.False(okBig);
            Assert.Contains("over", msgBig);

            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var (okWin, _, _) = RemoteActions.SaveUploadFile(win, "a.bin", new byte[] { 1 });
            Assert.False(okWin);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
