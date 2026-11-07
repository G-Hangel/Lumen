param(
    [ValidateSet('Install', 'Uninstall', 'Verify')][string]$Action = 'Install',
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]+$')][string]$InstanceId,
    [string]$Vsix
)
$ErrorActionPreference = 'Stop'
if (-not $Vsix) { $Vsix = Join-Path $PSScriptRoot '..\src\Lumen.Vsix\bin\Release\net48\Lumen.Vsix.vsix' }
$extensionId = 'Lumen.37b13e92-66f4-4ebd-8f31-4fe1a889f684'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$instances = & $vswhere -prerelease -all -format json | ConvertFrom-Json
$instance = @($instances | Where-Object { $_.instanceId -eq $InstanceId })
if ($instance.Count -ne 1 -or ([version]$instance[0].installationVersion).Major -ne 18) {
    throw 'Expected exactly one Visual Studio 18 instance.'
}
$installer = Join-Path $instance[0].installationPath 'Common7\IDE\VSIXInstaller.exe'
$extensions = Join-Path $env:LOCALAPPDATA "Microsoft\VisualStudio\18.0_${InstanceId}Exp\Extensions"
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
[IO.Directory]::CreateDirectory($artifacts) | Out-Null

function Get-LumenInstall {
    if (Test-Path $extensions) {
        Get-ChildItem $extensions -Filter extension.vsixmanifest -Recurse | ForEach-Object {
            [xml]$manifest = [IO.File]::ReadAllText($_.FullName)
            if ($manifest.PackageManifest.Metadata.Identity.Id -eq $extensionId) { $_.DirectoryName }
        }
    }
}

function Invoke-Installer([string]$Operation) {
    $logName = 'lumen-' + [Guid]::NewGuid().ToString('N') + '.log'
    $arguments = '/q /rootSuffix:Exp /instanceIds:{0} /logFile:{1} {2}' -f $InstanceId, $logName, $Operation
    $process = Start-Process -FilePath $installer -ArgumentList $arguments -Wait -PassThru
    $log = Join-Path $env:TEMP $logName
    if (Test-Path $log) { Copy-Item $log (Join-Path $artifacts $logName) }
    Write-Output ("VSIXInstaller exit={0}; log={1}" -f $process.ExitCode, $logName)
    if ($process.ExitCode -ne 0) { throw "VSIXInstaller failed: $($process.ExitCode)" }
}

function Assert-InstalledPackage([string]$Package) {
    $installed = @(Get-LumenInstall)
    if ($installed.Count -ne 1) { throw 'Lumen was not found exactly once in the selected Exp hive.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Package)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        foreach ($entry in $zip.Entries) {
            if (-not $entry.Name -or $entry.FullName -eq '[Content_Types].xml') { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $installed[0] $entry.FullName))
            if (-not $target.StartsWith($installed[0] + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Package entry escapes the extension directory.'
            }
            if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Missing installed file: $($entry.FullName)" }
            $sourceStream = $entry.Open()
            $targetStream = [IO.File]::OpenRead($target)
            try {
                $expected = [BitConverter]::ToString($sha.ComputeHash($sourceStream))
                $actual = [BitConverter]::ToString($sha.ComputeHash($targetStream))
                if ($expected -ne $actual) { throw "Stale installed file: $($entry.FullName)" }
            } finally { $sourceStream.Dispose(); $targetStream.Dispose() }
        }
    } finally { $sha.Dispose(); $zip.Dispose() }
    Write-Output ("Verified every packaged file in " + $installed[0])
}

if ($Action -ne 'Verify') {
    $running = Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object {
        $_.ExecutablePath -eq (Join-Path $instance[0].installationPath 'Common7\IDE\devenv.exe') -and
        $_.CommandLine -match '/RootSuffix\s+Exp(\s|$)'
    }
    if ($running) { throw 'Close the selected experimental Visual Studio instance before deployment.' }
}
if ($Action -ne 'Uninstall') {
    $Vsix = (Resolve-Path -LiteralPath $Vsix).Path
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Vsix)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('extension.vsixmanifest').Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($manifest.PackageManifest.Metadata.Identity.Id -ne $extensionId) { throw 'Only the Lumen package can be deployed.' }
    } finally { $zip.Dispose() }
}
if ($Action -eq 'Install' -or $Action -eq 'Uninstall') {
    if (@(Get-LumenInstall).Count -gt 0) {
        Invoke-Installer ('/uninstall:' + $extensionId)
        if (@(Get-LumenInstall).Count -ne 0) { throw 'Uninstall did not remove Lumen from the Exp hive.' }
    }
}
if ($Action -eq 'Install') { Invoke-Installer ('"' + $Vsix + '"') }
if ($Action -ne 'Verify') {
    $devenv = Join-Path $instance[0].installationPath 'Common7\IDE\devenv.exe'
    $updateLog = Join-Path $artifacts 'vs-update.xml'
    $process = Start-Process -FilePath $devenv -ArgumentList ('/RootSuffix Exp /UpdateConfiguration /Log "{0}"' -f $updateLog) -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Experimental configuration update failed: $($process.ExitCode)" }
}
if ($Action -ne 'Uninstall') { Assert-InstalledPackage $Vsix }
