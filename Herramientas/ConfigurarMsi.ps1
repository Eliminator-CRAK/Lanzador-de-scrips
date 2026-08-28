# (Autor: Alex Roman)
# Descripcion: Completa y valida las tablas corporativas del MSI de LanzadorScripts.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RutaMsi,

    [Parameter(Mandatory = $true)]
    [string]$RutaHerramientaInstalador
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RutaArchivoValidada {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Ruta,

        [Parameter(Mandatory = $true)]
        [string]$Extension
    )

    $completa = [System.IO.Path]::GetFullPath($Ruta)
    if (-not [System.IO.File]::Exists($completa)) {
        throw "No existe el archivo requerido: $completa"
    }

    if (-not [System.IO.Path]::GetExtension($completa).Equals(
            $Extension,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "El archivo no tiene la extension esperada $Extension`: $completa"
    }

    $atributos = [System.IO.File]::GetAttributes($completa)
    if (($atributos -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "No se admiten enlaces ni puntos de reanalisis: $completa"
    }

    return $completa
}

function Invoke-ComMethod {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [object[]]$Arguments = @()
    )

    return $Object.GetType().InvokeMember(
        $Name,
        [System.Reflection.BindingFlags]::InvokeMethod,
        $null,
        $Object,
        $Arguments)
}

function Set-ComProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [object[]]$Arguments
    )

    $null = $Object.GetType().InvokeMember(
        $Name,
        [System.Reflection.BindingFlags]::SetProperty,
        $null,
        $Object,
        $Arguments)
}

function Get-ComProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Object,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [object[]]$Arguments = @()
    )

    return $Object.GetType().InvokeMember(
        $Name,
        [System.Reflection.BindingFlags]::GetProperty,
        $null,
        $Object,
        $Arguments)
}

function New-MsiRecord {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Installer,

        [AllowNull()]
        [object[]]$Values
    )

    $record = Invoke-ComMethod -Object $Installer -Name 'CreateRecord' -Arguments @($Values.Count)
    for ($indice = 0; $indice -lt $Values.Count; $indice++) {
        $valor = $Values[$indice]
        if ($null -eq $valor) {
            continue
        }

        if ($valor -is [byte] -or $valor -is [int16] -or $valor -is [int32] -or $valor -is [int64]) {
            Set-ComProperty -Object $record -Name 'IntegerData' -Arguments @(($indice + 1), [int]$valor)
        }
        else {
            Set-ComProperty -Object $record -Name 'StringData' -Arguments @(($indice + 1), [string]$valor)
        }
    }

    return $record
}

function Open-MsiView {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Sql
    )

    return Invoke-ComMethod -Object $Database -Name 'OpenView' -Arguments @($Sql)
}

function Invoke-MsiStatement {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Installer,

        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Sql,

        [object[]]$Values = @()
    )

    $view = Open-MsiView -Database $Database -Sql $Sql
    try {
        if ($Values.Count -eq 0) {
            $null = Invoke-ComMethod -Object $view -Name 'Execute'
        }
        else {
            $record = New-MsiRecord -Installer $Installer -Values $Values
            $null = Invoke-ComMethod -Object $view -Name 'Execute' -Arguments @($record)
        }
    }
    finally {
        $null = Invoke-ComMethod -Object $view -Name 'Close'
    }
}

