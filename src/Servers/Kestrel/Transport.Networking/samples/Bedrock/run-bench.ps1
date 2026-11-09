# Runs the Bedrock client/server comparison cells and appends one CSV row per cell.
# Usage (from the repository root, after `. ./activate.ps1` and a Release build of Bedrock.csproj):
#   pwsh -NoProfile -File src\Servers\Kestrel\Transport.Networking\samples\Bedrock\run-bench.ps1 -Plan client -Out <dir>
# Plans:
#   client - plain sockets server (default cpus 16-31); client handler varies (sockets / iocp / rio; read-ahead -1 is the default UseConnectionFactory overload), client on cpus 0-7.
#   server - plain SocketsHttpHandler client (default cpus 16-31); server registration varies (sockets / AddNetworkTransport / UseNetworkTransport), server on cpus 0-3.
#   both   - Bedrock transport on both sides (server cpus 0-7, client cpus 16-31).
param(
    [ValidateSet('client', 'server', 'both')][string]$Plan = 'client',
    [string]$Out = '.',
    [int]$Reps = 2,
    [int]$Duration = 8,
    [int]$Warmup = 2,
    [int]$Connections = 64,
    [int[]]$Bodies = @(1024, 8192, 65536),
    [int]$Port = 5443,
    [int]$ClientWorkers = 8,
    [int]$ServerWorkers = 4,
    [string]$ServerCpus = '',
    [string]$ClientCpus = '',
    [string[]]$Cells = @()
)

$ErrorActionPreference = 'Stop'
$dotnet = Join-Path (Get-Location) '.dotnet\dotnet.exe'
$dll = Join-Path (Get-Location) 'artifacts\bin\Bedrock\Release\net11.0\Bedrock.dll'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$csv = Join-Path $Out "bedrock-$Plan.csv"
if (-not (Test-Path $csv))
{
    'plan,rep,server,registration,client,readAhead,body,rps,cpuMsPer1kReq,p50us,p99us,errors,clientWorkingSetMB,serverWorkingSetMB' | Set-Content $csv
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

function Invoke-Cell($rep, $server, $registration, $client, $readAhead, $body, $serverCpus, $clientCpus)
{
    $tag = "$Plan-r$rep-s$server-$registration-c$client-ra$readAhead-b$body"
    $serverArgs = @($dll, 'serve', '--backend', $server, '--registration', $registration, '--port', $Port, '--body-bytes', $body, '--workers', $ServerWorkers, '--cpus', $serverCpus)
    $srv = Start-Process -FilePath $dotnet -ArgumentList $serverArgs -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $Out "$tag.server.txt") -RedirectStandardError (Join-Path $Out "$tag.server.err")
    try
    {
        Wait-Port $Port
        $clientArgs = @($dll, 'bench', '--handler', $client, '--url', "http://127.0.0.1:$Port/", '--connections', $Connections, '--duration', $Duration, '--warmup', $Warmup, '--body-bytes', $body, '--workers', $ClientWorkers, '--cpus', $clientCpus, '--read-ahead', $readAhead)
        $cli = Start-Process -FilePath $dotnet -ArgumentList $clientArgs -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $Out "$tag.client.txt") -RedirectStandardError (Join-Path $Out "$tag.client.err")
        if (-not $cli.WaitForExit(($Duration + $Warmup + 90) * 1000))
        {
            Stop-Process -Id $cli.Id -Force
            Write-Host "TIMEOUT $tag"
        }
        $srv.Refresh()
        $serverWs = [int]($srv.WorkingSet64 / 1MB)
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
        $row = "$Plan,$rep,$server,$registration,$client,$readAhead,$body,$($kv.rps),$($kv.cpuMsPer1kReq),$($kv.p50us),$($kv.p99us),$($kv.errors),$($kv.workingSetMB),$serverWs"
        Add-Content $csv $row
        Write-Host $row
    }
    finally
    {
        try
        {
            Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:$Port/shutdown" -TimeoutSec 3 | Out-Null
        }
        catch
        {
        }
        if (-not $srv.WaitForExit(5000))
        {
            Stop-Process -Id $srv.Id -Force
        }
    }
}

$defaults = @{ client = @('16-31', '0-7'); server = @('0-3', '16-31'); both = @('0-7', '16-31') }
if ($ServerCpus -eq '')
{
    $ServerCpus = $defaults[$Plan][0]
}
if ($ClientCpus -eq '')
{
    $ClientCpus = $defaults[$Plan][1]
}
for ($rep = 1; $rep -le $Reps; $rep++)
{
    foreach ($body in $Bodies)
    {
        switch ($Plan)
        {
            'client'
            {
                # Rotate the arm order per rep so no client handler always runs first.
                $arms = @(@('sockets', -1), @('iocp', -1), @('rio', -1))
                for ($i = 0; $i -lt $arms.Count; $i++)
                {
                    $c = $arms[($i + $rep - 1) % $arms.Count]
                    if ($Cells.Count -gt 0 -and "$($c[0]):$($c[1])" -notin $Cells)
                    {
                        continue
                    }
                    Invoke-Cell $rep 'sockets' 'bedrock' $c[0] $c[1] $body $ServerCpus $ClientCpus
                }
            }
            'server'
            {
                $arms = @(@('sockets', 'bedrock'), @('iocp', 'bedrock'), @('iocp', 'legacy'), @('rio', 'bedrock'), @('rio', 'legacy'))
                for ($i = 0; $i -lt $arms.Count; $i++)
                {
                    $s = $arms[($i + $rep - 1) % $arms.Count]
                    Invoke-Cell $rep $s[0] $s[1] 'sockets' -1 $body $ServerCpus $ClientCpus
                }
            }
            'both'
            {
                foreach ($p in @(@('sockets', 'sockets', -1), @('iocp', 'iocp', -1), @('rio', 'rio', -1)))
                {
                    Invoke-Cell $rep $p[0] 'bedrock' $p[1] $p[2] $body $ServerCpus $ClientCpus
                }
            }
        }
    }
}
