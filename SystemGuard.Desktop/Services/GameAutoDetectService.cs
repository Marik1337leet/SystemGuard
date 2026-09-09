using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SystemGuard.Desktop.Services;

// Автодетект запущенных игр: поллинг имён процессов по списку exe профилей.
// При появлении игры — OnGameDetected(exeName), при исчезновении всех — OnAllGamesExited.
public sealed class GameAutoDetectService : IDisposable
{
    private System.Timers.Timer? _timer;
    private Func<IEnumerable<string>>? _exeProvider;
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    // Процессы, уже работавшие на момент Start: их появление — НЕ запуск игры.
    // Иначе открытие вкладки при уже запущенной игре тут же включало Game Mode.
    private readonly HashSet<int> _baselinePids = new();
    private DateTime _startTime = DateTime.Now;
    private readonly object _lock = new();
    private bool _disposed;

    public event Action<string>? OnGameDetected;
    public event Action? OnAllGamesExited;

    public bool IsRunning => _timer != null;

    public void Start(Func<IEnumerable<string>> exeNamesProvider, int intervalMs = 5000)
    {
        Stop();
        _exeProvider = exeNamesProvider;
        _startTime = DateTime.Now;
        // Baseline: всё, что уже запущено — не считаем "запуском игры"
        try
        {
            lock (_lock)
                foreach (var p in Process.GetProcesses())
                {
                    try { _baselinePids.Add(p.Id); } catch { }
                    try { p.Dispose(); } catch { }
                }
        }
        catch { }
        _timer = new System.Timers.Timer(Math.Clamp(intervalMs, 2000, 30000)) { AutoReset = true };
        _timer.Elapsed += (_, _) => Poll();
        _timer.Start();
        Poll();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _timer?.Stop();
            _timer?.Dispose();
            _timer = null;
            _active.Clear();
            _baselinePids.Clear();
        }
    }

    private void Poll()
    {
        Func<IEnumerable<string>>? provider;
        lock (_lock) provider = _exeProvider;
        if (provider == null) return;
        HashSet<string> wanted;
        try
        {
            wanted = new HashSet<string>(
                provider().Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim().Trim('"'))
                    .Select(s => System.IO.Path.GetFileNameWithoutExtension(s)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch { return; }
        if (wanted.Count == 0) return;

        // Кандидаты: только процессы, стартовавшие ПОСЛЕ включения вотчера.
        // Проверка двойная: PID не из baseline И StartTime новее старта вотчера
        // (защита от переиспользования PID системой).
        HashSet<string> fresh = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            DateTime start;
            HashSet<int> baseline;
            lock (_lock) { start = _startTime; baseline = new HashSet<int>(_baselinePids); }
            foreach (var p in Process.GetProcesses())
            {
                string name;
                try { name = p.ProcessName; } catch { try { p.Dispose(); } catch { } continue; }
                try
                {
                    if (!string.IsNullOrEmpty(name)
                        && !baseline.Contains(p.Id) && p.StartTime >= start - TimeSpan.FromSeconds(5))
                        fresh.Add(name);
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
        }
        catch { return; }

        var found = wanted.FirstOrDefault(w => fresh.Contains(w));
        lock (_lock)
        {
            if (_disposed) return;
            if (found != null)
            {
                if (_active.Add(found))
                    try { OnGameDetected?.Invoke(found); } catch { }
            }
            else if (_active.Count > 0)
            {
                _active.Clear();
                try { OnAllGamesExited?.Invoke(); } catch { }
            }
        }
    }

    public void Dispose()
    {
        lock (_lock) _disposed = true;
        Stop();
    }
}
