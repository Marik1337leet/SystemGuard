using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

namespace SystemGuard.Desktop.Services;

public interface IFileDialogService
{
    Task<string?> PickExecutableAsync();
}

public class FileDialogService : IFileDialogService
{
    public async Task<string?> PickExecutableAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;

        var topLevel = TopLevel.GetTopLevel(desktop.MainWindow);
        if (topLevel?.StorageProvider == null) return null;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select game executable",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executable files") { Patterns = new[] { "*.exe" } }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }
}