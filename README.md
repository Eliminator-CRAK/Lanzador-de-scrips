<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Documentacion tecnica del lanzador de scripts PowerShell. -->

# LanzadorScripts

| Campo | Valor |
|---|---|
| Version | 1.4.6 |
| Tipo | WPF + WebView2 |
| Runtime | .NET 10 Windows x64 |
| Uso | Descubrimiento y ejecucion controlada de scripts PowerShell |
| Backend | Proceso integrado en el ejecutable |
| Configuracion | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\configuracion.dat` |

```mermaid
flowchart TD
    A[Lanzador nativo con elevacion UAC] --> B[Runtime .NET embebido]
    B --> C[Aplicacion WPF]
    C --> D[WebView2]
    D --> E[ClienteWeb embebido]
    E --> F[Backend integrado]
    F --> G[Descubrimiento scripts]
    F --> H[Gestor ejecucion]
    F --> I[Permisos]
    H --> J[PowerShell]
    I --> K[permisos.json cifrado]
    I --> L[catalogo-scripts.json cifrado]
```

## Rutas

| Recurso | Ruta |
|---|---|
| Config usuario | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\configuracion.dat` |
| Tokens admin | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Tokens` |
| Logs | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Logs` |
| Auditoria | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Auditoria` |
| Clave de artefactos | `%ProgramData%\LanzadorScripts\Seguridad\artefactos.key` |
| Perfil WebView2 | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\WebView2\Perfil` |
| Temporales de proceso | `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Temporales` |
| Aplicacion .NET interna | `%ProgramFiles%\LanzadorScripts\Aplicacion\runtime-<hash>` |
| Extraccion nativa .NET | `%ProgramFiles%\LanzadorScripts\Runtimes\DotNet\runtime-<hash>` |
| Runtime WebView2 principal | `%ProgramFiles%\LanzadorScripts\Runtimes\WebView2` |
| Staging ejecucion | `%ProgramFiles%\LanzadorScripts\Staging` |

## Configuracion

```json
{
  "RutaScripts": "\\\\MAD002MICROPRU.mad.ae.aena.es\\R$\\SCRIPS",
  "RutaPermisos": "\\\\MAD002MICROPRU.mad.ae.aena.es\\R$\\PERMISOS",
  "RutaLogs": "%ProgramData%\\\\LanzadorScripts\\\\Usuarios\\\\<id-SID>\\\\Logs",
  "MaximoEjecucionesParalelas": 5
}
```

Las rutas antiguas que apuntaban directamente a `permisos.json` se migran a la carpeta de permisos operativa.

## Publicacion

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>"
```

El proceso usa WebView2 Fixed Version Runtime x64 `150.0.4078.48`, valida su version, arquitectura, firma y hashes, genera un ZIP reproducible y lo embebe como recurso dentro del EXE. Tambien crea un lanzador nativo x64 que contiene el runtime .NET firmado. Ese lanzador fija `DOTNET_BUNDLE_EXTRACT_BASE_DIR`, `TEMP` y `TMP` antes de iniciar .NET, por lo que el proceso no depende de `%LocalAppData%\Temp\.net`. La publicacion exige `pwsh 7.6.x` y las herramientas C++ x64 de Visual Studio. No instala servicios, certificados, cuentas, tareas ni puertos en los equipos cliente.

Para firmar el EXE final, usar:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>"
```

Tambien se puede usar `-CertPath` y `-CertPassword` con un certificado PFX. Si no se indica certificado, el script bloquea la publicacion salvo que se use `-AllowUnsignedForDev` para pruebas locales.

La carpeta `publicacion` contiene unicamente `LanzadorScripts.exe`. `permisos.json` y `catalogo-scripts.json` permanecen siempre en `\\MAD002MICROPRU.mad.ae.aena.es\R$\PERMISOS`.

El parametro `-RutaRuntimeWebView2Portable` permite usar una carpeta de Fixed Runtime ya descargada como origen local. Si no se indica, la publicacion descarga la URL oficial fijada de `150.0.4078.48`, guarda la cache en `Recursos\WebView2` y deja esa cache fuera de Git.

La inicializacion explicita de ambos archivos operativos se realiza con:

```powershell
.\Herramientas\PublicarPortable.ps1 -CertThumbprint "<THUMBPRINT>" -InicializarArtefactos
```

