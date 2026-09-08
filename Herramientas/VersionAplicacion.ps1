# (Autor: Alex Roman)
# Descripcion: Lee la version unica de LanzadorScripts desde Directory.Build.props.

function Get-LanzadorScriptsVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Raiz
    )

    $ruta = [System.IO.Path]::GetFullPath((Join-Path $Raiz 'Directory.Build.props'))
    if (-not [System.IO.File]::Exists($ruta)) {
        throw "No se encontro la fuente de version: $ruta"
    }

    $documento = [xml][System.IO.File]::ReadAllText($ruta)
    $grupo = @($documento.Project.PropertyGroup) |
        Where-Object {
            -not [string]::IsNullOrWhiteSpace($_.LanzadorScriptsVersion) -and
            -not [string]::IsNullOrWhiteSpace($_.LanzadorScriptsFileVersion)
        } |
        Select-Object -First 1
    $producto = [string]$grupo.LanzadorScriptsVersion
    $archivo = [string]$grupo.LanzadorScriptsFileVersion
    if ($producto -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$' -or
        $archivo -ne "$producto.0") {
        throw 'Directory.Build.props contiene una version no valida.'
    }

    [pscustomobject]@{
        Producto = $producto
        Archivo = $archivo
        Etiqueta = "v$producto"
        NombreMsi = "LanzadorScripts-$producto-x64.msi"
        NombrePortable = "LanzadorScripts_Portable-$producto-x64.exe"
        NombreServidor = "LanzadorScripts_Servidor-$producto-x64.zip"
    }
}
