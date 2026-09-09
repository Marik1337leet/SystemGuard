using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SystemGuard.Desktop.Services;

// ... (см. историю правок в шапке исходного файла)
public class LocalizationService : INotifyPropertyChanged
{
    // Единый общий экземпляр — язык переключается сразу во всём приложении.
    // Lazy: конструктор откладывается до первого обращения, когда все static-поля
    // (_languageCodes, _menuTranslations) уже инициализированы. Прямое `= new()`
    // падало с NRE: инициализатор Instance шёл раньше словарей по порядку полей.
    private static readonly Lazy<LocalizationService> _lazyInstance = new(() => new LocalizationService());
    public static LocalizationService Instance => _lazyInstance.Value;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? LanguageChanged;

    private Dictionary<string, string> _strings = new();
    private string _currentLanguage = "en";

    public string CurrentLanguage => _currentLanguage;

    // Соответствие отображаемого имени и кода языка — единый источник истины.
    // Полные переводы: en/ru. Остальные языки реально переключаются и сохраняются,
    // непереведённые строки откатываются на английский (без заглушек в коде).
    private static readonly Dictionary<string, string> _languageCodes = new()
    {
        ["English"] = "en",
        ["Русский"] = "ru",
        ["Deutsch"] = "de",
        ["Français"] = "fr",
        ["Español"] = "es",
        ["Português"] = "pt",
        ["Українська"] = "uk",
        ["Қазақша"] = "kk",
        ["中文"] = "zh",
        ["日本語"] = "ja",
        ["한국어"] = "ko"
    };

    private static readonly Dictionary<string, string> _menuTranslations = new()
    {
        // Dashboard
        ["de:Dashboard"] = "Übersicht", ["fr:Dashboard"] = "Tableau de bord", ["es:Dashboard"] = "Panel",
        ["pt:Dashboard"] = "Painel", ["uk:Dashboard"] = "Моніторинг", ["kk:Dashboard"] = "Мониторинг",
        ["zh:Dashboard"] = "监控", ["ja:Dashboard"] = "ダッシュボード", ["ko:Dashboard"] = "대시보드",
        // Processes
        ["de:Processes"] = "Prozesse", ["fr:Processes"] = "Processus", ["es:Processes"] = "Procesos",
        ["pt:Processes"] = "Processos", ["uk:Processes"] = "Процеси", ["kk:Processes"] = "Процестер",
        ["zh:Processes"] = "进程", ["ja:Processes"] = "プロセス", ["ko:Processes"] = "프로세스",
        // Cleanup
        ["de:Cleanup"] = "Bereinigung", ["fr:Cleanup"] = "Nettoyage", ["es:Cleanup"] = "Limpieza",
        ["pt:Cleanup"] = "Limpeza", ["uk:Cleanup"] = "Очищення", ["kk:Cleanup"] = "Тазалау",
        ["zh:Cleanup"] = "清理", ["ja:Cleanup"] = "クリーンアップ", ["ko:Cleanup"] = "정리",
        // Gaming
        ["de:Gaming"] = "Spiele", ["fr:Gaming"] = "Jeux", ["es:Gaming"] = "Juegos",
        ["pt:Gaming"] = "Jogos", ["uk:Gaming"] = "Ігри", ["kk:Gaming"] = "Ойындар",
        ["zh:Gaming"] = "游戏", ["ja:Gaming"] = "ゲーム", ["ko:Gaming"] = "게임",
        // Performance
        ["de:Performance"] = "Leistung", ["fr:Performance"] = "Performance", ["es:Performance"] = "Rendimiento",
        ["pt:Performance"] = "Desempenho", ["uk:Performance"] = "Продуктивність", ["kk:Performance"] = "Өнімділік",
        ["zh:Performance"] = "性能", ["ja:Performance"] = "パフォーマンス", ["ko:Performance"] = "성능",
        // Network
        ["de:Network"] = "Netzwerk", ["fr:Network"] = "Réseau", ["es:Network"] = "Red",
        ["pt:Network"] = "Rede", ["uk:Network"] = "Мережа", ["kk:Network"] = "Желі",
        ["zh:Network"] = "网络", ["ja:Network"] = "ネットワーク", ["ko:Network"] = "네트워크",
        // Startup
        ["de:Startup"] = "Autostart", ["fr:Startup"] = "Démarrage", ["es:Startup"] = "Inicio",
        ["pt:Startup"] = "Inicialização", ["uk:Startup"] = "Автозапуск", ["kk:Startup"] = "Автожүктеу",
        ["zh:Startup"] = "启动", ["ja:Startup"] = "スタートアップ", ["ko:Startup"] = "시작프로그램",
        // Settings
        ["de:Settings"] = "Einstellungen", ["fr:Settings"] = "Paramètres", ["es:Settings"] = "Ajustes",
        ["pt:Settings"] = "Configurações", ["uk:Settings"] = "Налаштування", ["kk:Settings"] = "Баптаулар",
        ["zh:Settings"] = "设置", ["ja:Settings"] = "設定", ["ko:Settings"] = "설정",
        // Uninstaller
        ["de:Uninstaller"] = "Deinstallation", ["fr:Uninstaller"] = "Désinstallation", ["es:Uninstaller"] = "Desinstalador",
        ["pt:Uninstaller"] = "Desinstalador", ["uk:Uninstaller"] = "Видалення", ["kk:Uninstaller"] = "Жою",
        ["zh:Uninstaller"] = "卸载", ["ja:Uninstaller"] = "アンインストーラー", ["ko:Uninstaller"] = "제거",
        // Scheduler
        ["de:Scheduler"] = "Planer", ["fr:Scheduler"] = "Planificateur", ["es:Scheduler"] = "Programador",
        ["pt:Scheduler"] = "Agendador", ["uk:Scheduler"] = "Планувальник", ["kk:Scheduler"] = "Жоспарлаушы",
        ["zh:Scheduler"] = "计划任务", ["ja:Scheduler"] = "スケジューラ", ["ko:Scheduler"] = "스케줄러",
        // Telegram
        ["de:Telegram"] = "Telegram", ["fr:Telegram"] = "Telegram", ["es:Telegram"] = "Telegram",
        ["pt:Telegram"] = "Telegram", ["uk:Telegram"] = "Телеграм", ["kk:Telegram"] = "Телеграм",
        ["zh:Telegram"] = "电报", ["ja:Telegram"] = "テレグラム", ["ko:Telegram"] = "텔레그램",
        // Security
        ["de:Security"] = "Sicherheit", ["fr:Security"] = "Sécurité", ["es:Security"] = "Seguridad",
        ["pt:Security"] = "Segurança", ["uk:Security"] = "Безпека", ["kk:Security"] = "Қауіпсіздік",
        ["zh:Security"] = "安全", ["ja:Security"] = "セキュリティ", ["ko:Security"] = "보안",
        // License
        ["de:License"] = "Lizenz", ["fr:License"] = "Licence", ["es:License"] = "Licencia",
        ["pt:License"] = "Licença", ["uk:License"] = "Ліцензія", ["kk:License"] = "Лицензия",
        ["zh:License"] = "许可证", ["ja:License"] = "ライセンス", ["ko:License"] = "라이선스",
    };

