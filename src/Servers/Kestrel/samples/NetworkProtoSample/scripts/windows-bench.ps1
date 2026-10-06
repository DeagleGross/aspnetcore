# Requires PowerShell 7 on Windows.
[CmdletBinding()]
param(
    [string[]]$Backends = @('sockets', 'iocp', 'rio'),
    [string[]]$Schemes = @('http', 'https'),
    [string[]]$Modes = @('short', 'long'),
    [int]$Repetitions = 3,
    [int]$Seconds = 15,
    [int]$Warmup = 5,
    [int]$Connections = 256,
    [int]$ShortConnections = 64,
    [int]$ShortCooldownSeconds = 60,
    [int]$BasePort = 16000,
    [ValidateSet('Tls12', 'Tls13')]
    [string]$TlsProtocol = 'Tls13',
    [int[]]$ServerCpus = @(0, 2, 4, 6),
    [int[]]$ClientCpus = @(16, 18, 20, 22, 24, 26, 28, 30),
    [switch]$SkipBuild,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..\..\..')).Path
Push-Location $root
try {
    . .\activate.ps1
    if (-not $IsWindows) {
        throw 'This benchmark requires Windows.'
    }
    if ($Repetitions -le 0 -or $Seconds -le 0 -or $Connections -le 0 -or $ShortConnections -le 0 -or $Warmup -lt 0 -or $ShortCooldownSeconds -lt 0) {
        throw 'Repetitions, seconds and connections must be positive; warmup must be nonnegative.'
    }
    $caseCount = $Repetitions * $Backends.Count * $Schemes.Count * $Modes.Count
    if ($caseCount -le 0 -or $caseCount -gt 254 -or $BasePort -lt 1024 -or $BasePort + $caseCount -gt 65535) {
        throw 'The case count must be 1..254 and every selected listening port must be in 1024..65535.'
    }
    if (@($ServerCpus | Where-Object { $ClientCpus -contains $_ }).Count -ne 0) {
        throw 'Server and client CPU lists must be disjoint.'
    }
    if (-not $SkipBuild) {
        dotnet build src\Servers\Kestrel\samples\NetworkProtoSample\NetworkProtoSample.csproj -c Release -v:q -p:UseIisNativeAssets=false
        if ($LASTEXITCODE -ne 0) { throw 'Sample build failed.' }
        dotnet build src\Servers\Kestrel\testassets\WindowsTransportLoad\WindowsTransportLoad.csproj -c Release -v:q -p:UseIisNativeAssets=false
        if ($LASTEXITCODE -ne 0) { throw 'Load generator build failed.' }
    }
    $dotnet = (Get-Command dotnet).Source
    $sample = Join-Path $root 'artifacts\bin\NetworkProtoSample\Release\net11.0\NetworkProtoSample.dll'
    $load = Join-Path $root 'artifacts\bin\WindowsTransportLoad\Release\net11.0\WindowsTransportLoad.dll'
    $topology = (& $dotnet $load --topology) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Topology probe failed.' }
    $used = @($ServerCpus) + @($ClientCpus)
    if (@($used | Select-Object -Unique).Count -ne $used.Count) {
        throw 'CPU lists must not contain duplicates.'
    }
    foreach ($cpu in $used) {
        if ($cpu -lt 0 -or $cpu -ge 64 -or @($topology.cores.logicalCpus) -notcontains $cpu) {
            throw "CPU $cpu is not in the supported processor group."
        }
    }
    foreach ($core in $topology.cores) {
        if (@($core.logicalCpus | Where-Object { $used -contains $_ }).Count -gt 1) {
            throw 'Choose only one logical processor from each physical core, without SMT sharing.'
        }
    }
    if (-not $OutputDirectory) {
        $OutputDirectory = Join-Path $root ("artifacts\windows-transports-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    }
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    $topology | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $OutputDirectory 'topology.json')
    $certificate = Join-Path $OutputDirectory 'certificate.pem'
    $key = Join-Path $OutputDirectory 'key.pem'
    if ($Schemes -contains 'https') {
        & openssl req -x509 -newkey rsa:2048 -nodes -sha256 -days 2 -subj '/CN=localhost' -keyout $key -out $certificate 2>$null
        if ($LASTEXITCODE -ne 0) { throw 'Benchmark certificate generation failed.' }
    }

    function Get-Mask([int[]]$Cpus) {
        [long]$mask = 0
        foreach ($cpu in $Cpus) { $mask = $mask -bor ([long]1 -shl $cpu) }
        return [IntPtr]$mask
    }

    function Start-Child([string[]]$Arguments, [int[]]$Cpus, [bool]$Server) {
        $info = [System.Diagnostics.ProcessStartInfo]::new($dotnet)
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.WorkingDirectory = $root
        foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
        $info.Environment['DOTNET_PROCESSOR_COUNT'] = [string]$Cpus.Count
        foreach ($name in @('NETWORKPROTO_CPUS', 'NETWORKPROTO_WINDOWS_SNDBUF', 'NETWORKPROTO_IOCP_ZERO_BYTE',
            'DOTNET_SYSTEM_NET_SOCKETS_INLINE_COMPLETIONS', 'DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT', 'NETWORKPROTO_KTLS')) {
            $null = $info.Environment.Remove($name)
        }
        if ($Server) { $info.Environment['NETWORKPROTO_CPUS'] = $ServerCpus -join ',' }
        $process = [System.Diagnostics.Process]::Start($info)
        $process.ProcessorAffinity = Get-Mask $Cpus
        return $process
    }

    function Get-Metrics([string]$BaseUrl) {
        $json = & curl.exe --silent --show-error --fail --noproxy '*' --max-time 5 --insecure "$BaseUrl/metrics" 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw [System.Net.Http.HttpRequestException]::new("Metrics request failed with curl exit code $LASTEXITCODE.")
        }
        return $json | ConvertFrom-Json
    }

    $runs = [System.Collections.Generic.List[object]]::new()
    $sourceOctet = [System.Random]::Shared.Next(1, 255)
    $caseNumber = 0
    for ($repeat = 0; $repeat -lt $Repetitions; $repeat++) {
        foreach ($scheme in $Schemes) {
            foreach ($mode in $Modes) {
                for ($position = 0; $position -lt $Backends.Count; $position++) {
                    $backend = $Backends[($position + $repeat) % $Backends.Count]
                    $caseNumber++
                    $port = $BasePort + $caseNumber
                    $baseUrl = "${scheme}://127.0.0.1:$port"
                    $label = "$caseNumber-$backend-$scheme-$mode"
                    $caseConnections = if ($mode -eq 'short') { $ShortConnections } else { $Connections }
                    $sourceNetwork = "127.$sourceOctet.$caseNumber.0"
                    $server = $null
                    $client = $null
                    $serverOutput = $null
                    $serverError = $null
                    try {
                        $serverArgs = @($sample, '--backend', $backend, '--scheme', $scheme, '--port', [string]$port,
                            '--workers', [string]$ServerCpus.Count, '--minimal', 'true', '--benchmark', 'true',
                            '--Logging:LogLevel:Default', 'Warning')
                        if ($scheme -eq 'https') { $serverArgs += @('--cert', $certificate, '--key', $key, '--tls-protocol', $TlsProtocol) }
                        $server = Start-Child $serverArgs $ServerCpus $true
                        $serverOutput = $server.StandardOutput.ReadToEndAsync()
                        $serverError = $server.StandardError.ReadToEndAsync()
                        $ready = $false
                        for ($attempt = 0; $attempt -lt 20; $attempt++) {
                            if ($server.HasExited) { throw "Server exited during startup: $($serverError.GetAwaiter().GetResult())" }
                            try {
                                $metrics = Get-Metrics $baseUrl
                                if ($metrics.backend -ne $backend -or $metrics.scheme -ne $scheme) {
                                    throw 'Backend/scheme mismatch in the live sample.'
                                }
                                $ready = $true
                                break
                            }
                            catch [Microsoft.PowerShell.Commands.HttpResponseException] {
                                Start-Sleep -Milliseconds 100
                            }
                            catch [System.Net.Http.HttpRequestException] {
                                Start-Sleep -Milliseconds 100
                            }
                        }
                        if (-not $ready) { throw 'Server never became ready.' }

                        $client = Start-Child @($load, '--url', "$baseUrl/", '--mode', $mode,
                            '--connections', [string]$caseConnections, '--seconds', [string]$Seconds, '--warmup', [string]$Warmup,
                            '--tls-protocol', $TlsProtocol, '--source-network', $sourceNetwork) $ClientCpus $false
                        $clientError = $client.StandardError.ReadToEndAsync()
                        $lines = [System.Collections.Generic.List[string]]::new()
                        $measurementStarted = $false
                        while (-not $measurementStarted) {
                            $line = $client.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds($Warmup + 30)).GetAwaiter().GetResult()
                            if ($null -eq $line) { throw "Load generator exited before measurement: $($clientError.GetAwaiter().GetResult())" }
                            $lines.Add($line)
                            $measurementStarted = $line -eq 'MEASURE_START'
                        }
                        $before = Get-Metrics $baseUrl
                        $remaining = $client.StandardOutput.ReadToEndAsync()
                        if (-not $client.WaitForExit(($Seconds + 30) * 1000)) { throw 'Load generator did not exit.' }
                        $clientText = ($lines -join "`n") + "`n" + $remaining.GetAwaiter().GetResult()
                        $clientText | Set-Content (Join-Path $OutputDirectory "$label-load.log")
                        $clientError.GetAwaiter().GetResult() | Set-Content (Join-Path $OutputDirectory "$label-load-error.log")
                        if ($client.ExitCode -ne 0) { throw 'Load generator failed.' }
                        $after = Get-Metrics $baseUrl
                        $resultLine = @($clientText -split "`n" | Where-Object { $_.StartsWith('LOAD_RESULT ', [StringComparison]::Ordinal) })[0]
                        $result = $resultLine.Substring('LOAD_RESULT '.Length) | ConvertFrom-Json
                        $null = & curl.exe --silent --show-error --fail --noproxy '*' --max-time 5 --insecure --request POST "$baseUrl/shutdown"
                        if ($LASTEXITCODE -ne 0) { throw 'Graceful shutdown request failed.' }
                        if (-not $server.WaitForExit(20000)) { throw 'Server shutdown did not drain.' }
                        $stdout = $serverOutput.GetAwaiter().GetResult()
                        $stderr = $serverError.GetAwaiter().GetResult()
                        $stdout | Set-Content (Join-Path $OutputDirectory "$label-server.log")
                        $stderr | Set-Content (Join-Path $OutputDirectory "$label-server-error.log")
                        if ($server.ExitCode -ne 0) { throw "Server failed: $stderr" }
                        if ($result.errors -ne 0 -or $result.warmupErrors -ne $result.startupResourceErrors -or $result.readyLanes -ne $caseConnections) {
                            throw "Load errors: $($result.firstError)"
                        }
                        if ($result.warmupErrors -ne 0) {
                            Write-Warning "Recovered startup load errors before all lanes passed the readiness barrier: $($result.warmupErrors). Details remain in $label-load.log."
                        }
                        $native = @($stdout -split "`n" | Where-Object { $_.StartsWith('WINDOWS_TRANSPORT ', [StringComparison]::Ordinal) } |
                            ForEach-Object { $_.Substring('WINDOWS_TRANSPORT '.Length) | ConvertFrom-Json })
                        if ($backend -ne 'sockets') {
                            if ($native.Count -ne $ServerCpus.Count -or @($native | Where-Object {
                                $_.backend -ne $backend -or $_.pendingIo -ne 0 -or $_.connections -ne 0 -or $_.inputLeases -ne 0 -or $_.outputLeases -ne 0
                            }).Count -ne 0) {
                                throw 'Native backend identity or ownership-drain verification failed.'
                            }
                            if (($native | Measure-Object receives -Sum).Sum -eq 0 -or ($native | Measure-Object sends -Sum).Sum -eq 0) {
                                throw 'The selected backend never performed native data I/O.'
                            }
                            if ($backend -eq 'rio' -and ($native | Measure-Object rioNotifications -Sum).Sum -eq 0) {
                                throw 'The RIO completion notification path did not run.'
                            }
                        }
                        $record = [pscustomobject]@{
                            repeat = $repeat + 1; backend = $backend; scheme = $scheme; mode = $mode
                            rps = $result.rps; result = $result
                            serverCpuCores = ($after.cpuMilliseconds - $before.cpuMilliseconds) / ($result.seconds * 1000)
                            allocatedBytesPerRequest = ($after.allocatedBytes - $before.allocatedBytes) / $result.requests
                            serverBefore = $before; serverAfter = $after; transport = $native
                        }
                        $runs.Add($record)
                        $runs.ToArray() | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $OutputDirectory 'runs.json')
                        Write-Host ("{0} {1} {2} round={3} RPS={4:N0} serverCPU={5:N2} clientCPU={6:N2}" -f
                            $backend, $scheme, $mode, ($repeat + 1), $result.rps, $record.serverCpuCores, $result.clientCpuCores)
                        if ($mode -eq 'short' -and $ShortCooldownSeconds -gt 0) {
                            Start-Sleep -Seconds $ShortCooldownSeconds
                        }
                    }
                    finally {
                        foreach ($child in @($client, $server)) {
                            if ($null -ne $child) {
                                if (-not $child.HasExited) { Stop-Process -Id $child.Id -Force }
                                $child.Dispose()
                            }
                        }
                        if ($null -ne $serverOutput) {
                            $serverOutput.GetAwaiter().GetResult() | Set-Content (Join-Path $OutputDirectory "$label-server.log")
                            $serverError.GetAwaiter().GetResult() | Set-Content (Join-Path $OutputDirectory "$label-server-error.log")
                        }
                    }
                }
            }
        }
    }
    Write-Host "RESULTS $OutputDirectory"
}
finally {
    Pop-Location
}
