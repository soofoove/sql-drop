# Starts a throwaway SQL Server container for local testing (test-only credentials).
$docker = 'C:\Users\mykha\AppData\Local\Programs\DockerDesktop\resources\bin\docker.exe'
if (-not (Test-Path $docker)) { $docker = 'docker' }
$env:PATH += ';' + (Split-Path $docker)
$name = 'sqldrop-test-db'
if (-not (& $docker ps -a --filter "name=^$name$" --format '{{.Names}}')) {
    & $docker run -d --name $name -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=SqlDrop_Test_123!' -p 14333:1433 mcr.microsoft.com/mssql/server:2022-latest
} else {
    & $docker start $name
}
Write-Host "Connection string: Server=127.0.0.1,14333;Database=SqlDropTest;User Id=sa;Password=SqlDrop_Test_123!;TrustServerCertificate=True"