    public LocalizationService()
    {
        LoadStrings(_currentLanguage);
    }

    public void SetLanguage(string langOrDisplayName)
    {
        // Принимаем и код ("en"/"de"...), и отображаемое имя ("English"/"Deutsch"...).
        // null/пусто (битый settings.json) — безопасный fallback на английский, а не краш.
        string code;
        if (string.IsNullOrWhiteSpace(langOrDisplayName))
            code = "en";
        else if (_languageCodes.TryGetValue(langOrDisplayName, out var mapped))
            code = mapped;
        else
            code = langOrDisplayName;

        if (!_languageCodes.ContainsValue(code)) code = "en";
        if (code == _currentLanguage && _strings.Count > 0) return;

        _currentLanguage = code;
        LoadStrings(code);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(code);
    }

    private void LoadStrings(string lang)
    {
        var baseStrings = lang == "ru" ? GetRussianStrings() : GetEnglishStrings();
        // Поверх базы накладываем переводы меню для выбранного языка.
        // Защита от null: при статической инициализации словарь может быть ещё не готов.
        var menu = _menuTranslations;
        if (menu == null) { _strings = baseStrings; return; }
        foreach (KeyValuePair<string, string> kv in menu)
        {
            if (kv.Key == null) continue;
            var sep = kv.Key.IndexOf(':');
            if (sep < 0) continue;
            if (kv.Key[..sep] == lang) baseStrings[kv.Key[(sep + 1)..]] = kv.Value ?? "";
        }
        _strings = baseStrings;
    }

    public string Get(string key, string defaultValue = "")
        => _strings != null && _strings.TryGetValue(key, out var value) ? value : defaultValue;

    public string this[string key] => Get(key, key);

    public string[] GetAvailableLanguages() => new[] { "English", "Русский", "Deutsch", "Français", "Español", "Português", "Українська", "Қазақша", "中文", "日本語", "한국어" };

    // Возвращает код языка по отображаемому имени — используется в UI
    public string GetLanguageCode(string displayName)
        => !string.IsNullOrEmpty(displayName) && _languageCodes.TryGetValue(displayName, out var code) ? code : "en";

