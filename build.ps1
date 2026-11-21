# Build the AutoOvertake plugin (bootstrap compile via Roslyn, no .NET SDK required)
# Roslyn (Microsoft.CodeAnalysis) is taken from the ETS2LA install; only the .NET 10 runtime is needed.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$bootstrap = Join-Path $root '.bootstrap'
$etsDir = Join-Path $env:LOCALAPPDATA 'ETS2LA\current'

# 0) preflight checks
if (-not (Test-Path $etsDir)) { throw "ETS2LA install dir not found: $etsDir (install ETS2LA first)" }
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source }
if (-not $dotnet) { throw 'dotnet (.NET runtime) not found - install the .NET 10 Desktop Runtime first' }

$sharedRoot = Join-Path (Split-Path $dotnet) 'shared\Microsoft.NETCore.App'
$runtimeDir = Get-ChildItem $sharedRoot -Directory -ErrorAction SilentlyContinue |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
if (-not $runtimeDir) { throw 'Microsoft.NETCore.App runtime folder not found' }

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw '.NET Framework compiler csc.exe not found' }

# 1) bootstrap folder (Roslyn ships inside ETS2LA)
if (-not (Test-Path (Join-Path $bootstrap 'Microsoft.CodeAnalysis.dll'))) {
    New-Item -ItemType Directory -Force -Path $bootstrap | Out-Null
    Copy-Item "$etsDir\Microsoft.CodeAnalysis.dll","$etsDir\Microsoft.CodeAnalysis.CSharp.dll","$etsDir\System.Collections.Immutable.dll","$etsDir\System.Reflection.Metadata.dll","$etsDir\System.Memory.dll","$etsDir\System.Runtime.CompilerServices.Unsafe.dll","$etsDir\System.Text.Encoding.CodePages.dll" -Destination $bootstrap -Force
}

# 2) compile the bootstrap driver with the Framework C# 5 compiler (driver itself targets net10)
& $csc /noconfig /nostdlib /target:exe /langversion:5 /out:"$bootstrap\CompileDriver.exe" `
    "/r:$runtimeDir\System.Runtime.dll" "/r:$runtimeDir\System.Console.dll" "/r:$runtimeDir\System.Collections.dll" `
    "/r:$runtimeDir\System.IO.FileSystem.dll" "/r:$runtimeDir\System.Runtime.Extensions.dll" "/r:$runtimeDir\System.Private.CoreLib.dll" `
    "/r:$bootstrap\Microsoft.CodeAnalysis.dll" "/r:$bootstrap\Microsoft.CodeAnalysis.CSharp.dll" `
    "/r:$bootstrap\System.Collections.Immutable.dll" "/r:$bootstrap\System.Reflection.Metadata.dll" `
    "$bootstrap\CompileDriver.cs"
if ($LASTEXITCODE -ne 0) { throw 'driver compile failed' }

# 3) compile the plugin with the driver (net10 + Roslyn)
New-Item -ItemType Directory -Force -Path (Join-Path $root 'dist') | Out-Null
Push-Location $bootstrap
& $dotnet exec 'CompileDriver.exe' $root (Join-Path $root 'dist\AutoOvertake.dll') $etsDir $runtimeDir
$exit = $LASTEXITCODE
Pop-Location
if ($exit -ne 0) { throw 'plugin compile failed - see errors above' }
Write-Host "Output: $root\dist\AutoOvertake.dll"
