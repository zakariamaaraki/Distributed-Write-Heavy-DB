param(
    [string]$ImageName = "lsm-write-db:tcp-sql",
    [string]$ContainerName = "lsm-write-db-tcp-sql",
    [int]$HttpPort = 8080,
    [int]$TcpPort = 6543,
    [switch]$Rebuild,
    [switch]$KeepContainer
)

$ErrorActionPreference = "Stop"

function Write-Banner {
    param([string]$Text)
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Write-Info {
    param([string]$Text)
    Write-Host "  $Text" -ForegroundColor DarkCyan
}

function Write-Ok {
    param([string]$Text)
    Write-Host "  $Text" -ForegroundColor Green
}

function Write-Warn {
    param([string]$Text)
    Write-Host "  $Text" -ForegroundColor Yellow
}

function Test-CommandExists {
    param([string]$Name)
    $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-Docker {
    param([string[]]$Arguments)
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Wait-ForTcp {
    param(
        [string]$HostName,
        [int]$Port,
        [int]$TimeoutSeconds = 45
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $connectTask = $client.ConnectAsync($HostName, $Port)
            if ($connectTask.Wait(1000) -and $client.Connected) {
                $client.Dispose()
                return
            }
            $client.Dispose()
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    }

    throw "Timed out waiting for TCP SQL on ${HostName}:${Port}."
}

function Read-AvailableText {
    param(
        [System.IO.StreamReader]$Reader,
        [int]$QuietMilliseconds = 120,
        [int]$TimeoutMilliseconds = 3000
    )

    $output = [System.Text.StringBuilder]::new()
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $lastRead = [System.Diagnostics.Stopwatch]::StartNew()

    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        while ($Reader.Peek() -ge 0) {
            [void]$output.Append([char]$Reader.Read())
            $lastRead.Restart()
        }

        if ($output.Length -gt 0 -and $lastRead.ElapsedMilliseconds -ge $QuietMilliseconds) {
            break
        }

        Start-Sleep -Milliseconds 20
    }

    return $output.ToString()
}

function Write-ServerText {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return
    }

    foreach ($line in ($Text -split "`n")) {
        $trimmed = $line.TrimEnd("`r")
        if ($trimmed.StartsWith("OK ")) {
            Write-Host $trimmed -ForegroundColor Green
        }
        elseif ($trimmed.StartsWith("ERR ")) {
            Write-Host $trimmed -ForegroundColor Red
        }
        elseif ($trimmed -eq "BYE") {
            Write-Host $trimmed -ForegroundColor Yellow
        }
        elseif ($trimmed.Length -gt 0) {
            Write-Host $trimmed -ForegroundColor Gray
        }
    }
}

function Start-SqlConsole {
    param(
        [string]$HostName,
        [int]$Port
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    $client.Connect($HostName, $Port)

    try {
        $stream = $client.GetStream()
        $reader = [System.IO.StreamReader]::new($stream, [System.Text.Encoding]::UTF8, $false, 4096, $true)
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true

        Write-ServerText (Read-AvailableText $reader)

        Write-Host ""
        Write-Host "Interactive TCP SQL CLI" -ForegroundColor Cyan
        Write-Host "End SQL with ';'. Type exit, quit, or \q to disconnect." -ForegroundColor DarkGray
        Write-Host "Examples:" -ForegroundColor DarkGray
        Write-Host "  CREATE TABLE users;" -ForegroundColor DarkGray
        Write-Host "  INSERT INTO users VALUES ('user:1001', '{`"name`":`"Ada`",`"tier`":`"gold`"}');" -ForegroundColor DarkGray
        Write-Host "  CREATE INDEX idx_users_tier ON users (value.tier);" -ForegroundColor DarkGray
        Write-Host "  SELECT key, value FROM users WHERE value.tier = 'gold';" -ForegroundColor DarkGray
        Write-Host ""

        $buffer = [System.Text.StringBuilder]::new()
        while ($true) {
            $prompt = if ($buffer.Length -eq 0) { "sql> " } else { "...> " }
            $line = Read-Host $prompt
            if ($null -eq $line) {
                break
            }

            $trimmed = $line.Trim()
            if ($buffer.Length -eq 0 -and ($trimmed -ieq "exit" -or $trimmed -ieq "quit" -or $trimmed -eq "\q")) {
                $writer.WriteLine("QUIT;")
                Write-ServerText (Read-AvailableText $reader)
                break
            }

            [void]$buffer.AppendLine($line)
            $statement = $buffer.ToString().Trim()
            if (-not $statement.EndsWith(";")) {
                continue
            }

            $writer.WriteLine($statement)
            $buffer.Clear() | Out-Null
            $response = Read-AvailableText $reader
            Write-ServerText $response

            if ($statement.Trim().TrimEnd(";") -match "(?i)^(quit|exit|\\q)$") {
                break
            }
        }
    }
    finally {
        $client.Dispose()
    }
}

if (-not (Test-CommandExists "docker")) {
    throw "Docker CLI was not found in PATH."
}

Write-Banner "LsmWriteDb TCP SQL Docker CLI"
Write-Info "Image: $ImageName"
Write-Info "Container: $ContainerName"
Write-Info "HTTP: http://localhost:$HttpPort"
Write-Info "TCP SQL: 127.0.0.1:$TcpPort"

$imageExists = $false
try {
    $imageId = (& docker image inspect $ImageName --format "{{.Id}}" 2>$null)
    $imageExists = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($imageId)
}
catch {
    $imageExists = $false
}

if ($Rebuild -or -not $imageExists) {
    Write-Banner "Building Docker image"
    Invoke-Docker @("build", "-t", $ImageName, ".")
    Write-Ok "Image ready."
}
else {
    Write-Ok "Image already exists. Use -Rebuild to rebuild."
}

$existing = (& docker ps -a --filter "name=^/$ContainerName$" --format "{{.ID}}" 2>$null)
if (-not [string]::IsNullOrWhiteSpace($existing)) {
    Write-Banner "Replacing existing container"
    Invoke-Docker @("rm", "-f", $ContainerName)
}

Write-Banner "Starting detached container"
Invoke-Docker @(
    "run",
    "-d",
    "--name", $ContainerName,
    "-p", "${HttpPort}:8080",
    "-p", "${TcpPort}:6543",
    "-e", "TcpSql__Enabled=true",
    "-e", "TcpSql__Host=0.0.0.0",
    "-e", "TcpSql__Port=6543",
    "-e", "Raft__Enabled=false",
    $ImageName
)

try {
    Wait-ForTcp -HostName "127.0.0.1" -Port $TcpPort
    Write-Ok "TCP SQL is ready."
    Start-SqlConsole -HostName "127.0.0.1" -Port $TcpPort
}
finally {
    if ($KeepContainer) {
        Write-Warn "Container left running: $ContainerName"
    }
    else {
        Write-Banner "Stopping container"
        try {
            Invoke-Docker @("rm", "-f", $ContainerName)
            Write-Ok "Container removed."
        }
        catch {
            Write-Warn $_.Exception.Message
        }
    }
}
