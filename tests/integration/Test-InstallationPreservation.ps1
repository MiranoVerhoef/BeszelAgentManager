[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SnapshotPath,
    [string]$CompareTo
)

$ErrorActionPreference = 'Stop'
$configPath = "$env:ProgramData\BeszelAgentManager\config.json"
$nssmPath = "$env:ProgramData\BeszelAgentManager\nssm\nssm.exe"
$agentLogPath = "$env:ProgramData\BeszelAgentManager\agent_logs"

function Get-FileHashOrEmpty([string]$Path) {
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
    return ''
}

function Invoke-NssmGet([string]$Parameter) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $nssmPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [System.Text.Encoding]::Unicode
    $startInfo.StandardErrorEncoding = [System.Text.Encoding]::Unicode
    $startInfo.ArgumentList.Add('get')
    $startInfo.ArgumentList.Add('Beszel Agent')
    $startInfo.ArgumentList.Add($Parameter)
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $output = $process.StandardOutput.ReadToEnd()
    $errorText = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "NSSM get $Parameter failed: $errorText"
    }
    return $output.Trim("`0", "`r", "`n", ' ')
}

if (-not (Test-Path -LiteralPath $configPath)) {
    throw "Manager config is missing: $configPath"
}

$snapshotDirectory = Split-Path -Parent $SnapshotPath
New-Item -ItemType Directory -Path $snapshotDirectory -Force | Out-Null
$configBackup = "$SnapshotPath.config.json"
Copy-Item -LiteralPath $configPath -Destination $configBackup -Force

$agentApplication = Invoke-NssmGet 'Application'
$environment = Invoke-NssmGet 'AppEnvironmentExtra'
$environmentEntries = @($environment -split "[`0`r`n]+" | Where-Object { $_.Length -gt 0 } | Sort-Object)
$environmentDigestInput = [string]::Join("`n", $environmentEntries)
$environmentDigest = [Convert]::ToHexString(
    [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($environmentDigestInput)))
$service = Get-Service -Name 'Beszel Agent'
$logs = if (Test-Path -LiteralPath $agentLogPath) {
    @(Get-ChildItem -LiteralPath $agentLogPath -File | Select-Object -ExpandProperty Name | Sort-Object)
} else {
    @()
}

$snapshot = [ordered]@{
    CapturedAt = (Get-Date).ToString('o')
    ConfigHash = Get-FileHashOrEmpty $configPath
    ConfigBackup = $configBackup
    AgentServiceStatus = [string]$service.Status
    AgentApplication = $agentApplication
    AgentApplicationHash = Get-FileHashOrEmpty $agentApplication
    NssmEnvironmentHash = $environmentDigest
    AgentLogFiles = $logs
}
$snapshot | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $SnapshotPath -Encoding utf8

if ([string]::IsNullOrWhiteSpace($CompareTo)) {
    Write-Output "Snapshot created: $SnapshotPath"
    exit 0
}

$before = Get-Content -LiteralPath $CompareTo -Raw | ConvertFrom-Json
$failures = [Collections.Generic.List[string]]::new()
foreach ($property in @('ConfigHash', 'AgentApplication', 'AgentApplicationHash', 'NssmEnvironmentHash')) {
    if ([string]$before.$property -ne [string]$snapshot.$property) {
        $failures.Add("$property changed")
    }
}
if ([string]$snapshot.AgentServiceStatus -ne 'Running') {
    $failures.Add("Agent service is $($snapshot.AgentServiceStatus), expected Running")
}
foreach ($logName in @($before.AgentLogFiles)) {
    if ($snapshot.AgentLogFiles -notcontains $logName) {
        $failures.Add("Existing agent log is missing: $logName")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output 'PASS: config, token ciphertext, agent state, NSSM environment, and existing logs were preserved.'
