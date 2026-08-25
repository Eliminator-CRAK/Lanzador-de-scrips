<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Arquitectura, compilacion y despliegue de LanzadorScripts. -->

# LanzadorScripts 1.8.2

LanzadorScripts ejecuta scripts PowerShell, BAT y CMD autorizados desde una interfaz WPF con WebView2. La version 1.8.2 separa el cliente de un servicio central de Windows que administra permisos, catalogo y auditoria.

## Entregables

- `LanzadorScripts-1.8.2-x64.msi`: cliente instalado para todos los usuarios.
- `LanzadorScripts_Portable-1.8.2-x64.exe`: cliente portable de sesion efimera.
- `LanzadorScripts_Servidor-1.8.2-x64.zip`: consola administrativa, servicio Windows y scripts de despliegue.

Los tres paquetes son autocontenidos para Windows x64 y no descargan .NET ni WebView2 durante la ejecucion.

## Arquitectura

El cliente abre los scripts desde la ruta compartida configurada y consulta al servidor central antes de mostrarlos o ejecutarlos. La comunicacion usa TCP con `NegotiateStream`, autenticacion integrada de Windows, cifrado, firma y autenticacion mutua.

El servidor mantiene una base SQLite local con tablas para:

- usuarios y roles;
- permisos de ejecucion y elevacion;
- catalogo y SHA-256 de cada script;
- auditoria por usuario, equipo, script y ejecucion;
- metadatos y revisiones.

El contenido sensible de las filas se cifra y autentica con AES-256-GCM. Los indices de busqueda usan HMAC-SHA-256. La clave se genera automaticamente en el primer arranque y se protege con DPAPI `LocalMachine`; ningun cliente recibe esa clave y no se solicita una contraseña AES.

SQLite conserva visible su estructura tecnica, identificadores opacos y columnas necesarias para busqueda. Una modificacion del contenido cifrado se detecta al leerlo. Un administrador local del servidor siempre puede borrar o sustituir archivos, por lo que las ACL y las copias de seguridad siguen siendo necesarias.

## Rutas del servidor

```text
C:\Program Files\LanzadorScriptsServidor
C:\ProgramData\LanzadorScriptsServidor\Datos\LanzadorScripts.db
C:\ProgramData\LanzadorScriptsServidor\Seguridad\base-datos.key.dpapi
C:\ProgramData\LanzadorScriptsServidor\CopiasSeguridad
C:\ProgramData\LanzadorScriptsServidor\Logs
C:\ProgramData\LanzadorScriptsServidor\configuracion-servidor.json
```

Las ACL de `ProgramData` permiten acceso completo solo a `SYSTEM` y administradores locales. La base y `base-datos.key.dpapi` deben respaldarse juntas. La proteccion DPAPI actual permite restaurar esa pareja en el mismo servidor Windows.

## Puesta en marcha

1. Extraer `LanzadorScripts_Servidor-1.8.2-x64.zip` en `MAD002MICROPRU`.
2. Ejecutar `LanzadorScripts.Servidor.exe` como administrador y pulsar **Instalar**, o ejecutar `Instalar-Servidor.ps1` desde PowerShell 7.
3. Confirmar que el servicio `LanzadorScriptsServidor` esta iniciado y que el firewall de dominio admite TCP 47831.
4. Abrir la consola servidor, revisar el administrador registrado y recrear el catalogo desde la carpeta local de scripts.
5. Generar un `.lanzadorconfig` con `Crear-ConfiguracionCliente.ps1` y distribuirlo junto al MSI o la portable.

La cuenta elevada que realiza la instalacion se registra como primer administrador. La identidad se entrega al servicio mediante un archivo DPAPI de un solo uso, se elimina tras crear o validar la base y no se guarda en `configuracion-servidor.json`.

El archivo `.lanzadorconfig` solo contiene DNS, puerto y ruta de scripts. No contiene permisos, certificados privados ni secretos.

## Cliente

La configuracion predeterminada usa:

```text
Servidor: MAD002MICROPRU.mad.ae.aena.es
Puerto: 47831
Scripts: \\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS
```

La cuenta de dominio debe estar activa en la base central y disponer de lectura sobre la carpeta compartida de scripts. La ejecucion queda bloqueada si no se confirman permisos, catalogo o el evento inicial de auditoria.

Los administradores pueden abrir la auditoria con `Ctrl+Shift+M`. La ventana permite filtrar por usuario, fecha, resultado y script. En la version instalada, el boton de cerrar mantiene el cliente en la bandeja y **Cerrar** en su menu finaliza la aplicacion.

La portable no crea icono de bandeja: el boton rojo cierra el proceso. Guarda sus datos exclusivamente bajo `%TEMP%\LanzadorScripts\Portable\<sesion>` y elimina la sesion al terminar. En el siguiente arranque limpia sesiones abandonadas cuyo proceso ya no exista.

## Compilacion

Requisitos de desarrollo:

- Windows x64;
- SDK .NET fijado por `global.json`;
- Visual Studio Professional 2026;
- Visual Studio Installer Projects;
- herramientas C++ x64;
- PowerShell 7.6 para publicar.

```powershell
dotnet restore .\Pruebas\LanzadorScripts.Pruebas.csproj
dotnet build .\LanzadorScripts.csproj -c Release --no-restore
dotnet build .\Servidor\LanzadorScripts.Servidor.Servicio\LanzadorScripts.Servidor.Servicio.csproj -c Release --no-restore
dotnet build .\Servidor\LanzadorScripts.Servidor.Administracion\LanzadorScripts.Servidor.Administracion.csproj -c Release --no-restore
dotnet test .\Pruebas\LanzadorScripts.Pruebas.csproj -c Release --no-restore
```

## Publicacion

La publicacion final exige un arbol Git limpio y el certificado Authenticode configurado:

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1
pwsh -NoProfile -File .\Herramientas\PublicarServidor.ps1
```

`publicacion` recibe el MSI y la portable. `publicacion-servidor` recibe un unico ZIP con los binarios servidor, scripts operativos firmados, certificado publico y `SHA256SUMS.txt`. Nunca se empaquetan una base, una clave DPAPI, un PFX ni una clave privada.

## Verificacion

Antes de publicar se ejecutan:

- pruebas Release;
- auditoria de dependencias NuGet;
- Semgrep estricto;
- Gitleaks sobre todo el historial;
- validacion Authenticode y SHA-256.

No se utiliza Aikido. GitLab es el flujo principal de merge request y GitHub mantiene una replica exacta. Ambos `main` deben terminar en el mismo commit.

Consulta [Manual_Servidor.md](Manual_Servidor.md), [Manual_Usuarios.md](Manual_Usuarios.md) y [Manual_Administradores_Desarrolladores.md](Manual_Administradores_Desarrolladores.md) para el procedimiento operativo.
