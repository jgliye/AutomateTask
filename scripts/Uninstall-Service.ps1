$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请右键卸载服务.cmd，以管理员身份运行' }
$service = Get-Service -Name AutomateTask -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name AutomateTask
        $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))
    }
    & sc.exe delete AutomateTask
    if ($LASTEXITCODE -ne 0) { throw '卸载服务失败' }
}
if (Get-ScheduledTask -TaskName 'AutomateTask.Desktop' -TaskPath '\' -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName 'AutomateTask.Desktop' -TaskPath '\' -Confirm:$false
}
Write-Host '服务和桌面端交互计划任务已卸载；桌面连接断开后自行退出，保留当前数据库和日志'