function Get-MsiRows {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Sql,

        [Parameter(Mandatory = $true)]
        [string[]]$Columns
    )

    $view = Open-MsiView -Database $Database -Sql $Sql
    try {
        $null = Invoke-ComMethod -Object $view -Name 'Execute'
        while ($true) {
            $record = Invoke-ComMethod -Object $view -Name 'Fetch'
            if ($null -eq $record) {
                break
            }

            $row = [ordered]@{}
            for ($indice = 0; $indice -lt $Columns.Count; $indice++) {
                $row[$Columns[$indice]] = [string](Get-ComProperty `
                    -Object $record `
                    -Name 'StringData' `
                    -Arguments @(($indice + 1)))
            }

            [pscustomobject]$row
        }
    }
    finally {
        $null = Invoke-ComMethod -Object $view -Name 'Close'
    }
}

function Add-MsiRow {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Installer,

        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Table,

        [Parameter(Mandatory = $true)]
        [string[]]$Columns,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object[]]$Values
    )

    if ($Table -notmatch '^[A-Za-z][A-Za-z0-9_]*$' -or $Columns.Count -ne $Values.Count) {
        throw 'La fila MSI solicitada no es valida.'
    }

    foreach ($column in $Columns) {
        if ($column -notmatch '^[A-Za-z][A-Za-z0-9_]*$') {
            throw "La columna MSI no es valida: $column"
        }
    }

    $columnSql = ($Columns | ForEach-Object { "``$_``" }) -join ', '
    $parameterSql = ((1..$Values.Count) | ForEach-Object { '?' }) -join ', '
    Invoke-MsiStatement `
        -Installer $Installer `
        -Database $Database `
        -Sql "INSERT INTO ``$Table`` ($columnSql) VALUES ($parameterSql)" `
        -Values $Values
}

function Add-MsiBinary {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Installer,

        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $view = Open-MsiView -Database $Database -Sql 'INSERT INTO `Binary` (`Name`, `Data`) VALUES (?, ?)'
    try {
        $record = Invoke-ComMethod -Object $Installer -Name 'CreateRecord' -Arguments @(2)
        Set-ComProperty -Object $record -Name 'StringData' -Arguments @(1, $Name)
        $null = Invoke-ComMethod -Object $record -Name 'SetStream' -Arguments @(2, $Path)
        $null = Invoke-ComMethod -Object $view -Name 'Execute' -Arguments @($record)
    }
    finally {
        $null = Invoke-ComMethod -Object $view -Name 'Close'
    }
}

function Set-MsiProperty {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Installer,

        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Name -notmatch '^[A-Za-z][A-Za-z0-9_]*$') {
        throw "La propiedad MSI no es valida: $Name"
    }

    $exists = Get-MsiRows `
        -Database $Database `
        -Sql 'SELECT `Property` FROM `Property`' `
        -Columns @('Property') | Where-Object Property -eq $Name
    if ($null -ne $exists) {
        Invoke-MsiStatement `
            -Installer $Installer `
            -Database $Database `
            -Sql "UPDATE ``Property`` SET ``Value``=? WHERE ``Property``='$Name'" `
            -Values @($Value)
    }
    else {
        Add-MsiRow `
            -Installer $Installer `
            -Database $Database `
            -Table 'Property' `
            -Columns @('Property', 'Value') `
            -Values @($Name, $Value)
    }
}

function Add-Control {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Installer,

        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Dialog,

        [Parameter(Mandatory = $true)]
        [string]$Control,

        [Parameter(Mandatory = $true)]
        [string]$Property,

        [Parameter(Mandatory = $true)]
        [string]$Text,

        [Parameter(Mandatory = $true)]
        [int]$Y,

        [Parameter(Mandatory = $true)]
        [string]$Next
    )

    Add-MsiRow `
        -Installer $Installer `
        -Database $Database `
        -Table 'Control' `
        -Columns @('Dialog_', 'Control', 'Type', 'X', 'Y', 'Width', 'Height', 'Attributes', 'Property', 'Text', 'Control_Next') `
        -Values @($Dialog, $Control, 'CheckBox', 18, $Y, 330, 18, 3, $Property, $Text, $Next)
}

function Assert-MsiRow {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Rows,

        [Parameter(Mandatory = $true)]
        [string]$Column,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    if ($Rows.Where({ [string]$_.$Column -eq $Value }).Count -ne 1) {
        throw "La validacion del MSI no encontro exactamente una fila $Column=$Value."
    }
}

$msiPath = Get-RutaArchivoValidada -Ruta $RutaMsi -Extension '.msi'
$helperPath = Get-RutaArchivoValidada -Ruta $RutaHerramientaInstalador -Extension '.exe'
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = Invoke-ComMethod -Object $installer -Name 'OpenDatabase' -Arguments @($msiPath, 1)

try {
    $properties = @(Get-MsiRows `
        -Database $database `
        -Sql 'SELECT `Property`, `Value` FROM `Property`' `
        -Columns @('Property', 'Value'))
    if ($properties.Where({ $_.Property -eq 'LANZADOR_MSI_CONFIGURADO' }).Count -ne 0) {
        throw 'El MSI ya fue configurado. Debe partir de una compilacion nueva del proyecto vdproj.'
    }

    $files = @(Get-MsiRows `
        -Database $database `
        -Sql 'SELECT `File`, `Component_`, `FileName`, `Version` FROM `File`' `
        -Columns @('File', 'Component', 'FileName', 'Version'))
    $appFiles = @($files | Where-Object {
        ($_.FileName -split '\|')[-1].Equals('LanzadorScripts.exe', [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($appFiles.Count -ne 1 -or $appFiles[0].Version -ne '1.8.4.0') {
        throw 'El MSI no contiene exactamente un LanzadorScripts.exe con version 1.8.4.0.'
    }

    $features = @(Get-MsiRows `
        -Database $database `
        -Sql 'SELECT `Feature` FROM `Feature`' `
        -Columns @('Feature'))
    if ($features.Count -ne 1) {
        throw 'El MSI debe contener una unica caracteristica principal.'
    }

    $appFile = $appFiles[0].File
    $feature = $features[0].Feature
    Add-MsiBinary -Installer $installer -Database $database -Name 'LanzadorInstallerHelper' -Path $helperPath

    Add-MsiRow -Installer $installer -Database $database -Table 'CustomAction' -Columns @('Action', 'Type', 'Source', 'Target') -Values @('LS_CheckClose', 2, 'LanzadorInstallerHelper', '--comprobar-cierre [UILevel]')
    Add-MsiRow -Installer $installer -Database $database -Table 'CustomAction' -Columns @('Action', 'Type', 'Source', 'Target') -Values @('LS_Migrate16', 3074, 'LanzadorInstallerHelper', '--migrar-1.6')
    Add-MsiRow -Installer $installer -Database $database -Table 'CustomAction' -Columns @('Action', 'Type', 'Source', 'Target') -Values @('LS_Cleanup', 3074, 'LanzadorInstallerHelper', '--limpiar-desinstalacion')
    Add-MsiRow -Installer $installer -Database $database -Table 'CustomAction' -Columns @('Action', 'Type', 'Source', 'Target') -Values @('LS_Launch', 210, $appFile, $null)

    Add-MsiRow -Installer $installer -Database $database -Table 'InstallExecuteSequence' -Columns @('Action', 'Condition', 'Sequence') -Values @('LS_CheckClose', 'NOT PATCH AND ACTION <> "ADMIN"', 1450)
    Add-MsiRow -Installer $installer -Database $database -Table 'InstallExecuteSequence' -Columns @('Action', 'Condition', 'Sequence') -Values @('LS_Migrate16', 'NOT Installed AND NOT REMOVE~="ALL" AND NOT PATCH AND ACTION <> "ADMIN"', 1510)
    Add-MsiRow -Installer $installer -Database $database -Table 'InstallExecuteSequence' -Columns @('Action', 'Condition', 'Sequence') -Values @('LS_Cleanup', 'REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE AND ACTION <> "ADMIN"', 3650)

    Add-MsiRow -Installer $installer -Database $database -Table 'Directory' -Columns @('Directory', 'Directory_Parent', 'DefaultDir') -Values @('LanzadorScriptsMenuFolder', 'ProgramMenuFolder', 'LANZAD~1|LanzadorScripts')

    Add-MsiRow -Installer $installer -Database $database -Table 'Component' -Columns @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') -Values @('CmpStartMenuShortcut', '{89BC6217-748A-4ED7-9F36-8251365B80D4}', 'LanzadorScriptsMenuFolder', 260, $null, 'RegStartMenuKey')
    Add-MsiRow -Installer $installer -Database $database -Table 'Component' -Columns @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') -Values @('CmpDesktopShortcut', '{56715FE3-10A5-4778-A0BE-E6917F5A832A}', 'DesktopFolder', 260, 'CREATE_DESKTOP_SHORTCUT=1', 'RegDesktopKey')
    Add-MsiRow -Installer $installer -Database $database -Table 'Component' -Columns @('Component', 'ComponentId', 'Directory_', 'Attributes', 'Condition', 'KeyPath') -Values @('CmpFileAssociation', '{81D1C96B-0F10-4E8C-A9CB-0F94CB282077}', 'TARGETDIR', 260, $null, 'RegAssociationKey')

    foreach ($component in @('CmpStartMenuShortcut', 'CmpDesktopShortcut', 'CmpFileAssociation')) {
        Add-MsiRow -Installer $installer -Database $database -Table 'FeatureComponents' -Columns @('Feature_', 'Component_') -Values @($feature, $component)
    }

    Add-MsiRow -Installer $installer -Database $database -Table 'Registry' -Columns @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') -Values @('RegStartMenuKey', 2, 'SOFTWARE\LanzadorScripts\Installer', 'StartMenuShortcut', '[ProductCode]', 'CmpStartMenuShortcut')
    Add-MsiRow -Installer $installer -Database $database -Table 'Registry' -Columns @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') -Values @('RegDesktopKey', 2, 'SOFTWARE\LanzadorScripts\Installer', 'DesktopShortcut', '[ProductCode]', 'CmpDesktopShortcut')
    Add-MsiRow -Installer $installer -Database $database -Table 'Registry' -Columns @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') -Values @('RegAssociationKey', 0, '.lanzadorconfig', $null, 'LanzadorScripts.Configuracion', 'CmpFileAssociation')
    Add-MsiRow -Installer $installer -Database $database -Table 'Registry' -Columns @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') -Values @('RegAssociationDescription', 0, 'LanzadorScripts.Configuracion', $null, 'Paquete de configuracion de LanzadorScripts', 'CmpFileAssociation')
    Add-MsiRow -Installer $installer -Database $database -Table 'Registry' -Columns @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') -Values @('RegAssociationIcon', 0, 'LanzadorScripts.Configuracion\DefaultIcon', $null, ('"[#' + $appFile + ']",0'), 'CmpFileAssociation')
    Add-MsiRow -Installer $installer -Database $database -Table 'Registry' -Columns @('Registry', 'Root', 'Key', 'Name', 'Value', 'Component_') -Values @('RegAssociationCommand', 0, 'LanzadorScripts.Configuracion\shell\open\command', $null, ('"[#' + $appFile + ']" "%1"'), 'CmpFileAssociation')

    Add-MsiRow -Installer $installer -Database $database -Table 'Shortcut' -Columns @('Shortcut', 'Directory_', 'Name', 'Component_', 'Target', 'Arguments', 'Description', 'ShowCmd', 'WkDir') -Values @('LanzadorScriptsStartMenu', 'LanzadorScriptsMenuFolder', 'LANZAD~1|LanzadorScripts', 'CmpStartMenuShortcut', ('[#' + $appFile + ']'), $null, 'Abrir LanzadorScripts', 1, 'TARGETDIR')
    Add-MsiRow -Installer $installer -Database $database -Table 'Shortcut' -Columns @('Shortcut', 'Directory_', 'Name', 'Component_', 'Target', 'Arguments', 'Description', 'ShowCmd', 'WkDir') -Values @('LanzadorScriptsDesktop', 'DesktopFolder', 'LANZAD~1|LanzadorScripts', 'CmpDesktopShortcut', ('[#' + $appFile + ']'), $null, 'Abrir LanzadorScripts', 1, 'TARGETDIR')
    Add-MsiRow -Installer $installer -Database $database -Table 'CreateFolder' -Columns @('Directory_', 'Component_') -Values @('LanzadorScriptsMenuFolder', 'CmpStartMenuShortcut')
    Add-MsiRow -Installer $installer -Database $database -Table 'RemoveFile' -Columns @('FileKey', 'Component_', 'FileName', 'DirProperty', 'InstallMode') -Values @('RemoveLanzadorMenuFolder', 'CmpStartMenuShortcut', $null, 'LanzadorScriptsMenuFolder', 2)

    Add-MsiRow -Installer $installer -Database $database -Table 'CheckBox' -Columns @('Property', 'Value') -Values @('CREATE_DESKTOP_SHORTCUT', '1')
    foreach ($dialog in @('ConfirmInstallForm', 'AdminConfirmInstallForm')) {
        Add-Control -Installer $installer -Database $database -Dialog $dialog -Control 'DesktopShortcutCheck' -Property 'CREATE_DESKTOP_SHORTCUT' -Text 'Crear acceso directo en el escritorio' -Y 205 -Next 'Line2'
        Invoke-MsiStatement -Installer $installer -Database $database -Sql "UPDATE ``Control`` SET ``Control_Next``='DesktopShortcutCheck', ``Height``=130 WHERE ``Dialog_``='$dialog' AND ``Control``='BodyText1'"
    }

    Add-MsiRow -Installer $installer -Database $database -Table 'CheckBox' -Columns @('Property', 'Value') -Values @('LAUNCH_LANZADORSCRIPTS', '1')
    Add-Control -Installer $installer -Database $database -Dialog 'FinishedForm' -Control 'LaunchLanzadorCheck' -Property 'LAUNCH_LANZADORSCRIPTS' -Text 'Abrir LanzadorScripts' -Y 205 -Next 'Line2'
    Invoke-MsiStatement -Installer $installer -Database $database -Sql "UPDATE ``Control`` SET ``Control_Next``='LaunchLanzadorCheck' WHERE ``Dialog_``='FinishedForm' AND ``Control``='UpdateText'"
    Add-MsiRow -Installer $installer -Database $database -Table 'ControlCondition' -Columns @('Dialog_', 'Control_', 'Action', 'Condition') -Values @('FinishedForm', 'LaunchLanzadorCheck', 'Hide', 'Installed OR REMOVE~="ALL"')
    Invoke-MsiStatement -Installer $installer -Database $database -Sql "UPDATE ``ControlEvent`` SET ``Ordering``=1 WHERE ``Dialog_``='FinishedForm' AND ``Control_``='CloseButton' AND ``Event``='EndDialog'"
    Add-MsiRow -Installer $installer -Database $database -Table 'ControlEvent' -Columns @('Dialog_', 'Control_', 'Event', 'Argument', 'Condition', 'Ordering') -Values @('FinishedForm', 'CloseButton', 'DoAction', 'LS_Launch', 'LAUNCH_LANZADORSCRIPTS=1 AND NOT Installed AND NOT REMOVE~="ALL" AND ACTION <> "ADMIN"', 0)

    Set-MsiProperty -Installer $installer -Database $database -Name 'ALLUSERS' -Value '1'
    Set-MsiProperty -Installer $installer -Database $database -Name 'FolderForm_AllUsersVisible' -Value '0'
    $secureValue = @(
        (($properties | Where-Object Property -eq 'SecureCustomProperties').Value -split ';')
        'CREATE_DESKTOP_SHORTCUT'
        'LAUNCH_LANZADORSCRIPTS'
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    Set-MsiProperty -Installer $installer -Database $database -Name 'SecureCustomProperties' -Value ($secureValue -join ';')
    Set-MsiProperty -Installer $installer -Database $database -Name 'LANZADOR_MSI_CONFIGURADO' -Value '1'

    $null = Invoke-ComMethod -Object $database -Name 'Commit'
}
finally {
    if ($null -ne $database) {
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
    }

    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}

[System.GC]::Collect()
[System.GC]::WaitForPendingFinalizers()
[System.GC]::Collect()

$validationInstaller = New-Object -ComObject WindowsInstaller.Installer
$validationDatabase = Invoke-ComMethod -Object $validationInstaller -Name 'OpenDatabase' -Arguments @($msiPath, 0)
try {
    $validationActions = @(Get-MsiRows -Database $validationDatabase -Sql 'SELECT `Action` FROM `CustomAction`' -Columns @('Action'))
    $validationComponents = @(Get-MsiRows -Database $validationDatabase -Sql 'SELECT `Component`, `Directory_` FROM `Component`' -Columns @('Component', 'Directory'))
    $validationDirectories = @(Get-MsiRows -Database $validationDatabase -Sql 'SELECT `Directory` FROM `Directory`' -Columns @('Directory'))
    $validationFiles = @(Get-MsiRows -Database $validationDatabase -Sql 'SELECT `File`, `Component_`, `FileName` FROM `File`' -Columns @('File', 'Component', 'FileName'))
    $validationProperties = @(Get-MsiRows -Database $validationDatabase -Sql 'SELECT `Property`, `Value` FROM `Property`' -Columns @('Property', 'Value'))
    foreach ($action in @('LS_CheckClose', 'LS_Migrate16', 'LS_Cleanup', 'LS_Launch')) {
        Assert-MsiRow -Rows $validationActions -Column 'Action' -Value $action
    }

    foreach ($component in @('CmpStartMenuShortcut', 'CmpDesktopShortcut', 'CmpFileAssociation')) {
        Assert-MsiRow -Rows $validationComponents -Column 'Component' -Value $component
    }

    $directoryNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($directory in $validationDirectories) {
        $null = $directoryNames.Add([string]$directory.Directory)
    }

    $orphanComponents = @($validationComponents | Where-Object {
        -not $directoryNames.Contains([string]$_.Directory)
    })
    if ($orphanComponents.Count -ne 0) {
        throw "El MSI contiene componentes asociados a directorios inexistentes: $($orphanComponents.Component -join ', ')."
    }

    $webViewLoaders = @($validationFiles | Where-Object {
        (($_.FileName -split '\|')[-1]).Equals('WebView2Loader.dll', [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($webViewLoaders.Count -ne 1) {
        throw 'El MSI debe contener una unica WebView2Loader.dll.'
    }

    $loaderComponent = @($validationComponents | Where-Object {
        $_.Component -eq $webViewLoaders[0].Component
    })
    if ($loaderComponent.Count -ne 1 -or $loaderComponent.Directory -ne 'TARGETDIR') {
        throw 'WebView2Loader.dll debe instalarse en la raiz de LanzadorScripts.'
    }

    $allUsers = $validationProperties | Where-Object Property -eq 'ALLUSERS'
    if ($allUsers.Count -ne 1 -or $allUsers.Value -ne '1') {
        throw 'El MSI no quedo configurado para todos los usuarios.'
    }
}
finally {
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($validationDatabase) | Out-Null
    [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($validationInstaller) | Out-Null
}

Write-Host "MSI configurado y validado: $msiPath"
