[CmdletBinding()]
param(
    [int[]]$WorkerCounts = @(1, 2, 4),
    [int[]]$Cpus = @(0, 2, 4, 6),
    [switch]$SkipBuild,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..\..')).Path
Push-Location $root
try {
    . .\activate.ps1
    if (-not $IsWindows) { throw 'Windows boundary checks require Windows.' }
    if (-not $SkipBuild) {
        dotnet build src\Servers\Kestrel\samples\NetworkProtoSample\NetworkProtoSample.csproj -c Release -v:q -p:UseIisNativeAssets=false
        if ($LASTEXITCODE -ne 0) { throw 'Sample build failed.' }
    }
    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path $root ("artifacts\windows-check-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    $sample = Join-Path $root 'artifacts\bin\NetworkProtoSample\Release\net11.0\NetworkProtoSample.dll'
    $dotnet = (Get-Command dotnet).Source
    foreach ($count in $WorkerCounts) {
        if ($count -le 0 -or $count -gt $Cpus.Count) { throw 'Worker count exceeds the supplied CPU list.' }
        foreach ($backend in @('iocp', 'rio')) {
            $info = [System.Diagnostics.ProcessStartInfo]::new($dotnet)
            $info.UseShellExecute = $false
            $info.RedirectStandardOutput = $true
            $info.RedirectStandardError = $true
            $info.WorkingDirectory = $root
            foreach ($argument in @($sample, '--backend', $backend, '--scheme', 'http', '--workers', [string]$count,
                '--port', '0', '--check-windows', 'true', '--Logging:LogLevel:Default', 'Warning')) {
                $info.ArgumentList.Add($argument)
            }
            $info.Environment['NETWORKPROTO_CPUS'] = $Cpus[0..($count - 1)] -join ','
            $info.Environment['NETWORKPROTO_WINDOWS_SNDBUF'] = '4096'
            $info.Environment['DOTNET_PROCESSOR_COUNT'] = [string]$count
            $process = [System.Diagnostics.Process]::Start($info)
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            try {
                if (-not $process.WaitForExit(90000)) { throw "$backend/$count boundary check timed out." }
                $text = $stdout.GetAwaiter().GetResult()
                $text | Set-Content (Join-Path $OutputDirectory "$backend-$count.log")
                $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $OutputDirectory "$backend-$count-error.log")
                if ($process.ExitCode -ne 0) { throw "$backend/$count boundary check failed." }
                $reports = @($text -split "`n" | Where-Object { $_.StartsWith('WINDOWS_TRANSPORT ', [StringComparison]::Ordinal) } |
                    ForEach-Object { $_.Substring('WINDOWS_TRANSPORT '.Length) | ConvertFrom-Json })
                if ($reports.Count -ne $count -or @($reports | Where-Object {
                    $_.pendingIo -ne 0 -or $_.connections -ne 0 -or $_.inputLeases -ne 0 -or $_.outputLeases -ne 0
                }).Count -ne 0) {
                    throw "$backend/$count did not drain native ownership."
                }
                if (($reports | Measure-Object backpressure -Sum).Sum -eq 0 -or ($reports | Measure-Object maxLeasedPages -Maximum).Maximum -ne 4) {
                    throw "$backend/$count did not reach the actual four-page receive pressure boundary."
                }
                if ($backend -eq 'rio' -and ($reports | Measure-Object rioNotifications -Sum).Sum -eq 0) {
                    throw 'The actual RIO producer path did not run.'
                }
                if (@($text -split "`n" | Where-Object { $_.StartsWith('PASS Windows ', [StringComparison]::Ordinal) }).Count -ne 3) {
                    throw "$backend/$count did not reach every required assertion."
                }
                Write-Host "PASS $backend workers=$count"
            }
            finally {
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
                $process.Dispose()
            }
        }
    }
    Write-Host "CHECKS $OutputDirectory"
}
finally {
    Pop-Location
}
