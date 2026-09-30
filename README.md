<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Arquitectura, compilacion y despliegue de LanzadorScripts. -->

# LanzadorScripts 2.0.2

LanzadorScripts ejecuta scripts PowerShell, BAT y CMD autorizados desde una interfaz completamente WPF nativa. El cliente y el servidor 2.0.2 administran configuracion global, permisos, catalogo, auditoria y actualizaciones opcionales del MSI instalado.

La version 1.9.1 incorpora [monitorizacion tecnica en GitLab con OpenTelemetry](Documentacion/MonitorizacionGitLab.md), sin exportar datos personales, scripts ni auditorias. Se puede desactivar con `LANZADOR_MONITORIZACION_HABILITADA=false`.

## Entregables

- `LanzadorScripts-2.0.2-x64.msi`: cliente instalado para todos los usuarios.
- `LanzadorScripts_Portable-2.0.2-x64.exe`: cliente portable de sesion efimera.
- `LanzadorScripts_Servidor-2.0.2-x64.zip`: consola administrativa, servicio Windows y scripts de despliegue.

Los tres paquetes son autocontenidos para Windows x64. Los clientes no utilizan WebView2, HTML, JavaScript ni un servidor HTTP local. No descargan ni instalan componentes de navegador y no eliminan el runtime compartido de Edge que utilicen otras aplicaciones.

## Arquitectura

