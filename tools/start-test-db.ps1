<#
.SYNOPSIS
    Starts (or creates) a throwaway SQL Server container for the SqlDrop integration tests.

.DESCRIPTION
    Requires Docker (the `docker` command on PATH). The credentials are test-only and match the
    default connection string used by tests/SqlDrop.Tests. The container is created on first run
    and simply started on later runs; data lives inside the container, so it is disposable.

.PARAMETER Name
    Container name. Default: sqldrop-test-db

.PARAMETER Port
    Host port mapped to SQL Server's 1433. Default: 14333

.PARAMETER Password
    SA password (must satisfy SQL Server's complexity rules). Default: the one the tests expect.
    If you change it (or the port), set SQLDROP_TEST_CONN for `dotnet test` accordingly.

.PARAMETER Remove
    Remove the container instead of starting it.

.EXAMPLE
    ./tools/start-test-db.ps1
    dotnet test
#>
[CmdletBinding()]
param(
    [string]$Name = 'sqldrop-test-db',
    [int]$Port = 14333,
    [string]$Password = 'SqlDrop_Test_123!',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "'docker' was not found on PATH. Install Docker (Desktop) and make sure it is running."
}

function Test-ContainerExists { [bool](docker ps -a --filter "name=^$Name$" --format '{{.Names}}') }
function Test-ContainerRunning { [bool](docker ps --filter "name=^$Name$" --format '{{.Names}}') }

if ($Remove) {
    if (Test-ContainerExists) {
        docker rm -f $Name | Out-Null
        Write-Host "Removed container '$Name'."
    } else {
        Write-Host "Container '$Name' does not exist - nothing to remove."
    }
    return
}

if (-not (Test-ContainerExists)) {
    Write-Host "Creating container '$Name' (the first run pulls the SQL Server image, which can take a few minutes)..."
    docker run -d --name $Name `
        -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$Password" `
        -p "${Port}:1433" `
        mcr.microsoft.com/mssql/server:2022-latest | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker run failed (exit code $LASTEXITCODE)." }
}
elseif (-not (Test-ContainerRunning)) {
    Write-Host "Starting existing container '$Name'..."
    docker start $Name | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker start failed (exit code $LASTEXITCODE)." }
}
else {
    Write-Host "Container '$Name' is already running."
}

# SQL Server needs a while after the container starts before it accepts logins.
Write-Host -NoNewline 'Waiting for SQL Server to accept connections'
$ready = $false
for ($i = 0; $i -lt 60 -and -not $ready; $i++) {
    docker exec $Name /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $Password -C -l 3 -Q 'SELECT 1' *> $null
    if ($LASTEXITCODE -eq 0) { $ready = $true } else { Write-Host -NoNewline '.'; Start-Sleep -Seconds 2 }
}
Write-Host ''
if (-not $ready) {
    throw "SQL Server did not become ready in time. Check: docker logs $Name"
}

Write-Host 'Ready.'
# 127.0.0.1 rather than localhost: localhost may resolve to IPv6 first, which Docker's port forwarding doesn't serve reliably.
Write-Host "Connection string: Server=127.0.0.1,$Port;Database=SqlDropTest;User Id=sa;Password=$Password;TrustServerCertificate=True"
