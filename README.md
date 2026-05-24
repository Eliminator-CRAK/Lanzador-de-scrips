<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Documentacion tecnica del lanzador de scripts PowerShell. -->

# LanzadorScripts

| Campo | Valor |
|---|---|
| Tipo | WPF + WebView2 |
| Runtime | .NET 10 Windows x64 |
| Uso | Descubrimiento y ejecucion controlada de scripts PowerShell |
| Backend | Servidor HTTP local interno |
| Configuracion | `%AppData%\LanzadorScripts\configuracion.json` |

```mermaid
flowchart TD
    A[Aplicacion WPF] --> B[WebView2]
    B --> C[ClienteWeb embebido]
    C --> D[Servidor local]
    D --> E[Descubrimiento scripts]
    D --> F[Gestor ejecucion]
    D --> G[Permisos]
    F --> H[PowerShell]
    G --> I[permissions.json]
```

## Rutas

| Recurso | Ruta |
|---|---|
| Config usuario | `%AppData%\LanzadorScripts\configuracion.json` |
| Config equipo | `C:\ProgramData\LanzadorScripts\configuracion.json` |
| Tokens admin | `%AppData%\LanzadorScripts\Tokens` |
| Logs | `%LocalAppData%\LanzadorScripts\Logs` |
| Perfil WebView2 | `%LocalAppData%\LanzadorScripts\WebView2` |

## Configuracion

```json
{
  "RutaScripts": "\\\\MAD002MICROPRU\\REPO",
  "RutaPermisos": "PERMISOS\\\\permissions.json",
  "RutaLogs": "%LocalAppData%\\\\LanzadorScripts\\\\Logs",
  "MaximoEjecucionesParalelas": 5
}
```

## Publicacion

```powershell
dotnet publish .\LanzadorScripts.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\publicacion
```

## Requisitos

| Requisito | Valor |
|---|---|
| SO | Windows 10/11 Pro o Enterprise |
| PowerShell | 5.1 |
| WebView2 | Runtime instalado o `publicacion\WebView2Runtime` |
| Permisos | Administrador local |
| Politicas | GPO/AppLocker/WDAC permitiendo app y `powershell.exe` |
