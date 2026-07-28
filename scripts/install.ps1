[CmdletBinding()]
param(
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\QuickHue\QuickHue.csproj'
$publishDirectory = Join-Path $repoRoot 'artifacts\publish\win-x64'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\QuickHue'
$installedExe = Join-Path $installDirectory 'QuickHue.exe'

# Stop every running QuickHue before building, whatever it was started from. A copy
# launched out of bin\Release holds the single-instance mutex, so a freshly installed
# one would exit on startup without a word, and it also locks the build output.
foreach ($process in @(Get-Process -Name 'QuickHue' -ErrorAction SilentlyContinue)) {
    $path = try { $process.Path } catch { '<unknown path>' }
    try {
        Stop-Process -InputObject $process -Force -ErrorAction Stop
        [void]$process.WaitForExit(5000)
        Write-Host "Stopped running QuickHue (PID $($process.Id)) from $path"
    }
    catch {
        Write-Warning "Could not stop QuickHue (PID $($process.Id)) from ${path}: $($_.Exception.Message)"
    }
}

dotnet publish $project -c Release -r win-x64 --self-contained false -o $publishDirectory

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDirectory 'QuickHue.exe') -Destination $installedExe -Force

Write-Host "Installed QuickHue to $installedExe"
if (-not $NoLaunch) {
    Start-Process -FilePath $installedExe
}