    public string GetDisplayName(string code) => code switch
    {
        "ru" => "Русский",
        "de" => "Deutsch",
        "fr" => "Français",
        "es" => "Español",
        "pt" => "Português",
        "uk" => "Українська",
        "kk" => "Қазақша",
        "zh" => "中文",
        "ja" => "日本語",
        "ko" => "한국어",
        _ => "English"
    };

    private Dictionary<string, string> GetEnglishStrings() => new()
    {
        ["Dashboard"] = "Dashboard",
        ["Processes"] = "Processes",
        ["Cleanup"] = "Cleanup",
        ["Power"] = "Power",
        ["Gaming"] = "Gaming",
        ["Performance"] = "Performance",
        ["Startup"] = "Startup",
        ["Network"] = "Network",
        ["Telegram Bot"] = "Telegram Bot",
        ["Telegram"] = "Telegram",
        ["Scheduler"] = "Scheduler",
        ["Settings"] = "Settings",
        ["Security"] = "Security",
        ["License"] = "License",
        ["Startup & Services"] = "Startup & Services",
        ["Shutdown"] = "Shutdown",
        ["Restart"] = "Restart",
        ["Sleep"] = "Sleep",
        ["Lock"] = "Lock",
        ["Hibernate"] = "Hibernate",
        ["Save"] = "Save",
        ["Cancel"] = "Cancel",
        ["Delete"] = "Delete",
        ["Refresh"] = "Refresh",
        ["Search"] = "Search",
        ["Loading"] = "Loading...",
        ["Ready"] = "Ready",
        ["Error"] = "Error",
        ["Success"] = "Success",
        ["Warning"] = "Warning",
        ["Active"] = "Active",
        ["Inactive"] = "Inactive",
        ["Enabled"] = "Enabled",
        ["Disabled"] = "Disabled",
        ["Running"] = "Running",
        ["Stopped"] = "Stopped",
        ["CPU"] = "CPU",
        ["GPU"] = "GPU",
        ["Memory"] = "Memory",
        // Дубликат "Network" удалён — ключ уже определён выше
        ["Temperature"] = "Temperature",
        ["Usage"] = "Usage",
        ["Speed"] = "Speed",
        ["Download"] = "Download",
        ["Upload"] = "Upload",
        ["System"] = "System",
        ["Automation"] = "Automation",
        ["Power off"] = "Power off",
        ["Reboot"] = "Reboot",
        ["Low power"] = "Low power",
        ["Deep sleep"] = "Deep sleep",
        ["Secure"] = "Secure",
        ["Timer"] = "Timer",
        ["Appearance"] = "Appearance",
        ["Colors"] = "Colors",
        ["Uninstaller"] = "Uninstaller",
        ["Packs"] = "Packs",
        ["Docks"] = "Docks",
        ["Widgets"] = "Widgets",
        ["Taskbar"] = "Taskbar",
        ["Tools"] = "Tools",
        ["Privacy"] = "Privacy",
        ["Uninstall"] = "Uninstall",
        ["Account"] = "Account",
        ["Monthly"] = "Monthly",
        ["HalfYear"] = "Half-Year",
        ["Yearly"] = "Yearly",
        ["Lifetime"] = "Lifetime",
        ["MacAppearance"] = "Appearance",
        ["SafeThemingTitle"] = "System appearance — safe and reversible",
        ["SafeThemingDesc"] = "SystemGuard applies wallpaper, cursors, icons, taskbar tweaks, dock and widgets. No system files are modified.",
        ["CreateRestorePoint"] = "Create restore point",
        ["WindowsThemes"] = "Windows Themes",
        ["SetWallpaper"] = "Set wallpaper…",
        ["Installed"] = "INSTALLED",
        ["Launch"] = "Launch",
        ["Download"] = "Download",
        ["RestoreDefaults"] = "Restore defaults",
        ["RestartExplorer"] = "Restart Explorer to apply",
        ["CenterIcons"] = "Center icons (Windows 11)",
        ["CenterIconsDesc"] = "StartAllBack-style centered taskbar",
        ["WidgetsBoard"] = "Widgets board",
        ["WidgetsBoardDesc"] = "Weather and news feed button",
        ["ChatTeams"] = "Chat (Teams)",
        ["ChatTeamsDesc"] = "Built-in chat button",
        ["TaskViewBtn"] = "Task View",
        ["TaskViewBtnDesc"] = "Virtual desktops button",
        ["TransparencyOpt"] = "Transparency",
        ["TransparencyDesc"] = "Acrylic taskbar and menus",
        ["DarkModeOpt"] = "Dark mode",
        ["DarkModeDesc"] = "System and apps theme",
        ["NoPacks"] = "No packs in this section.",
        ["NoDocks"] = "No docks in this section.",
        ["NoWidgets"] = "No widgets in this section.",
    };