Antes de inicializar, la clave AES debe aprovisionarse en una consola administrativa:

```powershell
powershell.exe -NoProfile -File .\Herramientas\AprovisionarClaveArtefactos.ps1
```

El script solicita la clave de 32 bytes en Base64 mediante entrada segura, la protege con DPAPI `LocalMachine` y aplica una ACL limitada a `SYSTEM` y `Administrators`. La clave no se acepta como argumento.

La clave se crea una sola vez en un gestor de secretos corporativo y debe ser identica en todos los equipos que lean los mismos contenedores. No genere una clave diferente para corregir el aviso en un cliente: primero aprovisione la clave compartida y despues regenere o migre `permisos.json` y `catalogo-scripts.json` desde un equipo que tenga el certificado privado de artefactos.

Para actualizar una instalacion anterior, haga copia de `permisos.json` y `catalogo-scripts.json`, exporte la configuracion con la version anterior, aprovisione la misma clave AES en cada equipo autorizado e importe la configuracion con la version nueva. Los dos JSON v2 son contenedores cifrados: no se editan directamente con un editor de texto. Cualquier cambio en un script exige volver a publicar el catalogo.

La publicacion final debe ser self-contained, de un unico EXE y x64. El EXE exterior comprueba la huella SHA-256 del componente .NET embebido, lo reutiliza solo si coincide y lo ejecuta desde `Program Files`. Si un equipo muestra un error de .NET Desktop Runtime faltante al abrir el portable, la publicacion no es valida o se esta ejecutando un binario incorrecto.

GitLab (`micro2822131/Lanzador-de-scrips`) y GitHub (`Eliminator-CRAK/Lanzador-de-scrips`) mantienen el mismo historial de `main`. Cada cambio se publica y verifica en ambos remotos.

Semgrep Managed Scans analiza ambos repositorios con las 2944 reglas disponibles, analisis entre archivos, Code, Supply Chain y deteccion con IA. Los flujos de GitLab y GitHub complementan esa cobertura en cada `push`, PR/MR, ejecucion manual y revision diaria con `auto`, `p/security-audit` y `p/secrets`. Como Semgrep Secrets no esta incluido en el plan actual, Gitleaks 8.30.1 revisa ademas todo el historial Git, archivos comprimidos y valores codificados. No se aplican exclusiones de `.gitignore` y los CI fallan ante cualquier hallazgo nuevo o error de configuracion.

La unica excepcion de hallazgo Semgrep corresponde a un falso positivo de baja confianza sobre `AnimatePresence` de Framer Motion en el bundle compilado. El parser actual tampoco comprende por completo cuatro archivos que usan literales raw, un constructor primario o JavaScript minificado. `Herramientas/ValidarResultadosSemgrep.py` exige que cada hallazgo, error y omision coincida con su regla o tipo, ruta, lineas y SHA-256 del archivo completo. Si uno de esos archivos cambia, el CI obliga a revisar de nuevo la excepcion y mantiene bloqueado cualquier problema distinto.

El workflow de publicacion en GitHub actua como respaldo para compilacion, pruebas y publicacion Windows. Sus acciones estan fijadas a commits inmutables. Instala PowerShell `7.6.0` desde la publicacion oficial, valida su SHA-256 y exige firma Authenticode en `main` mediante los secretos `WINDOWS_SIGNING_CERT_BASE64` y `WINDOWS_SIGNING_CERT_PASSWORD`.

## Recuperacion WebView2

La aplicacion extrae WebView2 Fixed Runtime x64 `150.0.4078.48` en `%ProgramFiles%\LanzadorScripts\Runtimes\WebView2\<hash-version>`. La extraccion solo se reutiliza cuando coinciden el hash del ZIP, el ejecutable y la huella completa de sus 260 archivos; una copia local alterada se sustituye automaticamente. Un bloqueo explicito de WDAC o AppLocker debe autorizarse mediante la politica corporativa.

Cada extraccion concede lectura y ejecucion a `ALL APPLICATION PACKAGES` y `ALL RESTRICTED APPLICATION PACKAGES`, requeridos por el aislamiento de WebView2 Fixed Runtime. Los usuarios normales no reciben permisos de escritura sobre los binarios.

