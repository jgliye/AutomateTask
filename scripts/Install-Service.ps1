$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$exe = Join-Path $root 'AutomateTask.exe'
if (!(Test-Path -LiteralPath $exe)) { throw '请从发布目录运行安装脚本' }
$tray = Join-Path $root 'Tray\AutomateTask.Desktop.exe'
if (!(Test-Path -LiteralPath $tray)) { throw '发布包缺少托盘程序' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '请右键安装服务.cmd，以管理员身份运行' }
if (Get-Service -Name 'AutomateTask' -ErrorAction SilentlyContinue) { throw '服务已存在，请先检查现有服务，不覆盖安装' }
& sc.exe create AutomateTask binPath= ('"' + $exe + '"') start= auto obj= 'NT SERVICE\AutomateTask' DisplayName= '自动化任务'
if ($LASTEXITCODE -ne 0) { throw '注册服务失败' }
& sc.exe sidtype AutomateTask unrestricted
if ($LASTEXITCODE -ne 0) { throw '服务 SID 设置失败；未启动服务' }
& (Join-Path $root 'Initialize-Data.ps1') -Root $root
& sc.exe failure AutomateTask reset= 86400 actions= restart/60000/restart/120000/restart/300000
if ($LASTEXITCODE -ne 0) { throw '失败恢复设置失败；未启动服务' }
& sc.exe description AutomateTask '本机管理页和工作日工时服务；仅保存每日汇报，不提交审批'
# 登录时检查一次运行需求；服务也可按需在指定的用户会话启动桌面端
$scheduler = $null
$folder = $null
$definition = $null
$trigger = $null
$action = $null
$registeredTask = $null

try {
    $scheduler = New-Object -ComObject 'Schedule.Service'
    $scheduler.Connect()
    $folder = $scheduler.GetFolder('\')
    $definition = $scheduler.NewTask(0)
    $definition.RegistrationInfo.Description = '自动化任务桌面提醒：存在已启用的通知任务时运行，全部停用后自行退出'
    $definition.Principal.GroupId = 'S-1-5-4'
    $definition.Principal.LogonType = 4
    $definition.Principal.RunLevel = 0
    $definition.Settings.AllowDemandStart = $true
    $definition.Settings.MultipleInstances = 0
    $definition.Settings.DisallowStartIfOnBatteries = $false
    $definition.Settings.StopIfGoingOnBatteries = $false
    $definition.Settings.ExecutionTimeLimit = 'PT0S'
    $definition.Settings.RestartCount = 3
    $definition.Settings.RestartInterval = 'PT1M'
    $trigger = $definition.Triggers.Create(9)
    $trigger.Delay = 'PT10S'
    $action = $definition.Actions.Create(0)
    $action.Path = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $action.Arguments = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $root 'Start-Desktop.ps1') + '" -Root "' + $root + '"'
    $action.WorkingDirectory = Split-Path -Parent $tray
    $serviceAccount = [Security.Principal.NTAccount]::new('NT SERVICE\AutomateTask')
    $serviceSid = $serviceAccount.Translate([Security.Principal.SecurityIdentifier]).Value
    $taskSecurity = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;IU)(A;;GRGX;;;$serviceSid)"
    # TASK_CREATE | TASK_DONT_ADD_PRINCIPAL_ACE：只授予交互用户和服务读取、运行权限
    $registeredTask = $folder.RegisterTaskDefinition('AutomateTask.Desktop', $definition, 18, 'S-1-5-4', $null, 4, $taskSecurity)
} finally {
    foreach ($value in @($registeredTask, $action, $trigger, $definition, $folder, $scheduler)) {
        if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($value)
        }
    }
}

Start-Service -Name AutomateTask
Write-Host '服务已设置为开机自启；桌面端通过交互计划任务按需启动，无通知任务时不常驻'
Write-Host '全新工时配置默认关闭自动填报；请在管理页面登录并配置任务'
