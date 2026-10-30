<#
Builds the launcher as one exe in dist\.

  .\publish.ps1          self-contained: runs on any 64-bit Windows 10/11, no .NET needed (about 60 MB)
  .\publish.ps1 -Small   framework-dependent: a few hundred KB, needs the .NET 8 Desktop Runtime
                         (Windows offers to download it on first start if it is missing)
#>
param([switch]$Small)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist'

$publishArgs = @('publish', (Join-Path $root 'src\OpenBattlefront\OpenBattlefront.csproj'), '-c', 'Release', '-r', 'win-x64',
    '-p:PublishSingleFile=true', '-p:DebugType=none', '-o', $out, '--nologo')
if ($Small) { $publishArgs += '--self-contained', 'false' }
else { $publishArgs += '--self-contained', 'true', '-p:EnableCompressionInSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true' }

& dotnet @publishArgs
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
$exe = Get-Item (Join-Path $out 'OpenBattlefront.exe')
Write-Host ("{0}  {1:N1} MB" -f $exe.FullName, ($exe.Length / 1MB))
