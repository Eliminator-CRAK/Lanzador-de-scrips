// (Autor: Alex Roman)
// Descripcion: Documentacion de uso del lanzador de scripts PowerShell.

# LanzadorScripts

Aplicacion WPF con WebView2 que carga la interfaz original de `lanzador-de-scripts-pro` y ejecuta scripts desde un backend local sin abrir terminales externas.

## Configuracion

La aplicacion busca la configuracion en este orden:

1. `configuracion.json` junto al `.exe`.
2. `C:\ProgramData\LanzadorScripts\configuracion.json`

Ejemplo:

```json
{
  "RutaScripts": "\\\\MAD002MICROPRU\\REPO",
  "RutaPermisos": "PERMISOS\\\\permissions.json",
  "RutaLogs": "C:\\ProgramData\\LanzadorScripts\\Logs",
  "MaximoEjecucionesParalelas": 5
}
```

La ruta de scripts no se muestra ni se puede cambiar desde la interfaz.

## Permisos

Los scripts en la raiz de `RutaScripts` se pueden ejecutar con la aplicacion elevada.

Los scripts en subcarpetas requieren autorizacion en:

```text
<RutaScripts>\PERMISOS\permissions.json
```

Ejemplo:

```json
{
  "EjecutoresSubcarpetas": [
    "DOMINIO\\usuario1",
    "DOMINIO\\usuario2"
  ]
}
```

## Publicacion

Publicacion recomendada para equipos sin .NET instalado, en un solo `.exe`:

```powershell
dotnet publish .\LanzadorScripts.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\publicacion
```

El ejecutable queda en `publicacion\LanzadorScripts.exe`.

## Requisitos

- Windows 10/11 Pro o Enterprise.
- PowerShell 5.1.
- Microsoft Edge WebView2 Runtime.
- Acceso a la ruta configurada.
- UAC y permisos de administrador local.
- Politicas GPO/AppLocker/WDAC que permitan ejecutar la app y `powershell.exe`.
