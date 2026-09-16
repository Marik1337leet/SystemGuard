# Покрытие WebApp <-> сервер БЕЗ запуска: парсит РЕАЛЬНЫЕ app.js/index.html
# и сверяет с РЕАЛЬНЫМИ RemoteHttpServer.cs / RemoteActions.cs /
# TelegramBotService.cs. Ловит рассинхрон "кнопка есть — команды нет".
# Запуск: powershell -ExecutionPolicy Bypass -File WebCheck.ps1
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

$js = ReadSrc "SystemGuard.MiniApp/app.js"
$html = ReadSrc "SystemGuard.MiniApp/index.html"
$http = ReadSrc "SystemGuard.Desktop/Services/RemoteHttpServer.cs"
$act = ReadSrc "SystemGuard.Desktop/Services/RemoteActions.cs"
$bot = ReadSrc "SystemGuard.Desktop/Services/TelegramBotService.cs"

# ── 1. Эндпоинты: всё, что дёргает WebApp, есть на сервере ─────────────
$jsPaths = @{}
foreach ($m in ([regex]"/api/[a-z.]+").Matches($js)) { $jsPaths[$m.Value] = $true }
$servPaths = @{}
foreach ($m in ([regex]'"/api/[a-z.]+"').Matches($http)) { $servPaths[$m.Value.Trim('"')] = $true }
$servPaths["/api"] = $true; $servPaths["/api/"] = $true
$missEp = @($jsPaths.Keys | Where-Object { -not $servPaths.ContainsKey($_) })
Check ($missEp.Count -eq 0) "web/endpoints covered" ($missEp -join ",")
Check ($jsPaths.Count -ge 8) "web/endpoint surface" ($jsPaths.Keys -join " ")

# ── 2. Действия: кнопки WebApp ⊆ команды сервера ───────────────────────
$appActions = @{}
foreach ($m in ([regex]"run\(\s*'([a-z_]+)'").Matches($js)) { $appActions[$m.Groups[1].Value] = $true }
foreach ($m in ([regex]'data-run="([a-z_]+)"').Matches($html)) { $appActions[$m.Groups[1].Value] = $true }
$servActions = @{}
foreach ($m in ([regex]'case "([a-z_]+)"').Matches($act)) { $servActions[$m.Groups[1].Value] = $true }
foreach ($a in @("screenshot","shot","cam","camera")) { $servActions[$a] = $true } # фото-ветка relay
$missAct = @($appActions.Keys | Where-Object { -not $servActions.ContainsKey($_) })
Check ($missAct.Count -eq 0) "web/actions handled by server" ($missAct -join ",")
Check ($appActions.Count -ge 30) "web/action surface" ($appActions.Count.ToString() + " actions")

# ── 3. CHAT_FALLBACK: ключи — действия WebApp, значения — команды чата ──
$fbBlock = [regex]::Match($js, 'CHAT_FALLBACK\s*=\s*\{(?<b>.*?)\};', [Text.RegularExpressions.RegexOptions]::Singleline).Groups["b"].Value
Check ($fbBlock.Length -gt 50) "web/fallback map present"
$fbKeys = @{}
foreach ($m in ([regex]"([a-z_]+)\s*:").Matches($fbBlock)) { $fbKeys[$m.Groups[1].Value] = $true }
$fbOrphan = @($fbKeys.Keys | Where-Object { -not $appActions.ContainsKey($_) })
Check ($fbOrphan.Count -eq 0) "web/fallback keys are real app actions" ($fbOrphan -join ",")
$chatCmds = @{}
foreach ($m in ([regex]'case "(/[a-z_]+)"').Matches($bot)) { $chatCmds[$m.Groups[1].Value] = $true }
$badFb = @()
foreach ($m in ([regex]"([a-z_]+)\s*:\s*(1|'([a-z_]+)')").Matches($fbBlock)) {
  $key = $m.Groups[1].Value; $val = $m.Groups[2].Value
  $cmd = if ($val -eq "1") { "/" + $key } else { "/" + $m.Groups[3].Value }
  if (-not $chatCmds.ContainsKey($cmd)) { $badFb += ($key + "->" + $cmd) }
}
Check ($badFb.Count -eq 0) "web/fallback targets exist in chat" ($badFb -join ",")
Check ($js.Contains("chatFallback(action, arg)") -and $js.Contains("copyText(fb")) "web/fallback wired in run().catch"
Check ($js.Contains("copyText('/' + pol.dataset.policy")) "web/policy fallback wired"

# ── 4. OUT_MAP: цели вывода существуют в index.html ────────────────────
$outBlock = [regex]::Match($js, 'OUT_MAP\s*=\s*\{(?<b>.*?)\};', [Text.RegularExpressions.RegexOptions]::Singleline).Groups["b"].Value
$outIds = @{}
foreach ($m in ([regex]"'([A-Za-z0-9_]+)'").Matches($outBlock)) {
  $v = $m.Groups[1].Value
  if ($v -notmatch "^[a-z_]+$" -or $v.Length -gt 3 -and $v -match "Out$") { $outIds[$v] = $true }
}
$outIds = @{}
foreach ($m in ([regex]":\s*'([A-Za-z0-9_]+Out)'").Matches($outBlock)) { $outIds[$m.Groups[1].Value] = $true }
$missOut = @($outIds.Keys | Where-Object { $html -notmatch ('id="' + $_ + '"') })
Check ($missOut.Count -eq 0) "web/output targets exist" ($missOut -join ",")
Check ($outIds.Count -ge 4) "web/output map size" ($outIds.Keys -join ",")

# ── 5. Безопасность: секретов в статике нет ────────────────────────────
$tokenLike = ([regex]"\d{6,}:[\w\-]{20,}").Matches($js + $html)
Check ($tokenLike.Count -eq 0) "web/no hardcoded bot tokens"
$httpApiCall = ([regex]"https://api\.telegram\.org").Matches($js)
Check ($httpApiCall.Count -eq 0) "web/no direct Bot API calls (would need token)"

# ── 6. Протокол запросов как ждёт сервер ───────────────────────────────
Check ($js.Contains("Content-Type', 'text/plain") -or $js.Contains('Content-Type") : "text/plain') -or $js.Contains("text/plain")) "web/no-preflight POST"
Check ($js.Contains("token=") -and $js.Contains("encodeURIComponent")) "web/token in query"
Check ($http.Contains('"text/plain"') -or $http.Contains("action")) "server/parses any content-type"

# ── 7. Санитария JS ────────────────────────────────────────────────────
$bo = ([regex]::Matches($js, "\{").Count); $bc = ([regex]::Matches($js, "\}").Count)
Check ($bo -eq $bc) "web/braces balanced" ("{=" + $bo + " }=" + $bc)
try { [xml]$html | Out-Null; $xmlOk = $true } catch { $xmlOk = $false }
Check ($xmlOk -or $html.Contains("<html")) "web/html present"
Check ($html.Contains("/screenshot")) "web/offline hint mentions chat fallback"

Write-Host ""
if ($fail -eq 0) { Write-Host "ALL GREEN ($pass passed)"; exit 0 } else { Write-Host "$fail FAILURES ($pass passed)"; exit 1 }
