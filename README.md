# 🛡 SystemGuard

Мощный системный монитор и твикер для Windows (Avalonia, .NET 10) + Telegram-бот с WebApp-пультом.

## Проекты

| Проект | Что это |
|---|---|
| `SystemGuard.Desktop` | Основное приложение: дашборд, процессы, сеть, игры, Telegram-бот |
| `SystemGuard.Core` | Общие интерфейсы и модели |
| `SystemGuard.KeyGenerator` | Генератор лицензионных ключей |
| `SystemGuard.MiniApp` | Telegram WebApp-пульт (деплой: GitHub Pages → [`systemguard-miniapp`](https://github.com/Marik1337leet/systemguard-miniapp)) |

## Сборка

```powershell
dotnet build SystemGuard.Desktop/SystemGuard.Desktop.csproj
```

Требуется: .NET 10 SDK, Windows 10/11.

## Telegram-бот

1. Создайте бота через [@BotFather](https://t.me/BotFather), получите токен.
2. Узнайте свой Chat ID через [@userinfobot](https://t.me/userinfobot).
3. В приложении: **Настройки → Telegram** → вставьте токен и Chat ID → **Connect**.
4. В чате с ботом: `/start`. Кнопка **🌐 Web App** открывает мини-приложение.

Команды понимают аргумент в одну строку: `/ls C:\Games`, `/cmd ipconfig`,
`/volume 70`, `/open chrome`, `/close notepad`.

### WebApp

Мини-приложение отправляет команды через `Telegram.WebApp.sendData`
(без токенов в браузере), ответы бота приходят сообщениями в чат.
Хостинг — GitHub Pages (репозиторий `systemguard-miniapp`).

## Производительность

- Опрос железа: раз в ~2 с, без перекрытия тиков, тяжёлая работа вне UI-потока
- Графики обновляются раз в ~4 с (окно 40 точек)
- Трафик сети считается в фоне
- Бот: polling-цикл отделён от исполнения команд, отправка через rate-limit шлюз с обработкой FloodWait (429)
