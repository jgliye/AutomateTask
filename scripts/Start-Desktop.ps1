param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'

# 只在登录或服务发起启动事件时检查一次，不常驻、不定时查询
$Root = (Resolve-Path -LiteralPath $Root).Path
$config = Get-Content -LiteralPath (Join-Path $Root 'appsettings.Production.json') -Raw | ConvertFrom-Json
$url = [Uri]$config.Urls
if (!$url.IsAbsoluteUri -or $url.Scheme -ne 'http' -or $url.Host -ne '127.0.0.1') {
    throw '请在 appsettings.Production.json 中配置有效的本机服务地址'
}
$overview = Invoke-RestMethod -Uri ($url.GetLeftPart([UriPartial]::Authority) + '/api/tasks') -TimeoutSec 15
$required = @($overview.tasks | Where-Object { $_.type -eq 2 -and $_.settings.enabled }).Count -gt 0
if (!$required) { exit 0 }

$desktop = Join-Path $Root 'Tray\AutomateTask.Desktop.exe'
if (!(Test-Path -LiteralPath $desktop)) { throw '发布包缺少桌面端程序' }
$process = Start-Process -FilePath $desktop -ArgumentList ('"' + $Root + '"') -WindowStyle Hidden -Wait -PassThru
exit $process.ExitCode
