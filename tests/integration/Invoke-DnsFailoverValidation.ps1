[CmdletBinding()]
param(
    [string]$ConfigPath = "$env:ProgramData\BeszelAgentManager\config.json",
    [string]$StatePath = "$env:ProgramData\BeszelAgentManager\dns-fallback-state.json",
    [string]$NssmPath = "$env:ProgramData\BeszelAgentManager\nssm\nssm.exe"
)

$ErrorActionPreference = 'Stop'
$backupRoot = Join-Path $env:LOCALAPPDATA "BeszelAgentManager\TestBackups\dns-failover-$((Get-Date).ToString('yyyyMMdd-HHmmss'))"
$configBackup = Join-Path $backupRoot 'config.json'
$environmentBackup = Join-Path $backupRoot 'AppEnvironmentExtra.txt'
$script:restored = $false

function Save-JsonAtomically([string]$Path, [object]$Value) {
    $temporary = "$Path.$PID.test.tmp"
    $Value | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $temporary -Encoding utf8
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Move-Item -LiteralPath $temporary -Destination $Path -Force
            return
        } catch {
            if ($attempt -ge 5) {
                throw
            }
            Start-Sleep -Milliseconds (150 * $attempt)
        }
    }
}

function Invoke-BrokerConfigApply {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        '.',
        'BeszelAgentManager.Background.v1',
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous,
        [System.Security.Principal.TokenImpersonationLevel]::Impersonation)
    try {
        $pipe.Connect(5000)
        $encoding = [System.Text.UTF8Encoding]::new($false)
        $writer = [System.IO.StreamWriter]::new($pipe, $encoding, 1024, $true)
        $reader = [System.IO.StreamReader]::new($pipe, $encoding, $false, 1024, $true)
        try {
            $requestId = [guid]::NewGuid().ToString('N')
            $request = @{
                ProtocolVersion = 1
                RequestId = $requestId
                Action = 'config.apply'
                Arguments = @{}
            } | ConvertTo-Json -Compress
            $writer.WriteLine($request)
            $writer.Flush()
            $response = $reader.ReadLine() | ConvertFrom-Json
            if (-not $response.Success -or $response.RequestId -ne $requestId) {
                throw "Broker config restore failed with error code $($response.ErrorCode)."
            }
        } finally {
            $writer.Dispose()
            $reader.Dispose()
        }
    } finally {
        $pipe.Dispose()
    }
}

function Get-NssmEnvironment {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $NssmPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::Unicode
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::Unicode
    $startInfo.ArgumentList.Add('get')
    $startInfo.ArgumentList.Add('Beszel Agent')
    $startInfo.ArgumentList.Add('AppEnvironmentExtra')
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $output = $process.StandardOutput.ReadToEnd()
    $errorText = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "NSSM environment read failed: $errorText"
    }
    return @($output -split "[`0`r`n]+" | Where-Object { $_.Length -gt 0 } | Sort-Object)
}

function Wait-ForFailoverState([bool]$Active, [int]$TimeoutSeconds) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (Test-Path -LiteralPath $StatePath) {
            try {
                $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
                if ([bool]$state.active -eq $Active) {
                    return $state
                }
            } catch {
            }
        }
        Start-Sleep -Seconds 5
    } while ((Get-Date) -lt $deadline)
    throw "Timed out waiting for DNS failover active=$Active."
}

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
Copy-Item -LiteralPath $ConfigPath -Destination $configBackup
$originalEnvironment = Get-NssmEnvironment
$originalEnvironment | Set-Content -LiteralPath $environmentBackup -Encoding utf8
$initialStateActive = $false
if (Test-Path -LiteralPath $StatePath) {
    try {
        $initialStateActive = [bool](Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json).active
    } catch {
    }
}

try {
    $configuration = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$configuration.hub_url_ip_fallback)) {
        throw 'A fallback URL must be configured before running this validation.'
    }

    $originalPrimary = [string]$configuration.hub_url
    $fallback = [string]$configuration.hub_url_ip_fallback
    $configuration.hub_url = "https://bam-failover-$([guid]::NewGuid().ToString('N')).invalid"
    $configuration.hub_url_ip_fallback_enabled = $true
    Save-JsonAtomically $ConfigPath $configuration
    Write-Output 'Waiting for primary DNS failure to activate fallback...'
    Wait-ForFailoverState $true 150 | Out-Null

    $fallbackEnvironment = Get-NssmEnvironment
    if (-not ($fallbackEnvironment -contains "HUB_URL=$fallback")) {
        throw 'The failover state activated, but the agent environment does not contain the fallback HUB_URL.'
    }

    Copy-Item -LiteralPath $configBackup -Destination $ConfigPath -Force
    Write-Output 'Primary configuration restored; waiting for five successful DNS checks...'
    Wait-ForFailoverState $false 420 | Out-Null

    $restoredEnvironment = Get-NssmEnvironment
    if (-not ($restoredEnvironment -contains "HUB_URL=$originalPrimary")) {
        throw 'The failover state recovered, but the agent environment does not contain the primary HUB_URL.'
    }

    $script:restored = $true
    Write-Output 'PASS: fallback activated once and primary was restored after recovery.'
} finally {
    Copy-Item -LiteralPath $configBackup -Destination $ConfigPath -Force
    Invoke-BrokerConfigApply
    if (-not $initialStateActive -and (Test-Path -LiteralPath $StatePath)) {
        $currentState = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
        if ([bool]$currentState.active) {
            $resetConfiguration = Get-Content -LiteralPath $configBackup -Raw | ConvertFrom-Json
            $resetConfiguration.hub_url_ip_fallback_enabled = $false
            Save-JsonAtomically $ConfigPath $resetConfiguration
            Wait-ForFailoverState $false 150 | Out-Null
            Copy-Item -LiteralPath $configBackup -Destination $ConfigPath -Force
            Invoke-BrokerConfigApply
        }
    }
    $finalEnvironment = Get-NssmEnvironment
    if ((Compare-Object $originalEnvironment $finalEnvironment).Count -ne 0) {
        throw "Original NSSM environment was not restored. Backups remain at $backupRoot"
    }
    $script:restored = $true
    Write-Output "Original config and NSSM environment restored. Backups: $backupRoot"
}

if (-not $script:restored) {
    exit 1
}
