param(
    [string]$OutputPath = (Join-Path $env:LOCALAPPDATA 'WindowsIsland\claude-status.json')
)

# Claude Code 通过 stdin 提供状态 JSON。只落盘额度字段。
$raw = $input -join [Environment]::NewLine
if ([string]::IsNullOrWhiteSpace($raw) -and [Console]::IsInputRedirected) {
    $raw = [Console]::In.ReadToEnd()
}
try {
    $inputState = ConvertFrom-Json -InputObject $raw -ErrorAction Stop
    $windows = [ordered]@{}
    foreach ($name in @('five_hour', 'seven_day', 'spend_limit')) {
        $window = $inputState.rate_limits.$name
        if ($null -ne $window) {
            $windows[$name] = [ordered]@{
                used_percentage = $window.used_percentage
                resets_at = $window.resets_at
            }
        }
    }
    if ($windows.Count -gt 0) {
        $destination = [IO.Path]::GetFullPath($OutputPath)
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
        $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        $backup = $temporary + '.backup'
        try {
            $status = ConvertTo-Json -InputObject @{ rate_limits = $windows } -Depth 4 -Compress
            [IO.File]::WriteAllText($temporary, $status, [Text.UTF8Encoding]::new($false))
            if ([IO.File]::Exists($destination)) {
                [IO.File]::Replace($temporary, $destination, $backup)
            } else {
                [IO.File]::Move($temporary, $destination)
            }
        } finally {
            [IO.File]::Delete($temporary)
            [IO.File]::Delete($backup)
        }
    }
    if ($null -ne $windows['five_hour'] -and $null -ne $windows['five_hour'].used_percentage) {
        Write-Output ('Claude | 5h {0}% used' -f $windows['five_hour'].used_percentage)
    } else {
        Write-Output 'Claude'
    }
} catch {
    Write-Output 'Claude'
}