La aplicacion usa `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\WebView2\Perfil` como perfil local de WebView2. El identificador se obtiene del hash completo del SID; las raices impiden que otro usuario precree carpetas y la carpeta privada solo concede escritura a ese usuario, administradores y sistema. Si el perfil falla, la aplicacion intenta recuperarlo dentro de su zona privada y, como ultimo recurso, en `C:\Windows\Temp\LanzadorScripts` sin ejecutar binarios desde esa ruta.

Antes de arrancar .NET, el lanzador nativo dirige la extraccion del bundle a `%ProgramFiles%\LanzadorScripts\Runtimes\DotNet` y los temporales a la carpeta privada del usuario en `%ProgramData%`. Ninguna de esas rutas utiliza AppData.

| Caso | Accion |
|---|---|
| Perfil recuperable | Renombra a `WebView2_Danado_yyyyMMdd_HHmmss` y crea un perfil limpio |
| Perfil bloqueado | Usa `WebView2_Recuperacion_yyyyMMdd_HHmmss` |
| Fallo de proceso Edge/WebView2 | Registra detalle en `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Logs\arranque-yyyyMMdd.jsonl` |

Solo se conservan las ultimas 3 copias de diagnostico de perfiles dañados o de recuperacion.

## Seguridad de ejecucion

La API local exige cookie de sesion y token interno aleatorio por arranque. Los endpoints admin requieren siempre `Authorization: Bearer <token>`.

`permisos.json` y `catalogo-scripts.json` usan el contenedor v2, cifrado con AES-256-GCM y firmado con RSA-PSS/SHA-256. La clave AES se recupera de DPAPI de maquina y la clave RSA privada se busca en el almacen de certificados; el EXE solo incorpora el certificado publico. La aplicacion construye ambos nombres dentro de la carpeta configurada y no usa copias junto al EXE. Si falta un archivo, esta manipulado o no se puede validar, la aplicacion bloquea las ejecuciones.

La politica editable de permisos conserva solo las opciones operativas:

```json
{
  "seguridadScripts": {
    "scriptsElevadosPermitidos": [
      "admin/script.ps1"
    ],
    "permitirExecutionPolicyBypass": false
  }
}
```

La politica es fail closed. Los `.ps1`, `.bat` y `.cmd` deben figurar en el catalogo y coincidir en ruta relativa, extension, longitud y SHA-256. El lanzador valida el original y vuelve a validar la copia de staging. Los nombres con metacaracteres peligrosos se rechazan.

Los administradores publican el catalogo desde Ajustes mediante `Firmar scripts y publicar catálogo`. La operacion descubre de nuevo los archivos seleccionados y no modifica su contenido. El modo desarrollo es una excepcion administrativa limitada a la sesion.

La clave AES de los contenedores no esta integrada en el EXE: se recupera del archivo protegido por DPAPI de maquina. El EXE incorpora solo el certificado publico de verificacion; la clave RSA privada permanece en el almacen de certificados de los equipos administradores autorizados.

La aplicacion no crea tareas programadas ni registra la apertura con Windows. El operador la abre manualmente y el backend integrado toma la identidad del proceso que ha abierto la app.

El token maestro se firma con el certificado privado autorizado de Alex Roman. El mismo token puede reutilizarse mientras se conserve protegido y la firma sea valida; no requiere motivo operativo ni se registra como usado.

Antes de ejecutar, la aplicacion valida integridad, copia el script a staging local, aplica protecciones de archivo y revalida la copia para mitigar TOCTOU.

## Pruebas

```powershell
dotnet test .\Pruebas\LanzadorScripts.Pruebas.csproj
```

## Requisitos

| Requisito | Valor |
|---|---|
| SO | Windows 10/11 Pro o Enterprise |
| PowerShell | 5.1 |
| WebView2 | Fixed Runtime x64 embebido en el EXE y autoextraido al arrancar |
| Permisos app | Ejecutable con elevacion UAC `requireAdministrator` |
| Instalacion cliente | Ninguna; copiar el EXE |
| Politicas | GPO/AppLocker/WDAC permitiendo app y `powershell.exe` |

## Manuales

- [Manual de usuario](Manual_Usuarios.md)
- [Manual de administradores y desarrolladores](Manual_Administradores_Desarrolladores.md)
