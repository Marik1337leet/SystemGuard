using System.Collections.Generic;
using System.Linq;
using SystemGuard.Desktop.Models;

namespace SystemGuard.Desktop.Services;

// Каталог действий для хоткеев: любое действие из списка можно забиндить.
public static class HotkeyCatalog
{
    public record ActionDef(string Id, string Title, string DefaultCombo, string Hint);

    public static readonly List<ActionDef> Actions = new()
    {
        new("optimize", "Оптимизировать RAM", "Ctrl+Alt+O", "Trim working sets + standby"),
        new("gameboost", "Game Boost (ускорить игру)", "Ctrl+Alt+G", "Завершить фон, поднять приоритет"),
        new("widget", "Вкл/выкл виджет", "Ctrl+Alt+W", "Показать/скрыть виджеты"),
        new("screenshot", "Скриншот экрана", "Ctrl+Alt+S", "Сохранить кадр на Рабочий стол"),
        new("mute", "Мут / анмут", "Ctrl+Alt+M", "Выкл/вкл звук"),
        new("lock", "Блокировать ПК", "Ctrl+Alt+L", "Win+L без рук"),
    };

    // Стандартных биндов больше нет: пользователь сам создаёт нужные
    // из палитры Actions ниже. Пустой список = хук висит, но ничего не перехватывает.
    public static List<HotkeyBinding> Defaults() => new();

    public static string TitleOf(string id) =>
        Actions.FirstOrDefault(a => a.Id == id)?.Title ?? id;

    // Нормализация комбо: "ctrl + alt + o" -> "Ctrl+Alt+O"
    public static string Normalize(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return "";
        var parts = combo.Split('+', System.StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Where(p => p.Length > 0).ToList();
        var mods = new List<string>();
        if (parts.Contains("ctrl")) mods.Add("Ctrl");
        if (parts.Contains("alt")) mods.Add("Alt");
        if (parts.Contains("shift")) mods.Add("Shift");
        if (parts.Contains("win")) mods.Add("Win");
        var key = parts.FirstOrDefault(p => p is not ("ctrl" or "alt" or "shift" or "win" or "control" or "windows"));
        if (key == "control") key = null;
        if (string.IsNullOrEmpty(key)) return string.Join("+", mods);
        string keyUp = key.Length == 1 ? key.ToUpperInvariant()
            : char.ToUpperInvariant(key[0]) + key[1..];
        mods.Add(keyUp);
        return string.Join("+", mods);
    }
}
