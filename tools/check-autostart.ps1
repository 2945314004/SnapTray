# 开机自启"黑框"排查工具
#
# 列出本机所有自启入口（HKCU/HKLM Run、用户/公共启动文件夹），
# 读出每个程序的 PE 子系统：GUI 的启动时不会有窗口；CONSOLE 的才会闪黑框。
#
# 用法（不需要管理员）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check-autostart.ps1

function Get-PeSubsystem([string]$Path) {
    try {
        if (-not (Test-Path -LiteralPath $Path)) { return '路径不存在' }
        $stream = [System.IO.File]::OpenRead($Path)
        $buffer = New-Object byte[] 4096
        $null = $stream.Read($buffer, 0, 4096)
        $stream.Close()
        if ($buffer[0] -ne 0x4D -or $buffer[1] -ne 0x5A) { return '非 PE（脚本/快捷方式）' }
        $lfanew = [BitConverter]::ToInt32($buffer, 0x3C)
        if ($lfanew -le 0 -or $lfanew + 0x60 -gt 4096) { return 'PE 头越界' }
        $subsystem = [BitConverter]::ToUInt16($buffer, $lfanew + 24 + 68)
        switch ($subsystem) {
            2 { 'GUI（静默）' }
            3 { 'CONSOLE（会闪黑框）' }
            default { "subsystem=$subsystem" }
        }
    } catch {
        "读取失败：$($_.Exception.Message)"
    }
}

function Get-ExeFromCommand([string]$Command) {
    if ([string]::IsNullOrWhiteSpace($Command)) { return '' }
    $text = $Command.Trim()
    if ($text.StartsWith('"')) { return $text.Split('"')[1] }
    return ($text -split ' ')[0]
}

$rows = New-Object System.Collections.Generic.List[object]

$runKeys = @(
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run'
)
foreach ($key in $runKeys) {
    if (-not (Test-Path $key)) { continue }
    $props = (Get-ItemProperty -Path $key).PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' }
    foreach ($prop in $props) {
        $exe = Get-ExeFromCommand ([string]$prop.Value)
        $rows.Add([pscustomobject]@{
            来源   = $key.Replace(':\Software\Microsoft\Windows\CurrentVersion\Run', '').Replace(':\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run', ' (32位)')
            名称   = $prop.Name
            类型   = Get-PeSubsystem $exe
            目标   = $exe
        })
    }
}

$startupDirs = @(
    "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup",
    "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\Startup"
)
foreach ($dir in $startupDirs) {
    if (-not (Test-Path $dir)) { continue }
    foreach ($item in (Get-ChildItem -LiteralPath $dir -File)) {
        $type = if ($item.Extension -ieq '.lnk') { '快捷方式（需逐个看目标）' } else { Get-PeSubsystem $item.FullName }
        $rows.Add([pscustomobject]@{
            来源 = '启动文件夹'
            名称 = $item.Name
            类型 = $type
            目标 = $item.FullName
        })
    }
}

$rows | Sort-Object 类型 -Descending | Format-Table -AutoSize -Wrap | Out-String -Width 200

$suspects = $rows | Where-Object { $_.类型 -like '*CONSOLE*' }
if (@($suspects).Count -eq 0) {
    '结论：没有发现控制台子系统的自启程序 —— 黑框更可能来自某个自启程序自己拉起的命令行子进程。'
} else {
    "结论：以下 $($suspects.Count) 项是控制台程序，登录时会闪黑框："
    $suspects | ForEach-Object { "  - $($_.名称) → $($_.目标)" }
}
