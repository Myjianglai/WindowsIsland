param([ValidateSet('Claude', 'Codex')][string]$Source)
$ErrorActionPreference = 'Stop'
try {
    [Console]::InputEncoding = [Text.Encoding]::UTF8
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    if (!$payload.session_id) { return }
    $folder = $PSScriptRoot
    $request = ''
    $detail = ''
    $title = ''
    $project = if ($payload.cwd) { Split-Path -Leaf $payload.cwd } else { $Source }
    $session = [string]$payload.session_id
    $transcriptPath = [string]$payload.transcript_path
    $transcript = [IO.Path]::GetFileNameWithoutExtension($transcriptPath)
    if ($transcript -match '([0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})$') { $session = $Matches[1] }
    switch ($payload.hook_event_name) {
        'UserPromptSubmit' {
            $state = '运行中'
            $prompt = [string]$payload.prompt
            if ($prompt -match '(?s)^\s*# Files mentioned by the user:.*?## My request:\s*(.*)$') { $prompt = $Matches[1] }
            $prompt = ($prompt -replace '\s+', ' ').Trim()
            $prompt = $prompt.Substring(0, [Math]::Min(240, $prompt.Length))
            $title = "$project · $($prompt.Substring(0, [Math]::Min(32, $prompt.Length)))"
        }
        'Stop' { $state = '已完成' }
        'StopFailure' { $state = '出错' }
        'Interrupt' { $state = '已中断' }
        'PermissionRequest' {
            $state = '待确认'
            $approval = Join-Path $folder 'approval'
            $heartbeat = Join-Path $folder 'heartbeat'
            if ($payload.tool_name -and $payload.tool_input -and (Test-Path -LiteralPath $approval) -and
                ([IO.File]::ReadAllText($approval) -split ',') -contains $Source -and
                ([DateTime]::UtcNow - [IO.File]::GetLastWriteTimeUtc($heartbeat)).TotalSeconds -lt 5) {
                $request = [Guid]::NewGuid().ToString('N')
                $detail = "$($payload.tool_name) · $($payload.cwd)`n$($payload.tool_input | ConvertTo-Json -Depth 50)"
            }
        }
        default { return }
    }
    $hostProcess = $null
    $sessionProcess = $null
    try {
        $ancestry = @{}
        Get-CimInstance Win32_Process -Property ProcessId,ParentProcessId,Name,ExecutablePath | ForEach-Object {
            $ancestry[[int]$_.ProcessId] = $_
        }
        $ancestor = $ancestry[$PID]
        for ($depth = 0; $ancestor -and $depth -lt 15; $depth++) {
            if ($Source -eq 'Claude' -and !$sessionProcess -and
                ($ancestor.Name -eq 'claude.exe' -or $ancestor.Name -eq 'node.exe')) { $sessionProcess = $ancestor }
            if ($ancestor.Name -match '^(orca|WindowsTerminal|wezterm-gui|Code|Cursor|powershell|pwsh|cmd)\.exe$' -and
                [int]$ancestor.ProcessId -ne $PID -and (!$hostProcess -or $ancestor.Name -match '^(orca|WindowsTerminal|wezterm-gui|Code|Cursor)\.exe$')) {
                $hostProcess = $ancestor
                if ($ancestor.Name -eq 'orca.exe') { break }
            }
            $ancestor = $ancestry[[int]$ancestor.ParentProcessId]
        }
    } catch {}
    $orcaPane = if ($env:ORCA_PANE_KEY) { $env:ORCA_PANE_KEY } else { $env:ORCA_AGENT_PANE }
    $windowKey = if ($orcaPane) {
        "orca:$orcaPane"
    } elseif ($hostProcess -and $env:WT_SESSION -and $hostProcess.Name -ne 'orca.exe') {
        "terminal:$env:WT_SESSION"
    } elseif ($hostProcess.Name -eq 'orca.exe' -and $sessionProcess) {
        "orca:$($sessionProcess.ProcessId)"
    }
    $hostPath = $hostProcess.ExecutablePath
    $hostPid = [int]$hostProcess.ProcessId
    if ($orcaPane -and $hostProcess.Name -ne 'orca.exe') {
        $orcaApp = Get-Process Orca -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
        $hostPath = if ($orcaApp.Path) { $orcaApp.Path } else { Join-Path $env:LOCALAPPDATA 'Programs/orca/Orca.exe' }
        $hostPid = [int]$orcaApp.Id
    }
    $model = if ($payload.model -is [string]) { $payload.model } elseif ($payload.model.id) {
        [string]$payload.model.id
    } elseif ($payload.model.display_name) { [string]$payload.model.display_name }
    $effort = [string]$payload.effort
    $permission = [string]$payload.permission_mode
    if ($Source -eq 'Codex' -and $transcriptPath -and (Test-Path -LiteralPath $transcriptPath -PathType Leaf)) {
        try {
            $lines = @(Get-Content -LiteralPath $transcriptPath -Encoding UTF8 -Tail 200)
            for ($i = $lines.Count - 1; $i -ge 0; $i--) {
                if ($lines[$i] -notmatch '"type"\s*:\s*"turn_context"') { continue }
                $entry = $lines[$i] | ConvertFrom-Json
                if ($entry.type -ne 'turn_context') { continue }
                $context = $entry.payload
                if (!$model) { $model = [string]$context.model }
                if (!$effort) { $effort = [string]$context.effort }
                if ($context.approval_policy -is [string]) { $permission = $context.approval_policy }
                break
            }
        } catch {}
    }
    $answer = ([string]$payload.last_assistant_message -replace '\s+', ' ').Trim()
    if ($answer.Length -gt 240) { $answer = $answer.Substring(0, 240) }
    $id = [Guid]::NewGuid().ToString('N')
    $data = @{ Id = $session; Source = $Source; Title = $title; State = $state
        Detail = $detail; Request = $request; Time = [DateTimeOffset]::UtcNow.ToString('o')
        Model = [string]$model; Effort = $effort; Permission = $permission; TranscriptPath = $transcriptPath
        Tool = [string]$payload.tool_name; Prompt = [string]$prompt; Workspace = [string]$payload.cwd
        WindowKey = [string]$windowKey; HostPath = [string]$hostPath; HostPid = $hostPid
        Answer = $answer }
    $temp = Join-Path $folder "$id.tmp"
    [IO.File]::WriteAllText($temp, ($data | ConvertTo-Json -Compress))
    [IO.File]::Move($temp, (Join-Path $folder "$id.event"))
    if (!$request) { return }
    $reply = Join-Path $folder "$request.reply"
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 60) {
        if ($Source -notin ([IO.File]::ReadAllText($approval) -split ',') -or
            ([DateTime]::UtcNow - [IO.File]::GetLastWriteTimeUtc($heartbeat)).TotalSeconds -ge 5) { return }
        if (Test-Path -LiteralPath $reply) {
            $decision = [IO.File]::ReadAllText($reply)
            Remove-Item -LiteralPath $reply
            if ($decision -in @('allow', 'deny')) {
                $result = @{ behavior = $decision }
                if ($decision -eq 'deny') { $result.message = 'User denied the command in Windows Island.' }
                @{ hookSpecificOutput = @{ hookEventName = 'PermissionRequest'; decision = $result } } |
                    ConvertTo-Json -Depth 4 -Compress
            }
            return
        }
        Start-Sleep -Milliseconds 200
    }
} catch {
    # An unavailable island leaves the original tool permission flow in control.
    exit 0
}
