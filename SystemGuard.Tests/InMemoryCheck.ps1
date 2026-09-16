# In-memory проверка БЕЗ запуска бинарников (Smart App Control режет свежие
# сборки): читает РЕАЛЬНЫЕ .cs/.kt файлы и проверяет их содержимое и поведение
# паттернов. Запуск: powershell -ExecutionPolicy Bypass -File InMemoryCheck.ps1
# Код выполняется в доверенном powershell.exe, новые файлы не создаются.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pass = 0; $fail = 0
function Check([bool]$ok, [string]$name, [string]$detail = "") {
  if ($ok) { $script:pass++; Write-Host "[PASS] $name" }
  else { $script:fail++; Write-Host "[FAIL] $name :: $detail" }
}
function ReadSrc([string]$rel) {
  Get-Content -LiteralPath (Join-Path $root $rel) -Raw -Encoding UTF8
}

# ── 1. Regex-строки из РЕАЛЬНОГО TunnelService.cs ──────────────────────
$tunnel = ReadSrc "SystemGuard.Desktop/Services/TunnelService.cs"
function Extract-Verbatim([string]$src, [string]$varName) {
  $m = [regex]::Match($src, [regex]::Escape($varName) + '\s*=\s*new\(@?"(?<p>(?:[^"]|"")+)"')
  if (-not $m.Success) { throw "pattern $varName not found" }
  return $m.Groups["p"].Value.Replace('""', '"')
}
$lhrPat = Extract-Verbatim $tunnel "LhrUrlRx"
$pinggyPat = Extract-Verbatim $tunnel "PinggyUrlRx"
Check ($lhrPat.Contains('lhr\.life') -and $lhrPat.Contains('localhost\.run')) "tunnel/lhr pattern has all domains"
Check ($lhrPat.Contains('(?!admin\.)')) "tunnel/lhr rejects admin subdomain"
# serveo выкинут 13.09.2026 (Permission denied и с ключом, и без):
# ни таргета в цепочке, ни парсера (упоминание только в комментарии).
Check (-not $tunnel.Contains('SshTarget("serveo"') -and -not $tunnel.Contains("PickServeoUrl")) "tunnel/serveo removed"

$lhr = New-Object regex @($lhrPat, [Text.RegularExpressions.RegexOptions]::IgnoreCase)
$pinggy = New-Object regex @($pinggyPat, [Text.RegularExpressions.RegexOptions]::Compiled)
function PickLhr([string]$line) { $m = $lhr.Match($line); if ($m.Success) { return $m.Value.TrimEnd('.', ',', ')') }; return $null }
function PickPinggy([string]$line) {
  foreach ($m in $pinggy.Matches($line)) {
    $u = $m.Value.TrimEnd('.', ',', ')', "'", '"')
    $host_ = $u.Substring("https://".Length)
    if ($host_ -like "*pinggy*" -and $host_ -notlike "*dashboard*") { return $u }
  }
  return $null
}
function TryPick([string]$t) { $r = PickLhr $t; if ($r) { return $r }; return (PickPinggy $t) }

