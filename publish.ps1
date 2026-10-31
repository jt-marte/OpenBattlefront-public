<#
Builds the launcher as one exe in dist\.

  .\publish.ps1          self-contained: runs on any 64-bit Windows 10/11, no .NET needed (about 60 MB)
  .\publish.ps1 -Small   framework-dependent: a few hundred KB, needs the .NET 8 Desktop Runtime
                         (Windows offers to download it on first start if it is missing)

  .\publish.ps1 -Content [<AddOn dir>]
                         also builds dist\OpenBattlefront-<version>.zip: the exe and the included
                         content (the Conversion Pack's BF1 and the Xbox DLC's DLC_XBOX, nothing else)
                         under Content\, with Content\content.json. The AddOn dir defaults to the
                         launcher's own game copy. Combine with -Small as needed.
#>
param(
    [switch]$Small,
    [switch]$Content,
    [Parameter(Position = 0)][string]$AddOn = (Join-Path $env:LOCALAPPDATA 'OpenBattlefront\Game\AddOn')
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'dist'
$project = Join-Path $root 'src\OpenBattlefront\OpenBattlefront.csproj'
if ($PSBoundParameters.ContainsKey('AddOn')) { $Content = $true }
if ($Content -and -not (Test-Path -LiteralPath $AddOn -PathType Container)) { throw "No AddOn folder at $AddOn." }

$publishArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64',
    '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-o', $out, '--nologo')
if ($Small) { $publishArgs += '--self-contained', 'false' }
else { $publishArgs += '--self-contained', 'true', '-p:EnableCompressionInSingleFile=true' }

& dotnet @publishArgs
if ($LASTEXITCODE) { throw 'dotnet publish failed.' }
$exe = Get-Item (Join-Path $out 'OpenBattlefront.exe')
Write-Host ("{0}  {1:N1} MB" -f $exe.FullName, ($exe.Length / 1MB))

if ($Content) {
    $version = (Select-Xml -Path $project -XPath '/Project/PropertyGroup/Version').Node.InnerText
    $zip = Join-Path $out "OpenBattlefront-$version.zip"
    # '--' quoted: Windows PowerShell drops a bare -- instead of passing it to dotnet. (dotnet run
    # has no --nologo; it would hand it to the tool.)
    & dotnet run --project (Join-Path $root 'tools\OpenBattlefront.Pack\OpenBattlefront.Pack.csproj') -c Release '--' $exe.FullName $AddOn $zip
    if ($LASTEXITCODE) { throw 'Packing the included content failed.' }
    $package = Get-Item $zip
    Write-Host ("{0}  {1:N1} MB" -f $package.FullName, ($package.Length / 1MB))
}
