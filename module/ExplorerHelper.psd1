@{
    RootModule        = 'ExplorerHelper.psm1'
    ModuleVersion     = '2.0.0'
    GUID              = 'a3b7c8d9-e4f5-6a7b-8c9d-0e1f2a3b4c5d'
    Author            = 'Robin Alden'
    Description       = 'Windows shell automation - taskbar pinning, Quick Access management, Explorer restart'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Taskbar', 'QuickAccess', 'Explorer', 'ExplorerHelper')
    CmdletsToExport   = @()
    VariablesToExport  = @()
    AliasesToExport    = @()
}
