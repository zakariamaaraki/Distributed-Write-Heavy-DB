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

function Test-DockerEngine {
    $serverVersion = (& docker info --format "{{.ServerVersion}}" 2>$null)
    return $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($serverVersion)
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
                $stream = $client.GetStream()
                $bytes = [byte[]]::new(4096)
                $banner = [System.Text.StringBuilder]::new()
                $bannerDeadline = (Get-Date).AddSeconds(3)

                while ((Get-Date) -lt $bannerDeadline) {
                    if ($client.Client.Poll(100000, [System.Net.Sockets.SelectMode]::SelectRead)) {
                        if ($client.Available -eq 0) {
                            break
                        }

                        $readLength = [Math]::Min($bytes.Length, $client.Available)
                        $count = $stream.Read($bytes, 0, $readLength)
                        if ($count -le 0) {
                            break
                        }

                        [void]$banner.Append([System.Text.Encoding]::UTF8.GetString($bytes, 0, $count))
                        if ($banner.ToString().Contains("LsmWriteDb TCP SQL ready")) {
                            $client.Dispose()
                            return
                        }
                    }
                }
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
        [System.Net.Sockets.TcpClient]$Client,
        [System.Net.Sockets.NetworkStream]$Stream,
        [int]$TimeoutMilliseconds = 5000
    )

    $output = [System.Text.StringBuilder]::new()
    $bytes = [byte[]]::new(4096)
    $deadline = (Get-Date).AddMilliseconds($TimeoutMilliseconds)
    $disconnected = $false

    while ((Get-Date) -lt $deadline) {
        try {
            $readable = $Client.Client.Poll(100000, [System.Net.Sockets.SelectMode]::SelectRead)
            if (-not $readable) {
                continue
            }

            if ($Client.Available -eq 0) {
                $disconnected = $true
                break
            }

            while ($Client.Available -gt 0) {
                $readLength = [Math]::Min($bytes.Length, $Client.Available)
                $count = $Stream.Read($bytes, 0, $readLength)
                if ($count -le 0) {
                    $disconnected = $true
                    break
                }

                [void]$output.Append([System.Text.Encoding]::UTF8.GetString($bytes, 0, $count))
                $text = $output.ToString()
                if ($text.EndsWith("lsm> ") -or $text.EndsWith("...> ") -or $text.TrimEnd().EndsWith("BYE")) {
                    break
                }
            }

            if ($disconnected) {
                break
            }

            if ($output.Length -gt 0) {
                $text = $output.ToString()
                if ($text.EndsWith("lsm> ") -or $text.EndsWith("...> ") -or $text.TrimEnd().EndsWith("BYE")) {
                    break
                }
            }
        }
        catch [System.IO.IOException] {
            $disconnected = $true
            break
        }
        catch [System.Net.Sockets.SocketException] {
            $disconnected = $true
            break
        }
        catch [System.ObjectDisposedException] {
            $disconnected = $true
            break
        }
    }

    if (-not $disconnected -and $output.Length -eq 0 -and (Get-Date) -ge $deadline) {
        Write-Warn "Timed out waiting for a TCP SQL response."
    }

    return [pscustomobject]@{
        Text = $output.ToString()
        Disconnected = $disconnected
    }
}

