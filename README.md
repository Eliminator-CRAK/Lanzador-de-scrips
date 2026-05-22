// (Autor: Alex Roman)
// Descripcion: Documentacion de uso del lanzador de scripts PowerShell.

# LanzadorScripts

Aplicacion WPF con WebView2 que carga la interfaz original de `lanzador-de-scripts-pro` y ejecuta scripts desde un backend local sin abrir terminales externas.

## Configuracion

La aplicacion busca la configuracion en este orden:

1. `%AppData%\LanzadorScripts\configuracion.json`
2. `C:\ProgramData\LanzadorScripts\configuracion.json`
3. `configuracion.json` junto al `.exe` solo como migracion legada.

Ejemplo:

```json
{
  "RutaScripts": "\\\\MAD002MICROPRU\\REPO",
  "RutaPermisos": "PERMISOS\\\\permissions.json",
  "RutaLogs": "%LocalAppData%\\\\LanzadorScripts\\\\Logs",
  "MaximoEjecucionesParalelas": 5
}
```

La ruta de scripts no se muestra ni se puede cambiar desde la interfaz.

## Datos locales

La aplicacion guarda datos locales fuera de la carpeta del ejecutable:

- Configuracion de usuario: `%AppData%\LanzadorScripts\configuracion.json`.
- Tokens admin cifrados por usuario: `%AppData%\LanzadorScripts\Tokens`.
- Logs de ejecucion: `%LocalAppData%\LanzadorScripts\Logs`.
- Perfil WebView2: `%LocalAppData%\LanzadorScripts\WebView2`.

El token admin se genera solo para usuarios con rol `admin` y se cifra con DPAPI para el usuario Windows actual.

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

## WebView2 portable

La aplicacion usa el WebView2 Runtime instalado en Windows si no hay runtime fijo.

Para distribuir WebView2 junto a la aplicacion, coloca la version fija de Microsoft Edge WebView2 Runtime en:

```text
publicacion\WebView2Runtime
```

Esa carpeta debe contener `msedgewebview2.exe`. Si existe, la aplicacion usara ese runtime y no dependera del runtime instalado en el equipo.

## Requisitos

- Windows 10/11 Pro o Enterprise.
- PowerShell 5.1.
- Microsoft Edge WebView2 Runtime instalado o carpeta `WebView2Runtime` junto al `.exe`.
- Acceso a la ruta configurada.
- UAC y permisos de administrador local.
- Politicas GPO/AppLocker/WDAC que permitan ejecutar la app y `powershell.exe`.
