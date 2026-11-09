param(
    [ValidateSet('Prepare', 'Initialize', 'Inspect', 'Switch', 'Close', 'Cleanup')][string]$Action = 'Prepare',
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-fA-F0-9]+$')][string]$InstanceId
)
$ErrorActionPreference = 'Stop'
trap { Write-Output $_.ScriptStackTrace; throw $_ }
$interop = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.visualstudio.interop\17.14.40260\lib\net20\Microsoft.VisualStudio.Interop.dll'
[void][Reflection.Assembly]::LoadFrom($interop)
$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
$manifestPath = Join-Path $artifacts 'context-fixtures.json'
Add-Type -ReferencedAssemblies $interop -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
public static class LumenExperimentalAutomation
{
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    public static object Find(int processId)
    {
        IRunningObjectTable table; Marshal.ThrowExceptionForHR(GetRunningObjectTable(0, out table));
        IBindCtx context; Marshal.ThrowExceptionForHR(CreateBindCtx(0, out context));
        IEnumMoniker entries; table.EnumRunning(out entries);
        var item = new IMoniker[1];
        while (entries.Next(1, item, IntPtr.Zero) == 0)
        {
            string name; item[0].GetDisplayName(context, null, out name);
            if (name.StartsWith("!VisualStudio.DTE.18.0:", StringComparison.Ordinal) && name.EndsWith(":" + processId, StringComparison.Ordinal))
            {
                object result; table.GetObject(item[0], out result); return result;
            }
        }
        throw new InvalidOperationException("Experimental DTE was not registered in the ROT.");
    }
    public static void Output(object automation, string marker)
    {
        var dte = (EnvDTE80.DTE2)automation;
        var pane = dte.ToolWindows.OutputWindow.OutputWindowPanes.Add("Lumen context fixture");
        pane.OutputString(marker + "-output\r\n");
        pane.Activate();
    }
}
'@

function Get-ExperimentalDte {
    $processes = @(Get-CimInstance Win32_Process -Filter "Name='devenv.exe'" | Where-Object {
        $_.CommandLine -match '/RootSuffix\s+Exp(\s|$)' -and
        $_.ExecutablePath -eq 'C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\IDE\devenv.exe'
    })
    if ($processes.Count -ne 1) { throw 'Expected exactly one experimental process.' }
    if ($InstanceId -ne '25e762c6') { throw 'This fixture targets only the verified experimental instance.' }
    return [LumenExperimentalAutomation]::Find([int]$processes[0].ProcessId)
}

if ($Action -eq 'Inspect') {
    $dte = Get-ExperimentalDte
    Write-Output ('Solution: ' + $dte.Solution.FullName)
    Write-Output ('Active document: ' + $dte.ActiveDocument.FullName)
    foreach ($document in $dte.Documents) {
        Write-Output ('Document: ' + $document.FullName + '; selection: ' + ($null -ne $document.Selection))
    }
    return
}

if ($Action -eq 'Prepare') {
    if (Test-Path -LiteralPath $manifestPath) { throw 'Clean up the previous owned context fixture first.' }
    $dte = Get-ExperimentalDte
    if ($dte.Solution.FullName) { throw 'Refusing to replace a solution already open in the experimental instance.' }
    $root = Join-Path $artifacts ('context-' + [Guid]::NewGuid().ToString('D'))
    $manifest = @{ root = $root; configurationId = $null; conversationIds = @(); marker = 'context-' + [Guid]::NewGuid().ToString('D') }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $projectId = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $solution = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 18
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "ContextFixture", "ContextFixture.csproj", "$projectId"
EndProject
Global
EndGlobal
"@
    $project = @"
<Project ToolsVersion="15.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
  <PropertyGroup><ProjectGuid>$projectId</ProjectGuid><OutputType>Library</OutputType><RootNamespace>ContextFixture</RootNamespace><AssemblyName>ContextFixture</AssemblyName><TargetFrameworkVersion>v4.8</TargetFrameworkVersion></PropertyGroup>
  <ItemGroup><Reference Include="System" /><Compile Include="Sample.cs" /></ItemGroup>
  <Import Project="`$(MSBuildToolsPath)\Microsoft.CSharp.targets" />
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $root 'ContextFixture.sln'), $solution)
    [IO.File]::WriteAllText((Join-Path $root 'ContextFixture.csproj'), $project)
    [IO.File]::WriteAllText((Join-Path $root 'Sample.cs'), "public class ContextFixture {`r`n    public const string Value = `"disk-marker`";`r`n}`r`n")
    [IO.File]::WriteAllText((Join-Path $root 'Oversized.txt'), ('x' * 65537))
    [IO.File]::WriteAllBytes((Join-Path $root 'Binary.bin'), [byte[]](0, 1, 2, 255))
    $dte.Solution.Open((Join-Path $root 'ContextFixture.sln'))
    $Action = 'Initialize'
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$root = [IO.Path]::GetFullPath($manifest.root)
if (-not $root.StartsWith($artifacts + '\context-', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture root is not owned by this verifier.' }
if ($Action -eq 'Initialize') {
    $dte = Get-ExperimentalDte
    if (-not $dte.Solution.FullName) { $dte.Solution.Open((Join-Path $root 'ContextFixture.sln')) }
    if ([IO.Path]::GetFullPath($dte.Solution.FullName) -ne (Join-Path $root 'ContextFixture.sln')) { throw 'Refusing to replace an unrelated open solution.' }
    $source = Join-Path $root 'Sample.cs'
    $window = $dte.ItemOperations.OpenFile($source)
    $window.Activate()
    $deadline = (Get-Date).AddSeconds(15)
    do {
        $document = $dte.ActiveDocument
        $selection = if ($document -and $document.FullName -eq $source) { $document.Selection } else { $null }
        if ($selection) { break }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)
    if (-not $selection) { throw 'Owned editor selection was not available.' }
    $selection.SelectAll()
    $selection.Insert("public class ContextFixture {`r`n    public const string Value = `"$($manifest.marker)-unsaved`";`r`n}`r`n")
    $selection.MoveToLineAndOffset(2, 1, $false)
    $selection.EndOfLine($true)
    [LumenExperimentalAutomation]::Output($dte, $manifest.marker)
    $window.Activate()
    Write-Output 'Owned solution loaded; unsaved editor selection and synthetic Output ready.'
    return
}
if ($Action -eq 'Switch' -or $Action -eq 'Close') {
    $dte = Get-ExperimentalDte
    if ($dte.Solution.FullName -and [IO.Path]::GetFullPath($dte.Solution.FullName) -ne (Join-Path $root 'ContextFixture.sln')) { throw 'Refusing to close an unrelated solution.' }
    foreach ($document in @($dte.Documents)) {
        if ($document.FullName -and [IO.Path]::GetFullPath($document.FullName).StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { $document.Close(2) }
    }
    $dte.Solution.Close($false)
    Write-Output 'Owned solution closed without saving the unsaved fixture buffer.'
    return
}
Remove-Item -LiteralPath $root -Recurse -Force
Remove-Item -LiteralPath $manifestPath
Write-Output 'Owned context fixture directory and manifest removed.'
