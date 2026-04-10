# ExplorerHelper PowerShell Module
# Provides Taskbar, QuickAccess, and Explorer functions for Windows shell automation

# Load the C# class library
$dllPath = Join-Path $PSScriptRoot "ExplorerHelper.dll"
if (-not (Test-Path $dllPath)) {
    throw "ExplorerHelper.dll not found at $dllPath. Build the C# project first."
}
Add-Type -Path $dllPath


function Taskbar {
    <#
    .SYNOPSIS
    Manages Windows taskbar pinned applications.

    .DESCRIPTION
    Provides list, pin, unpin, unpinall, snapshot, and apply actions for taskbar management.

    .EXAMPLE
    Taskbar list

    .EXAMPLE
    Taskbar pin "Chrome" -Apply $false

    .EXAMPLE
    Taskbar apply -Order "Outlook, Chrome, File Explorer"
    #>
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateSet("list", "pin", "unpin", "unpinall", "snapshot", "apply")]
        [string]$Action,

        [Parameter(Position = 1)]
        [string]$Target,

        [string]$Apps,
        [string]$Order,
        [bool]$Apply = $true
    )

    switch ($Action) {
        "list" {
            [ExplorerHelper.Commands.TaskbarCommands]::List() | Out-Null
        }
        "pin" {
            $pinTarget = if ($Apps) { $Apps } elseif ($Target) { $Target } else { throw "Taskbar pin requires an app name or -Apps parameter." }
            [ExplorerHelper.Commands.TaskbarCommands]::Pin($pinTarget, $Apply) | Out-Null
        }
        "unpin" {
            if (-not $Target) { throw "Taskbar unpin requires an app name." }
            [ExplorerHelper.Commands.TaskbarCommands]::Unpin($Target) | Out-Null
        }
        "unpinall" {
            [ExplorerHelper.Commands.TaskbarCommands]::UnpinAllCommand() | Out-Null
        }
        "snapshot" {
            [ExplorerHelper.Commands.TaskbarCommands]::Snapshot($Target) | Out-Null
        }
        "apply" {
            # Target can be: a file path, "true", "false", or empty
            if ($Target -eq "false") {
                Write-Host "Taskbar: Apply skipped."
            }
            elseif ($Target -and ($Target -ne "true") -and (Test-Path $Target -ErrorAction SilentlyContinue)) {
                [ExplorerHelper.Commands.TaskbarCommands]::Apply($Target, $Order) | Out-Null
            }
            else {
                [ExplorerHelper.Commands.TaskbarCommands]::Apply($null, $Order) | Out-Null
            }
        }
    }
}


function QuickAccess {
    <#
    .SYNOPSIS
    Manages Windows Explorer Quick Access pinned folders.

    .DESCRIPTION
    Provides list, pin, unpin, snapshot, and apply actions for Quick Access management.

    .EXAMPLE
    QuickAccess list

    .EXAMPLE
    QuickAccess pin "D:\Temp" -RestartExplorer $false

    .EXAMPLE
    QuickAccess snapshot "D:\Temp\qa_backup.xml"
    #>
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateSet("list", "pin", "unpin", "snapshot", "apply")]
        [string]$Action,

        [Parameter(Position = 1)]
        [string]$Target,

        [string]$Paths,
        [bool]$RestartExplorer = $true
    )

    switch ($Action) {
        "list" {
            [ExplorerHelper.Commands.QuickAccessCommands]::List() | Out-Null
        }
        "pin" {
            $pinTarget = if ($Paths) { $Paths } elseif ($Target) { $Target } else { throw "QuickAccess pin requires a path or -Paths parameter." }
            [ExplorerHelper.Commands.QuickAccessCommands]::Pin($pinTarget, $RestartExplorer) | Out-Null
        }
        "unpin" {
            if (-not $Target) { throw "QuickAccess unpin requires a folder path." }
            [ExplorerHelper.Commands.QuickAccessCommands]::Unpin($Target) | Out-Null
        }
        "snapshot" {
            [ExplorerHelper.Commands.QuickAccessCommands]::Snapshot($Target) | Out-Null
        }
        "apply" {
            if (-not $Target) { throw "QuickAccess apply requires a snapshot XML path." }
            [ExplorerHelper.Commands.QuickAccessCommands]::Apply($Target, $RestartExplorer) | Out-Null
        }
    }
}


function Explorer {
    <#
    .SYNOPSIS
    Manages Windows Explorer process.

    .DESCRIPTION
    Restarts Windows Explorer to apply shell changes.

    .EXAMPLE
    Explorer restart

    .EXAMPLE
    Explorer restart $false
    #>
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateSet("restart")]
        [string]$Action,

        [Parameter(Position = 1)]
        [bool]$Enabled = $true
    )

    switch ($Action) {
        "restart" {
            [ExplorerHelper.Commands.ExplorerCommands]::Restart($Enabled) | Out-Null
        }
    }
}


function ExplorerHelper {
    <#
    .SYNOPSIS
    Shows ExplorerHelper module version.
    #>
    param(
        [Parameter(Mandatory, Position = 0)]
        [ValidateSet("version")]
        [string]$Action
    )

    switch ($Action) {
        "version" {
            $mod = Get-Module ExplorerHelper
            Write-Host "ExplorerHelper v$($mod.Version)"
        }
    }
}


Export-ModuleMember -Function Taskbar, QuickAccess, Explorer, ExplorerHelper
