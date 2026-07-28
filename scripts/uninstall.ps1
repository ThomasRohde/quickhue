[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$PurgeConfig
)

$ErrorActionPreference = 'Stop'
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\QuickHue'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if ($PSCmdlet.ShouldProcess('QuickHue', 'Stop application and remove startup registration')) {
    # Any running QuickHue, not just the installed copy: one started from a build folder
    # would otherwise keep its tray icon alive after everything else has been removed.
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
    Remove-ItemProperty -Path $runKey -Name 'QuickHue' -ErrorAction SilentlyContinue
}

if (Test-Path -LiteralPath $installDirectory) {
    $resolvedInstall = (Resolve-Path -LiteralPath $installDirectory).Path
    $expectedInstall = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\QuickHue'))
    if ($resolvedInstall -ne $expectedInstall) {
        throw "Refusing to remove unexpected path: $resolvedInstall"
    }
    if ($PSCmdlet.ShouldProcess($resolvedInstall, 'Remove installed application')) {
        Remove-Item -LiteralPath $resolvedInstall -Recurse -Force
    }
}

if ($PurgeConfig) {
    $configDirectory = Join-Path $env:LOCALAPPDATA 'QuickHue'
    if (Test-Path -LiteralPath $configDirectory) {
        $resolvedConfig = (Resolve-Path -LiteralPath $configDirectory).Path
        $expectedConfig = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'QuickHue'))
        if ($resolvedConfig -ne $expectedConfig) {
            throw "Refusing to remove unexpected path: $resolvedConfig"
        }
        if ($PSCmdlet.ShouldProcess($resolvedConfig, 'Remove QuickHue settings and encrypted key')) {
            Remove-Item -LiteralPath $resolvedConfig -Recurse -Force
        }
    }
}
