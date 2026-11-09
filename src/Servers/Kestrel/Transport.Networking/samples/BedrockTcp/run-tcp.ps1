# Raw-TCP client comparison against a standalone C server (tcpserver\tcp_server.c). No HTTP, no Kestrel on either side.
# Usage (from the repository root, after `. ./activate.ps1`, a Release build of BedrockTcp.csproj and a Windows build of tcp_server.c):
#   pwsh -NoProfile -File src\Servers\Kestrel\Transport.Networking\samples\BedrockTcp\run-tcp.ps1 -Out <dir>
# Arms: sockets (plain Socket), <iocp|rio>-pipe (ConnectionContext.Transport), <iocp|rio>-stream (the Stream SocketsHttpHandler would get).
# A case is "<request bytes>:<response bytes>"; the server reads the request, then writes the response, and repeats.
param(
    [string]$Out = '.',
    [int]$Reps = 3,
    [int]$Duration = 8,
    [int]$Warmup = 2,
    [int]$Connections = 64,
    [string[]]$Cases = @('64:64', '64:1024', '64:8192', '64:65536'),
    [string[]]$Arms = @('sockets', 'iocp-pipe', 'iocp-stream', 'rio-pipe', 'rio-stream'),
    [int]$Port = 5601,
    [int]$Workers = 8,
    [int]$ServerThreads = 8,
    [string]$ServerCpus = '16-31',
    [string]$ClientCpus = '0-7',
    [string]$ServerExe = 'D:\code\NetworkingTransports-Results\tcp-bin\tcp_server.exe'
)

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path (Get-Location) '.dotnet\dotnet.exe'
$dll = Join-Path (Get-Location) 'artifacts\bin\BedrockTcp\Release\net11.0\BedrockTcp.dll'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$csv = Join-Path $Out 'tcp-client.csv'
if (-not (Test-Path $csv))
{
    'rep,arm,request,response,rps,cpuMsPer1kReq,p50us,p99us,errors,clientWorkingSetMB' | Set-Content $csv
}

# The client pins itself with ProcessorAffinity after the runtime has started, so Environment.ProcessorCount and the ThreadPool
# minimum would still describe the whole machine. DOTNET_PROCESSOR_COUNT gives the process the same view taskset gives on Linux.
function Get-CpuCount([string]$spec)
{
    $count = 0
    foreach ($part in $spec.Split(','))
    {
        $range = $part.Split('-')
        $count += [int]$range[-1] - [int]$range[0] + 1
    }
    return $count
}

function Wait-Port([int]$port)
{
    for ($i = 0; $i -lt 100; $i++)
    {
        try
        {
            $c = [System.Net.Sockets.TcpClient]::new()
            $c.Connect('127.0.0.1', $port)
            $c.Dispose()
            return
        }
        catch
        {
            Start-Sleep -Milliseconds 200
        }
    }
    throw "server did not start on port $port"
}

function Invoke-Cell($rep, $arm, $request, $response)
{
    $tag = "r$rep-$arm-q$request-p$response"
    $serverArgs = @('--port', $Port, '--threads', $ServerThreads, '--request-bytes', $request, '--response-bytes', $response, '--cpus', $ServerCpus)
    $srv = Start-Process -FilePath $ServerExe -ArgumentList $serverArgs -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $Out "$tag.server.txt") -RedirectStandardError (Join-Path $Out "$tag.server.err")
    try
    {
        Wait-Port $Port
        $clientArgs = @($dll, '--arm', $arm, '--port', $Port, '--connections', $Connections, '--duration', $Duration, '--warmup', $Warmup, '--request-bytes', $request, '--response-bytes', $response, '--workers', $Workers, '--cpus', $ClientCpus)
        $env:DOTNET_PROCESSOR_COUNT = Get-CpuCount $ClientCpus
        $cli = Start-Process -FilePath $dotnet -ArgumentList $clientArgs -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $Out "$tag.client.txt") -RedirectStandardError (Join-Path $Out "$tag.client.err")
        Remove-Item Env:DOTNET_PROCESSOR_COUNT
        if (-not $cli.WaitForExit(($Duration + $Warmup + 90) * 1000))
        {
            Stop-Process -Id $cli.Id -Force
            Write-Host "TIMEOUT $tag"
        }
        $line = Select-String -Path (Join-Path $Out "$tag.client.txt") -Pattern '^RESULT ' | Select-Object -Last 1
        if ($null -eq $line)
        {
            Write-Host "NO RESULT $tag"
            return
        }
        $kv = @{}
        foreach ($m in [regex]::Matches($line.Line, '(\w+)=(\S+)'))
        {
            $kv[$m.Groups[1].Value] = $m.Groups[2].Value
        }
        $row = "$rep,$arm,$request,$response,$($kv.rps),$($kv.cpuMsPer1kReq),$($kv.p50us),$($kv.p99us),$($kv.errors),$($kv.workingSetMB)"
        Add-Content $csv $row
        Write-Host $row
    }
    finally
    {
        Stop-Process -Id $srv.Id -Force
        Start-Sleep -Milliseconds 300
    }
}

for ($rep = 1; $rep -le $Reps; $rep++)
{
    foreach ($case in $Cases)
    {
        $request, $response = $case.Split(':')
        # Rotate the arm order per rep so no arm always runs first.
        for ($i = 0; $i -lt $Arms.Count; $i++)
        {
            Invoke-Cell $rep $Arms[($i + $rep - 1) % $Arms.Count] $request $response
        }
    }
}