    private Dictionary<string, string> GetRussianStrings() => new()
    {
        ["Dashboard"] = "Мониторинг",
        ["Processes"] = "Процессы",
        ["Cleanup"] = "Очистка",
        ["Power"] = "Питание",
        ["Gaming"] = "Игры",
        ["Performance"] = "Производительность",
        ["Startup"] = "Автозагрузка",
        ["Network"] = "Сеть", // ← было дважды, теперь один раз
        ["Telegram Bot"] = "Телеграм Бот",
        ["Telegram"] = "Телеграм",
        ["Scheduler"] = "Планировщик",
        ["Settings"] = "Настройки",
        ["Security"] = "Безопасность",
        ["License"] = "Лицензия",
        ["Startup & Services"] = "Автозагрузка",
        ["Shutdown"] = "Выключить",
        ["Restart"] = "Перезагрузить",
        ["Sleep"] = "Сон",
        ["Lock"] = "Блокировка",
        ["Hibernate"] = "Гибернация",
        ["Save"] = "Сохранить",
        ["Cancel"] = "Отмена",
        ["Delete"] = "Удалить",
        ["Refresh"] = "Обновить",
        ["Search"] = "Поиск",
        ["Loading"] = "Загрузка...",
        ["Ready"] = "Готов",
        ["Error"] = "Ошибка",
        ["Success"] = "Успешно",
        ["Warning"] = "Внимание",
        ["Active"] = "Активен",
        ["Inactive"] = "Неактивен",
        ["Enabled"] = "Включен",
        ["Disabled"] = "Отключен",
        ["Running"] = "Запущен",
        ["Stopped"] = "Остановлен",
        ["CPU"] = "Процессор",
        ["GPU"] = "Видеокарта",
        ["Memory"] = "Память",
        ["Temperature"] = "Температура",
        ["Usage"] = "Загрузка",
        ["Speed"] = "Скорость",
        ["Download"] = "Загрузка",
        ["Upload"] = "Отдача",
        ["System"] = "Система",
        ["Automation"] = "Автоматизация",
        ["Power off"] = "Выключение",
        ["Reboot"] = "Перезагрузка",
        ["Low power"] = "Энергосбережение",
        ["Deep sleep"] = "Глубокая спячка",
        ["Secure"] = "Защита",
        ["Timer"] = "Таймер",
        ["Appearance"] = "Оформление",
        ["Colors"] = "Цвета",
        ["Uninstaller"] = "Деинсталлятор",
        ["Packs"] = "Паки",
        ["Docks"] = "Доки",
        ["Widgets"] = "Виджеты",
        ["Taskbar"] = "Панель задач",
        ["Tools"] = "Инструменты",
        ["Privacy"] = "Приватность",
        ["Uninstall"] = "Удаление",
        ["Account"] = "Аккаунт",
        ["Monthly"] = "Месяц",
        ["HalfYear"] = "Полгода",
        ["Yearly"] = "Год",
        ["Lifetime"] = "Навсегда",
        ["MacAppearance"] = "Оформление",
        ["SafeThemingTitle"] = "Оформление системы — безопасно и обратимо",
        ["SafeThemingDesc"] = "SystemGuard настраивает обои, курсоры, значки, панель задач, док и виджеты. Системные файлы не изменяются.",
        ["CreateRestorePoint"] = "Создать точку восстановления",
        ["WindowsThemes"] = "Темы Windows",
        ["SetWallpaper"] = "Обои…",
        ["Installed"] = "УСТАНОВЛЕНО",
        ["Launch"] = "Запустить",
        ["Download"] = "Скачать",
        ["RestoreDefaults"] = "Сбросить",
        ["RestartExplorer"] = "Перезапустить Explorer",
        ["CenterIcons"] = "Иконки по центру (Windows 11)",
        ["CenterIconsDesc"] = "Центрированная панель в стиле StartAllBack",
        ["WidgetsBoard"] = "Виджеты",
        ["WidgetsBoardDesc"] = "Кнопка погоды и новостей",
        ["ChatTeams"] = "Чат (Teams)",
        ["ChatTeamsDesc"] = "Встроенная кнопка чата",
        ["TaskViewBtn"] = "Представление задач",
        ["TaskViewBtnDesc"] = "Кнопка виртуальных рабочих столов",
        ["TransparencyOpt"] = "Прозрачность",
        ["TransparencyDesc"] = "Акриловая панель задач и меню",
        ["DarkModeOpt"] = "Тёмная тема",
        ["DarkModeDesc"] = "Тема системы и приложений",
        ["NoPacks"] = "В этом разделе паков нет.",
        ["NoDocks"] = "В этом разделе доков нет.",
        ["NoWidgets"] = "В этом разделе виджетов нет.",
    };
}