El cliente abre los scripts desde la ruta compartida configurada y consulta al servidor central antes de mostrarlos o ejecutarlos. Desde otros equipos, la comunicacion usa TCP con `NegotiateStream`, Kerberos, cifrado, firma y autenticacion mutua. En el propio servidor usa el pipe administrativo local protegido, evitando el fallo de autenticacion Kerberos en bucle sin aceptar NTLM. El servicio registra automaticamente `LanzadorScripts/<servidor>` en la cuenta de equipo de Active Directory.

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
C:\ProgramData\LanzadorScriptsServidor\Actualizaciones
C:\ProgramData\LanzadorScriptsServidor\configuracion-servidor.json
```

Las ACL de `ProgramData` permiten acceso completo solo a `SYSTEM` y administradores locales. La base y `base-datos.key.dpapi` deben respaldarse juntas. La proteccion DPAPI actual permite restaurar esa pareja en el mismo servidor Windows.

## Puesta en marcha

1. Respaldar la base y su clave y extraer `LanzadorScripts_Servidor-2.0.2-x64.zip` en `MAD002MICROPRU`.
2. Ejecutar `LanzadorScripts.Servidor.exe` como administrador y pulsar **Instalar**, o ejecutar `Instalar-Servidor.ps1` desde PowerShell 7.
3. Confirmar que el servicio `LanzadorScriptsServidor` esta iniciado, que el resumen muestra `Kerberos remoto preparado` y que el firewall de dominio admite TCP 47831.
4. Abrir la consola servidor, revisar el administrador registrado y recrear el catalogo desde la carpeta local de scripts.
5. Revisar **Actualizaciones** y confirmar el recurso `LanzadorScriptsActualizaciones$`.
6. En **Clientes**, revisar la ruta UNC de scripts y el limite global de ejecuciones simultaneas. Guardar antes de distribuir los clientes 2.0.2.

La cuenta elevada que realiza la instalacion se registra como primer administrador. La identidad se entrega al servicio mediante un archivo DPAPI de un solo uso, se elimina tras crear o validar la base y no se guarda en `configuracion-servidor.json`.

Los clientes 2.0.x no leen configuraciones locales ni importan `.lanzadorconfig`. La configuracion comun se guarda cifrada en la base del servidor. Los clientes anteriores siguen usando su formato anterior; la herramienta historica de generacion se conserva en el codigo, pero no se distribuye en el ZIP nuevo.

## Cliente

La configuracion predeterminada usa:

```text
Servidor: MAD002MICROPRU.mad.ae.aena.es
Puerto: 47831
Scripts: \\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS
```

La cuenta de dominio debe estar activa en la base central y disponer de lectura sobre la carpeta compartida de scripts. La ejecucion queda bloqueada si no se confirman configuracion, permisos, catalogo o el evento inicial de auditoria. No hay modo sin conexion. Una desconexion no cancela scripts ya iniciados: sus resultados se reintentan en memoria y bloquean nuevas ejecuciones hasta confirmarse. Al cerrar se espera hasta 30 segundos; un cierre forzado puede perder un resultado final, pero el inicio ya registrado permanece. La salida de consola no se guarda en disco ni se envia como auditoria.

Las consolas usan salida seleccionable y un campo de respuesta nativo inferior. Enter envia una linea, incluso vacia para Pause. Las respuestas tambien llegan a scripts elevados a traves del broker autenticado y no se registran en auditoria. La salida continua no cambia el foco del campo de respuesta.

Desde 2.0.1 la altura de cada consola depende del espacio disponible y de las consolas abiertas, no del volumen de salida. La salida larga se desplaza dentro de su consola y mantiene accesible la entrada, tambien al redimensionar la ventana.

La version 2.0.2 recupera el saludo, los contadores, el icono original, los controles de ventana y las consolas en dos columnas cuando hay anchura suficiente. **Detener todo** conserva las salidas; el boton de limpiar retira solo consolas finalizadas. La auditoria tiene acceso directo para administradores. Las pruebas visuales verifican la ventana real, no un contenedor de prueba con una barra estandar.

El buscador recorre todas las carpetas autorizadas, muestra la ruta relativa de cada coincidencia y vuelve a la carpeta anterior al vaciarlo. Cambiar de carpeta o refrescar no destruye las consolas abiertas.

Los administradores pueden abrir la auditoria con `Ctrl+Shift+M` o desde Configuracion avanzada > Conexion. La ventana permite filtrar por usuario, fecha, resultado y script. En la version instalada, el boton de cerrar mantiene el cliente en la bandeja y **Cerrar** en su menu finaliza la aplicacion.

El MSI 1.9.0 se instala manualmente una vez. El cliente instalado consulta una sola vez al iniciar y, cuando el servidor publica una version posterior valida, muestra **Actualizar a X.Y.Z**. Ignorar el boton no bloquea la aplicacion ni guarda aplazamientos. La portable nunca consulta ni instala actualizaciones.

En **Actualizaciones > Seleccionar MSI**, el administrador elige un archivo, revisa version, firma, tamano y SHA-256 y confirma la publicacion atomica. Una misma version no se sustituye por contenido distinto. Tambien se admite copiar MSI completos a `C:\ProgramData\LanzadorScriptsServidor\Actualizaciones`. El servicio selecciona la version valida mas alta y rechaza paquetes incompletos, enlazados, de otra arquitectura, producto, `UpgradeCode`, firma o certificado. Para retirar una version basta con renombrar o eliminar su MSI; las anteriores se conservan para rollback manual.

La portable no crea icono de bandeja: el boton rojo cierra el proceso. Guarda sus datos bajo `%TEMP%\LanzadorScripts\Portable\<sesion>` y ejecuta sus binarios desde una sesion protegida bajo `C:\Program Files\LanzadorScriptsPortable\Sesiones`. Elimina ambas sesiones al terminar y limpia restos abandonados en el siguiente arranque.

El cliente instalado conserva solo sus archivos instalados. Los datos propios de cada ejecucion se crean en `%LOCALAPPDATA%\LanzadorScripts\SesionesCliente\Sesion-<guid>` y se eliminan al cierre definitivo; un bloqueo permite recuperar sesiones abandonadas sin borrar una instancia activa. El staging de una actualizacion pendiente se limpia en el siguiente arranque. Windows y antivirus pueden generar registros propios que la aplicacion no debe borrar. No se eliminan exportaciones explicitas, datos remotos ni configuraciones historicas de versiones anteriores.

Actualizar primero el servidor y despues los clientes. La migracion WPF no cambia la estructura de permisos y catalogo. Desde 1.10.0 el servidor agrega un metadato cifrado sin cambiar los permisos ni el catalogo. Para volver a un servidor anterior, restaurar conjuntamente su copia previa de base y clave: su comprobador de integridad no conoce el nuevo metadato. No recrear la base ni borrar la clave durante la actualizacion.

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

GitHub ejecuta las pruebas WPF, las capturas y la publicacion de validacion en Windows alojado, sin certificados privados. GitLab compila los proyectos con destino Windows y ejecuta los analisis de seguridad; sus ejecutores Linux no ejecutan la interfaz WPF. La creacion, comprobacion y firma Authenticode de los instaladores se realiza en el equipo Windows de publicacion antes de subir los mismos archivos a ambos proveedores.

Consulta [Manual_Servidor.md](Manual_Servidor.md), [Manual_Usuarios.md](Manual_Usuarios.md) y [Manual_Administradores_Desarrolladores.md](Manual_Administradores_Desarrolladores.md) para el procedimiento operativo.
