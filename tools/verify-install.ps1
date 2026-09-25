# Пост-инсталляционный smoke: запускает установленный exe, открывает настройки
# (Ctrl+Shift+S), переводит оверлей в режим скриншота (Ctrl+Shift+C, снимает
# WDA_EXCLUDEFROMCAPTURE) и сохраняет скрины для визуальной проверки.
$exe = "$env:LOCALAPPDATA\Programs\BrainstormBuddy\BrainstormBuddy.exe"
$outDir = "$PSScriptRoot\verify-shots"
New-Item -ItemType Directory -Force $outDir | Out-Null

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

function Shot($name) {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($b.Left, $b.Top, 0, 0, $bmp.Size)
    $bmp.Save("$outDir\$name.png")
    $g.Dispose(); $bmp.Dispose()
    Write-Host "shot: $name.png"
}

Write-Host "launch: $exe"
$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 15

# Режим скриншота: показать оверлей (иначе его нет на снимках)
[System.Windows.Forms.SendKeys]::SendWait("^+C")
Start-Sleep -Seconds 2
Shot "01-overlay"

# Настройки
[System.Windows.Forms.SendKeys]::SendWait("^+S")
Start-Sleep -Seconds 4
Shot "02-settings"

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Host "done"
