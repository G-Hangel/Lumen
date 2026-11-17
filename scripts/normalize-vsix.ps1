param([Parameter(Mandatory = $true)][string]$Vsix)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$source = [IO.Path]::GetFullPath($Vsix)
if (-not [IO.File]::Exists($source)) { throw "VSIX not found: $source" }
$temporary = $source + '.normalized'
$fixedExtensionDir = '"extensionDir":"[installdir]\\Common7\\IDE\\Extensions\\lumen"'

$inputZip = [IO.Compression.ZipFile]::OpenRead($source)
try {
  $outputZip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
  try {
    $fixed = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    foreach ($entry in $inputZip.Entries) {
      $input = $entry.Open()
      $buffer = New-Object System.IO.MemoryStream
      try { $input.CopyTo($buffer) } finally { $input.Dispose() }
      $bytes = $buffer.ToArray()
      $buffer.Dispose()
      if ($entry.FullName -in @('manifest.json', 'catalog.json')) {
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        $text = [regex]::Replace($text, '"extensionDir"\s*:\s*"[^"]+"', $fixedExtensionDir)
        $bytes = [Text.Encoding]::UTF8.GetBytes($text)
      }
      $copy = $outputZip.CreateEntry($entry.FullName, [IO.Compression.CompressionLevel]::Optimal)
      $copy.LastWriteTime = $fixed
      $output = $copy.Open()
      try { $output.Write($bytes, 0, $bytes.Length) } finally { $output.Dispose() }
    }
  } finally { $outputZip.Dispose() }
} finally { $inputZip.Dispose() }

[IO.File]::Delete($source)
[IO.File]::Move($temporary, $source)
