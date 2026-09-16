# Кросс-проверки связности БЕЗ запуска бинарников (дополнение к InMemoryCheck):
# Android (viewBinding ID, API классов, manifest, зависимости) и совместимость
# протокола ПК (C#) <-> приложение (Kotlin): префиксы, поля JSON, имена команд.
# Запуск: powershell -ExecutionPolicy Bypass -File DeviceCheck.ps1
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

$andBase = "SystemGuard.Android/app/src/main"
$main = ReadSrc "$andBase/java/com/systemguard/remote/MainActivity.kt"
$relayKt = ReadSrc "$andBase/java/com/systemguard/remote/BotRelayClient.kt"
$apiKt = ReadSrc "$andBase/java/com/systemguard/remote/ApiClient.kt"
$storeKt = ReadSrc "$andBase/java/com/systemguard/remote/SecureStore.kt"
$mjpegKt = ReadSrc "$andBase/java/com/systemguard/remote/MjpegView.kt"
$layout = ReadSrc "$andBase/res/layout/activity_main.xml"
$manifest = ReadSrc "$andBase/AndroidManifest.xml"
$gradle = ReadSrc "SystemGuard.Android/app/build.gradle"

# ── 1. Layout: well-formed XML + все b.xxx существуют ──────────────────
$xmlOk = $true; try { [xml]$layout | Out-Null } catch { $xmlOk = $false }
Check $xmlOk "android/layout well-formed XML"
$ids = [regex]::Matches($layout, '@\+id/([A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$used = [regex]::Matches($main, '(?<![A-Za-z])b\.([A-Za-z0-9_]+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$used = @($used | Where-Object { $_ -ne "root" }) # b.root — встроенное свойство viewBinding, не android:id
$missing = @($used | Where-Object { $ids -notcontains $_ })
Check ($missing.Count -eq 0) "android/all viewBinding IDs exist" ($missing -join ",")
foreach ($need in @("inBotToken","inBotChat","btnRelayLink","txtRelayState","connBadge","txtState")) {
  Check ($ids -contains $need) "android/layout has $need"
}

# ── 2. API классов: всё, что дёргает MainActivity, объявлено ───────────
Check ($storeKt.Contains("var botToken") -and $storeKt.Contains("var botChatId") -and $storeKt.Contains("isRelayLinked") -and $storeKt.Contains("isAnyLinked") -and $storeKt.Contains("fun clearLive")) "android/securestore API complete"
Check ($storeKt.Contains("var baseUrl") -and $storeKt.Contains("var token") -and $storeKt.Contains("var fps") -and $storeKt.Contains("var quality")) "android/securestore live fields intact"
Check ($relayKt.Contains("suspend fun run(") -and $relayKt.Contains("suspend fun checkLink(") -and $relayKt.Contains("val isReady") -and $relayKt.Contains("class BotRelayClient")) "android/relayclient API complete"
Check ($relayKt.Contains("sealed interface RelayResult") -and $relayKt.Contains("class Ok") -and $relayKt.Contains("class Err")) "android/relayclient Result types"
Check ($apiKt.Contains("suspend fun status(") -and $apiKt.Contains("suspend fun run(") -and $apiKt.Contains("fun screenShotUrl(") -and $apiKt.Contains("fun screenStreamUrl(") -and $apiKt.Contains("fun camShotUrl(") -and $apiKt.Contains("fun camStreamUrl(") -and $apiKt.Contains("fun mediaUrl(")) "android/apiclient API intact"
Check ($mjpegKt.Contains("fun play(url: String, token: String)") -and $mjpegKt.Contains("fun stop()")) "android/mjpegview API intact"
Check ($main.Contains("private lateinit var relay: BotRelayClient")) "android/relay field declared"

# ── 3. Manifest / зависимости ──────────────────────────────────────────
Check ($manifest.Contains("android.permission.INTERNET")) "android/INTERNET permission"
Check ($manifest.Contains(".MainActivity") -and $manifest.Contains("android.intent.action.MAIN")) "android/launcher activity"
Check ($gradle.Contains("okhttp") -and $gradle.Contains("kotlinx-coroutines-android") -and $gradle.Contains("viewBinding = true")) "android/gradle deps for relay"

# ── 4. Грубая проверка баланса скобок в правленых Kotlin ───────────────
foreach ($f in @(@("MainActivity.kt",$main), @("BotRelayClient.kt",$relayKt), @("SecureStore.kt",$storeKt))) {
  $o = ([regex]::Matches($f[1], "\{").Count); $c = ([regex]::Matches($f[1], "\}").Count)
  Check ($o -eq $c) ("android/braces balanced " + $f[0]) ("{=$o }=$c")
}

# ── 5. Протокол C# <-> Kotlin: префикс запроса + PARKED-статус ─────────
$csRelay = ReadSrc "SystemGuard.Desktop/Services/BotRelayProtocol.cs"
$csReq = [regex]::Match($csRelay, 'RequestPrefix\s*=\s*"(?<v>[^"]+)"').Groups["v"].Value
$ktReq = [regex]::Match($relayKt, 'REQ_PREFIX\s*=\s*"(?<v>[^"]+)"').Groups["v"].Value
Check ($csReq.EndsWith("SG:") -and $ktReq.EndsWith("SG:") -and $csReq.Length -eq $ktReq.Length) "proto/request prefix match" ("cs=" + $csReq.Length + " kt=" + $ktReq.Length)
# Ответы — человекочитаемые (свои сообщения бот в getUpdates не видит).
Check (-not $csRelay.Contains("SG-RESP") -and $csRelay.Contains("FormatReply")) "proto/human replies no packets"
Check ($relayKt.Contains("PARKED")) "proto/kt relay marked parked"

# ── 6. Протокол: поля JSON запроса ───────────────────────────────────────
Check ($relayKt.Contains('.put("id"') -and $relayKt.Contains('.put("action"') -and $relayKt.Contains('.put("arg"')) "proto/kt request fields id/action/arg"
Check ($csRelay.Contains('"id"') -and $csRelay.Contains('"action"') -and $csRelay.Contains('"arg"')) "proto/cs request fields id/action/arg"

# ── 7. Протокол: каждая команда приложения обрабатывается ПК ───────────
$csActions = ReadSrc "SystemGuard.Desktop/Services/RemoteActions.cs"
$csBot = ReadSrc "SystemGuard.Desktop/Services/TelegramBotService.cs"
$handledByPhotos = @("screenshot","shot","cam","camera") # ветка HandleRelayRequest с фото
$cmdActions = [regex]::Matches($main, 'cmd\("([a-z_]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$uncovered = @()
foreach ($a in $cmdActions) {
  $inSwitch = $csActions.Contains('"' + $a + '"') -or $csActions.Contains("case `"$a`"")
  $inPhotos = $handledByPhotos -contains $a
  if (-not ($inSwitch -or $inPhotos)) { $uncovered += $a }
}
Check ($uncovered.Count -eq 0) "proto/all app commands handled by PC" ($uncovered -join ",")
Check ($cmdActions.Count -ge 15) "proto/command surface size" ($cmdActions.Count.ToString() + ": " + ($cmdActions -join ","))
# relay-фото-ветка реально есть на ПК
Check ($csBot.Contains('"screenshot" or "shot"') -and $csBot.Contains('"cam" or "camera"')) "proto/pc photo branch for screenshot/cam"

# ── 8. Relay-вызовы с аргментами совпадают по смыслу ───────────────────
Check ($main.Contains('cmd("wol"') -and $csActions.Contains('"wol"')) "proto/wol wired"
Check ($main.Contains('cmd("unlock"') -and $csActions.Contains('"unlock"')) "proto/unlock wired"
Check ($main.Contains('relay.run("ls"') -and $csActions.Contains('"ls"')) "proto/ls relay fallback wired"
Check ($main.Contains('relay.run("status")') -and $csActions.Contains('"status"')) "proto/status relay fallback wired"

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL GREEN ($pass passed)"; exit 0 } else { Write-Host "$fail FAILURES ($pass passed)"; exit 1 }