Check ((PickLhr "Forwarding https://abc-def-1-2-3-4.lhr.life -> 127.0.0.1:8899") -eq "https://abc-def-1-2-3-4.lhr.life") "tunnel/lhr simple (real pattern)"
Check ((PickLhr "https://admin.localhost.run status") -eq $null) "tunnel/lhr admin rejected (real pattern)"
Check ((PickLhr "url: https://nice-name.lhr.rocks") -eq "https://nice-name.lhr.rocks") "tunnel/lhr.rocks (real pattern)"
Check ((PickLhr "tunneled with tls termination, https://dc872105c69f64.lhr.life") -eq "https://dc872105c69f64.lhr.life") "tunnel/lhr live format (real output)"
Check ((PickPinggy "access at https://abc-def.a.free.pinggy.link") -eq "https://abc-def.a.free.pinggy.link") "tunnel/pinggy simple (real pattern)"
Check ((PickPinggy "dash https://dashboard.pinggy.io tun https://qwerty.a.free.pinggy.link") -eq "https://qwerty.a.free.pinggy.link") "tunnel/pinggy dashboard ignored (real pattern)"
Check ((PickPinggy "open https://dashboard.pinggy.io") -eq $null) "tunnel/pinggy only-dashboard null (real pattern)"
Check ((PickPinggy "https://mihuu-162-19-235-118.free.pinggy.net") -eq "https://mihuu-162-19-235-118.free.pinggy.net") "tunnel/pinggy net domain (live output)"
Check ((PickPinggy "https://uypwf-162-19-235-118.run.pinggy-free.link") -eq "https://uypwf-162-19-235-118.run.pinggy-free.link") "tunnel/pinggy free-link domain (live output)"
Check ((TryPick "see https://dashboard.pinggy.io and https://dc872105c69f64.lhr.life live") -eq "https://dc872105c69f64.lhr.life") "tunnel/trypick lhr over dashboard (real patterns)"
Check ((TryPick "") -eq $null -and (TryPick "no urls (ssh: connected)") -eq $null) "tunnel/trypick empty null"
$garbage = "$([char]27)[2J$([char]27)[HVisit https://dashboard.pinggy.io $([char]27)[K`ntunnel: https://ab-cd-12-34.a.free.pinggy.link, press ctrl+c"
Check ((TryPick $garbage) -eq "https://ab-cd-12-34.a.free.pinggy.link") "tunnel/pinggy TUI garbage (real patterns)"
# Ключ — только pinggy (lhr он ломает), PTY — только pinggy (иначе TUI молчит)
Check ($tunnel.Contains('-tt -p 443 -R0') -and $tunnel.Contains('free.pinggy.io')) "tunnel/pinggy key+pty wired"
Check ($tunnel.Contains('-p 22 -R 80:127.0.0.1') -and $tunnel.Contains('nokey@localhost.run')) "tunnel/lhr shell-no-key wired"
Check ($tunnel.Contains("public static string? PickLhrUrl") -and $tunnel.Contains("public static string? PickPinggyUrl") -and $tunnel.Contains("public static string? TryPickTunnelUrl")) "tunnel/parsers public static (testable)"

