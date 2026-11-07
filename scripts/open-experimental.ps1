param(
    [ValidateSet('Open', 'Close')][string]$Action = 'Open',
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]+$')][string]$InstanceId,
    [int]$Port = 9223
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instance = @(& $vswhere -prerelease -all -format json | ConvertFrom-Json |
    Where-Object { $_.instanceId -eq $InstanceId })
if ($instance.Count -ne 1) { throw 'Expected exactly one matching Visual Studio instance.' }
$devenv = Join-Path $instance[0].installationPath 'Common7\IDE\devenv.exe'
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
[IO.Directory]::CreateDirectory($artifacts) | Out-Null

function Get-ExperimentalProcess {
    Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object {
        $_.ExecutablePath -eq $devenv -and $_.CommandLine -match '/RootSuffix\s+Exp(\s|$)'
    }
}

function Find-Element($scope, $condition, $timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    do {
        $found = $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($found) { return $found }
        Start-Sleep -Milliseconds 400
    } while ((Get-Date) -lt $deadline)
    return $null
}

if ($Action -eq 'Close') {
    foreach ($process in @(Get-ExperimentalProcess)) {
        $window = Find-Element ([System.Windows.Automation.AutomationElement]::RootElement) `
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$process.ProcessId)) 5
        if ($window) {
            $pattern = $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
            $pattern.Close()
        } else {
            Stop-Process -Id $process.ProcessId
        }
    }
    $deadline = (Get-Date).AddSeconds(60)
    while (@(Get-ExperimentalProcess).Count -gt 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    $leftover = @(Get-ExperimentalProcess)
    if ($leftover.Count -gt 0) { throw "Experimental Visual Studio is still running: $($leftover.ProcessId -join ', ')" }
    Write-Output 'Experimental Visual Studio closed.'
    return
}

if (@(Get-ExperimentalProcess).Count -gt 0) { throw 'An experimental Visual Studio is already running.' }

$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=$Port --remote-debugging-address=127.0.0.1"
$activity = Join-Path $artifacts 'vs-activity.xml'
$started = Start-Process -FilePath $devenv -ArgumentList ('/RootSuffix Exp /Log "{0}"' -f $activity) -PassThru
try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $started.Id)

    $skip = Find-Element $root (New-Object System.Windows.Automation.AndCondition(
        $nameCondition,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, 'Continuar sem código')))) 180
    if ($skip) {
        $skip.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output 'Start page dismissed.'
    }

    $view = Find-Element $root (New-Object System.Windows.Automation.AndCondition(
        $nameCondition,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::MenuItem)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'Exibir')))))) 180
    if (-not $view) { throw 'The Exibir menu was not found in the experimental instance.' }
    $expand = $view.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()

    $lumen = Find-Element $view (New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Lumen')) 60
    if (-not $lumen) {
        $expand.Collapse()
        throw 'The Lumen command is missing from the Exibir menu of the experimental instance.'
    }
    $lumen.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output 'Lumen command invoked.'

    $panel = Find-Element $root (New-Object System.Windows.Automation.AndCondition(
        $nameCondition,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                [System.Windows.Automation.ControlType]::Pane)),
            (New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, 'Lumen')))))) 120
    if (-not $panel) { throw 'The Lumen tool window did not open.' }
    Write-Output ("Lumen tool window opened; WebView2 debugging on port {0}." -f $Port)
} catch {
    Stop-Process -Id $started.Id -ErrorAction SilentlyContinue
    throw
}
