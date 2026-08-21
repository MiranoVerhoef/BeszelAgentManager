param(
    [Parameter(Mandatory = $true)]
    [string]$DistDir
)

$root = (Resolve-Path -LiteralPath $DistDir).Path
$manifestPath = Join-Path $root 'INSTALL-MANIFEST.sha256'

$entries = Get-ChildItem -LiteralPath $root -File -Recurse |
    Where-Object {
        $_.Name -ne 'INSTALL-MANIFEST.sha256' -and
        $_.Name -ne 'nssm.exe' -and
        $_.Extension -ne '.pdb'
    } |
    ForEach-Object {
        $relativePath = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/')
        if ($relativePath.Contains('|') -or $relativePath.Contains("`r") -or $relativePath.Contains("`n")) {
            throw "Unsupported installer path: $relativePath"
        }

        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        [PSCustomObject]@{ Path = $relativePath; Line = "$hash|$relativePath" }
    } |
    Sort-Object -Property Path

$entries.Line | Set-Content -LiteralPath $manifestPath -Encoding ascii
Write-Host "Generated installer manifest with $($entries.Count) files: $manifestPath"
