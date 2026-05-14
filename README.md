// (Autor: Alex Roman)
// Descripcion: Documentacion de uso del lanzador de scripts PowerShell.

# LanzadorScripts

Aplicacion WPF para ejecutar scripts `.ps1` desde una carpeta configurada por administracion sin abrir terminales externas.

## Configuracion

La configuracion se guarda en:

```text
C:\ProgramData\LanzadorScripts\appsettings.json
```

Ejemplo:

```json
{
  "RutaScripts": "\\\\MAD002MICROPRU\\REPO",
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

Publicacion recomendada para equipos sin .NET instalado:

```powershell
dotnet publish .\LanzadorScripts.csproj -c Release -r win-x64 --self-contained true
```

## Requisitos

- Windows 10/11 Pro o Enterprise.
- PowerShell 5.1.
- Acceso a la ruta configurada.
- UAC y permisos de administrador local.
- Politicas GPO/AppLocker/WDAC que permitan ejecutar la app y `powershell.exe`.