function Write-ServerText {
    param([string]$Text)

    if ([string]::IsNullOrEmpty($Text)) {
        return
    }

    $displayText = $Text.Replace("lsm> ", "").Replace("...> ", "")
    foreach ($line in ($displayText -split "`n")) {
        $trimmed = $line.TrimEnd("`r")
        if ($trimmed.StartsWith("OK ")) {
            Write-OkResult $trimmed
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

function ConvertTo-DisplayText {
    param($Value)

    if ($null -eq $Value) {
        return "NULL"
    }

    return [string]$Value
}

function Write-TableRows {
    param(
        [string[]]$Columns,
        [object[]]$Rows
    )

    $widths = @{}
    foreach ($column in $Columns) {
        $widths[$column] = $column.Length
    }

    foreach ($row in $Rows) {
        foreach ($column in $Columns) {
            $value = ConvertTo-DisplayText $row.$column
            $widths[$column] = [Math]::Max($widths[$column], $value.Length)
        }
    }

    $separatorParts = @()
    $headerParts = @()
    foreach ($column in $Columns) {
        $width = $widths[$column]
        $separatorParts += ("-" * ($width + 2))
        $headerParts += (" " + $column.PadRight($width) + " ")
    }

    Write-Host ("+" + ($separatorParts -join "+") + "+") -ForegroundColor DarkGray
    Write-Host ("|" + ($headerParts -join "|") + "|") -ForegroundColor Cyan
    Write-Host ("+" + ($separatorParts -join "+") + "+") -ForegroundColor DarkGray

    foreach ($row in $Rows) {
        $rowParts = @()
        foreach ($column in $Columns) {
            $value = ConvertTo-DisplayText $row.$column
            $rowParts += (" " + $value.PadRight($widths[$column]) + " ")
        }

        Write-Host ("|" + ($rowParts -join "|") + "|") -ForegroundColor Gray
    }

    Write-Host ("+" + ($separatorParts -join "+") + "+") -ForegroundColor DarkGray
}

function Write-OkResult {
    param([string]$Line)

    try {
        $payload = $Line.Substring(3) | ConvertFrom-Json
        if ([string]::Equals($payload.statementType, "SELECT", [System.StringComparison]::OrdinalIgnoreCase)) {
            $rows = @($payload.rows)
            if ($rows.Count -eq 0) {
                Write-Host "OK SELECT (0 rows)" -ForegroundColor Green
                return
            }

            $columns = @()
            foreach ($row in $rows) {
                foreach ($property in $row.PSObject.Properties) {
                    if ($columns -notcontains $property.Name) {
                        $columns += $property.Name
                    }
                }
            }

            Write-TableRows -Columns $columns -Rows $rows
            Write-Host "OK SELECT ($($rows.Count) row$(if ($rows.Count -eq 1) { '' } else { 's' }))" -ForegroundColor Green
            return
        }

        $summary = "OK $($payload.statementType)"
        if ($null -ne $payload.rowsAffected) {
            $summary += " ($($payload.rowsAffected) row$(if ($payload.rowsAffected -eq 1) { '' } else { 's' }) affected)"
        }

        if (-not [string]::IsNullOrWhiteSpace($payload.message)) {
            $summary += " - $($payload.message)"
        }

        Write-Host $summary -ForegroundColor Green
    }
    catch {
        Write-Host $Line -ForegroundColor Green
    }
}

function Write-ServerResponse {
    param($Response)

    Write-ServerText $Response.Text

    if ($Response.Disconnected) {
        Write-Warn "TCP SQL connection closed by the server."
        return $false
    }

    return $true
}

function Test-SqlCommandStart {
    param([string]$Text)

    return $Text -match "(?i)^\s*(begin|commit|rollback|create|insert|select|update|delete)\b"
}

function Read-ConsoleLine {
    param([string]$Prompt)

    Write-Host -NoNewline $Prompt -ForegroundColor Cyan
    return [Console]::ReadLine()
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
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.NewLine = "`n"
        $writer.AutoFlush = $true

        if (-not (Write-ServerResponse (Read-AvailableText -Client $client -Stream $stream))) {
            return $false
        }

        Write-Host ""
        Write-Host "Interactive TCP SQL CLI" -ForegroundColor Cyan
        Write-Host "End every SQL statement with ';'. Type exit, quit, or \q to disconnect." -ForegroundColor DarkGray
        Write-Host "Use \clear to discard an unfinished statement." -ForegroundColor DarkGray
        Write-Host "Examples:" -ForegroundColor DarkGray
        Write-Host "  CREATE TABLE users;" -ForegroundColor DarkGray
        Write-Host "  INSERT INTO users VALUES ('user:1001', '{`"name`":`"Ada`",`"tier`":`"gold`"}');" -ForegroundColor DarkGray
        Write-Host "  CREATE INDEX idx_users_tier ON users (value.tier);" -ForegroundColor DarkGray
        Write-Host "  SELECT key, value FROM users WHERE value.tier = 'gold';" -ForegroundColor DarkGray
        Write-Host ""

        $buffer = [System.Text.StringBuilder]::new()
        while ($true) {
            $prompt = if ($buffer.Length -eq 0) { "sql> " } else { "...> " }
            $line = Read-ConsoleLine $prompt
            if ($null -eq $line) {
                break
            }

            $trimmed = $line.Trim()
            if ($trimmed -ieq "exit" -or $trimmed -ieq "quit" -or $trimmed -eq "\q") {
                $writer.WriteLine("QUIT;")
                [void](Write-ServerResponse (Read-AvailableText -Client $client -Stream $stream))
                return $true
            }

            if ($trimmed -ieq "\clear") {
                $buffer.Clear() | Out-Null
                Write-Warn "Unfinished statement cleared."
                continue
            }

            if ($buffer.Length -gt 0 -and (Test-SqlCommandStart $trimmed)) {
                Write-Warn "The previous statement is missing ';'. Type ';' to run it, or \clear to discard it."
                continue
            }

            [void]$buffer.AppendLine($line)
            $statement = $buffer.ToString().Trim()
            if (-not $statement.EndsWith(";")) {
                continue
            }

            $writer.WriteLine($statement)
            $buffer.Clear() | Out-Null
            $response = Read-AvailableText -Client $client -Stream $stream
            if (-not (Write-ServerResponse $response)) {
                return $false
            }

            if ($statement.Trim().TrimEnd(";") -match "(?i)^(quit|exit|\\q)$") {
                return $true
            }
        }

        return $true
    }
    catch [System.IO.IOException] {
        Write-Warn "TCP SQL connection failed: $($_.Exception.Message)"
        return $false
    }
    catch [System.ObjectDisposedException] {
        Write-Warn "TCP SQL connection was closed."
        return $false
    }
    finally {
        $client.Dispose()
    }
}

if (-not (Test-CommandExists "docker")) {
    throw "Docker CLI was not found in PATH."
}

if (-not (Test-DockerEngine)) {
    throw @"
Docker CLI was found, but the Docker engine is not reachable.

Start Docker Desktop and wait until the Linux engine is running, then rerun:
  .\scripts\tcp-sql-cli.cmd -Rebuild
"@
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

$sessionCompleted = $false
try {
    Wait-ForTcp -HostName "127.0.0.1" -Port $TcpPort
    Write-Ok "TCP SQL is ready."
    $sessionCompleted = Start-SqlConsole -HostName "127.0.0.1" -Port $TcpPort
    if (-not $sessionCompleted) {
        Write-Banner "Recent container logs"
        & docker logs --tail 80 $ContainerName
        Write-Warn "Container left running for inspection: $ContainerName"
    }
}
finally {
    if ($KeepContainer -or -not $sessionCompleted) {
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