# ── 2. Relay-префиксы и readonly-матрица из РЕАЛЬНОГО BotRelayProtocol.cs ─
$relay = ReadSrc "SystemGuard.Desktop/Services/BotRelayProtocol.cs"
$m1 = [regex]::Match($relay, 'RequestPrefix\s*=\s*"(?<v>[^"]+)"')
$reqP = $m1.Groups["v"].Value
# Сравнение без эмодзи-литералов: сам скрипт WinPS читает как ANSI и эмодзи
# в нём превращаются в '??' — сверяем структуру (суффикс + не-ASCII маркер).
Check ($reqP.EndsWith('SG:') -and $reqP.Length -gt 3 -and ([int][char]$reqP[0] -gt 127)) "relay/prefix" "req len=$($reqP.Length)"
Check ($relay.Contains('"id"') -and $relay.Contains('"action"') -and $relay.Contains('"arg"')) "relay/json fields id/action/arg"
# Ответы — человекочитаемые (машинных SG-RESP пакетов нет: свои сообщения бот
# через getUpdates не видит, парсить ответ в приложении невозможно).
Check (-not $relay.Contains('SG-RESP') -and $relay.Contains('FormatReply')) "relay/human replies no packets"
Check ($relay.Contains("4000") -and $relay.Contains("8192")) "relay/size guards present"
# readonly-список: вытащить имена из switch-секции IsReadOnlyAction
$roBlock = [regex]::Match($relay, 'IsReadOnlyAction\(string action\)\s*=>\s*action switch\s*\{(?<b>.*?)\n\s*\};', [Text.RegularExpressions.RegexOptions]::Singleline).Groups["b"].Value
$roNames = [regex]::Matches($roBlock, '"([a-z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
foreach ($a in @("status","perf","processes","uptime","sysinfo","battery","remote","rdp")) { Check ($roNames -contains $a) "relay/readonly has $a" }
foreach ($a in @("volume","cmd","shutdown","unlock","mouse_move","type","rdp_on","remote_install")) { Check (-not ($roNames -contains $a)) "relay/readonly excludes $a" }

# ── 3. Relay встроен в бота ────────────────────────────────────────────
$bot = ReadSrc "SystemGuard.Desktop/Services/TelegramBotService.cs"
Check ($bot.Contains("HandleRelayRequest") -and $bot.Contains("BotRelayProtocol.IsRelayRequest")) "bot/relay branch wired"
Check ($bot.Contains("TryParseWebAppData") -and $bot.Contains("public static bool TryParseWebAppData")) "bot/webapp parser testable"
Check ($bot.Contains("PushLiveUrlAsync")) "bot/live-url push method"
Check ($bot.Contains("public static string ExtractCommand") -and $bot.Contains("public static string ExtractArg")) "bot/parsers public static"
Check ($bot.Contains("[relay:")) "bot/relay photo tag"

# ── 4. Автозапуск и live-флаг ──────────────────────────────────────────
$live = ReadSrc "SystemGuard.Desktop/Services/LiveServices.cs"
Check ($live.Contains("AutopublishLive") -and $live.Contains("SetAutopublish") -and $live.Contains("live_autostart")) "live/autopublish flag"
$vm = ReadSrc "SystemGuard.Desktop/ViewModels/TelegramViewModel.cs"
Check ($vm.Contains("AutoStartAsync") -and $vm.Contains("PushLiveUrlAsync") -and $vm.Contains("_lastPushedUrl") -and $vm.Contains("SetAutopublish(true)")) "vm/autostart+autopush"
$prog = ReadSrc "SystemGuard.Desktop/Program.cs"
Check ($prog.Contains("--selftest") -and $prog.Contains("SelfTestService")) "app/--selftest entry"
Check ((Test-Path (Join-Path $root "SystemGuard.Desktop/Services/SelfTestService.cs")) -and (Test-Path (Join-Path $root "SystemGuard.Desktop/Services/BotRelayProtocol.cs"))) "app/new services exist"

# ── 5. Android: relay-клиент и гибрид ──────────────────────────────────
$store = ReadSrc "SystemGuard.Android/app/src/main/java/com/systemguard/remote/SecureStore.kt"
Check ($store.Contains("botToken") -and $store.Contains("botChatId") -and $store.Contains("isRelayLinked") -and $store.Contains("isAnyLinked") -and $store.Contains("clearLive")) "android/securestore relay"
Check (Test-Path (Join-Path $root "SystemGuard.Android/app/src/main/java/com/systemguard/remote/BotRelayClient.kt")) "android/BotRelayClient.kt exists"
$main = ReadSrc "SystemGuard.Android/app/src/main/java/com/systemguard/remote/MainActivity.kt"
Check ($main.Contains("BotRelayClient") -and $main.Contains("relay.run(") -and $main.Contains("checkLink") -and $main.Contains("Live + Relay")) "android/hybrid Live+Relay"
$layout = ReadSrc "SystemGuard.Android/app/src/main/res/layout/activity_main.xml"
Check ($layout.Contains("inBotToken") -and $layout.Contains("inBotChat") -and $layout.Contains("btnRelayLink") -and $layout.Contains("txtRelayState")) "android/relay UI ids"
$api = ReadSrc "SystemGuard.Android/app/src/main/java/com/systemguard/remote/ApiClient.kt"
Check ($api.Contains("/api/action") -and $api.Contains("X-Token")) "android/apiclient intact"

# ── 6. xUnit-зеркало для будущего (после выкл. SAC) ────────────────────
Check (Test-Path (Join-Path $root "SystemGuard.Tests/BotRelayProtocolTests.cs")) "tests/relay mirror"
Check (Test-Path (Join-Path $root "SystemGuard.Tests/TunnelParserTests.cs")) "tests/tunnel mirror"
Check (Test-Path (Join-Path $root "SystemGuard.Tests/RemoteActionsTests.cs")) "tests/actions mirror"
Check (Test-Path (Join-Path $root "SystemGuard.Tests/RemoteHttpServerTests.cs")) "tests/http mirror"

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL GREEN ($pass passed)"; exit 0 } else { Write-Host "$fail FAILURES ($pass passed)"; exit 1 }
