<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Administracion, desarrollo y publicacion de LanzadorScripts 1.8.1. -->

# Manual de administradores y desarrolladores

## Orden de despliegue

1. Instalar primero el paquete servidor.
2. Verificar servicio, base, puerto, administradores y catalogo.
3. Respaldar la base y la clave DPAPI.
4. Distribuir el MSI o la portable del cliente.
5. Importar la configuracion cliente y probar con una cuenta nominal.

No se deben mezclar clientes 1.8.1 con los JSON operativos de 1.7.x. La fuente autoritativa es la base central.

## Cliente instalado

```powershell
msiexec /i LanzadorScripts-1.8.1-x64.msi
msiexec /i LanzadorScripts-1.8.1-x64.msi /qn /norestart
msiexec /i LanzadorScripts-1.8.1-x64.msi CREATE_DESKTOP_SHORTCUT=1 /qn /norestart
msiexec /fa LanzadorScripts-1.8.1-x64.msi /qn /norestart
msiexec /x LanzadorScripts-1.8.1-x64.msi /qn /norestart
```

La instalacion es x64 y para todos los usuarios. Crea menu Inicio y asociacion `.lanzadorconfig`. Las actualizaciones conservan configuracion. La desinstalacion completa elimina solo rutas locales conocidas y nunca borra la base del servidor.

## Administracion central

La consola `LanzadorScripts.Servidor.exe` debe ejecutarse como administrador en el servidor. Permite:

- instalar, iniciar, detener, reiniciar y desinstalar el servicio;
- crear, editar, activar y desactivar usuarios;
- asignar rol, limite simultaneo y subcarpetas autorizadas;
- consultar la auditoria por usuario, fecha, resultado y script;
- recrear el catalogo y sus SHA-256;
- comprobar integridad SQLite;
- crear copias de seguridad.

El servidor impide eliminar o desactivar el ultimo administrador activo y registra las operaciones administrativas en la auditoria.

## Seguridad de la base

La clave AES-256 se crea mediante `RandomNumberGenerator` en el primer inicio y se protege con DPAPI `LocalMachine`. Las filas sensibles usan AES-GCM y los indices de busqueda usan HMAC. No hay una contraseña distribuida ni una clave embebida en los ejecutables.

La base no es un contenedor SQLCipher: nombres de tablas, identificadores opacos y columnas tecnicas siguen visibles. El contenido funcional queda cifrado y autenticado. Un administrador local puede provocar borrado o indisponibilidad, por lo que deben aplicarse control de acceso, copias y supervision del servidor.

Una copia contiene:

```text
LanzadorScripts.db
base-datos.key.dpapi
configuracion-servidor.json
```

La pareja actual se restaura en el mismo servidor porque DPAPI esta ligada a la maquina. No copiar solo la base ni distribuir la clave a clientes.

## Red y dominio

- Servicio: `LanzadorScriptsServidor` bajo `LocalSystem`.
- Inicio: automatico con recuperacion ante fallos.
- Puerto predeterminado: TCP 47831, perfil de firewall `Domain`.
- Autenticacion: SSPI/Kerberos o Negotiate con SPN `HOST/<servidor>`.
- Endpoint predeterminado: `MAD002MICROPRU.mad.ae.aena.es:47831`.

Los clientes deben resolver el FQDN y no deben usar una IP si se exige autenticacion mutua. La cuenta de equipo del servidor debe conservar sus SPN `HOST` normales de Active Directory.

## Catalogo y scripts

El servicio usa por defecto `R:\SCRIPS` para generar el catalogo. Los clientes usan la ruta UNC configurada. Si `R:` es una unidad mapeada de usuario y no un volumen local, `LocalSystem` no podra verla; configure una ruta local real en el servidor.

Tras cualquier modificacion autorizada de un script, abrir **Catalogo**, seleccionar la carpeta local y pulsar **Recrear catalogo**. El servidor crea antes una copia de seguridad y actualiza los hashes en una transaccion.

## Desarrollo

```powershell
dotnet restore .\Pruebas\LanzadorScripts.Pruebas.csproj
dotnet build .\LanzadorScripts.csproj -c Release --no-restore
dotnet build .\Servidor\LanzadorScripts.Servidor.Servicio\LanzadorScripts.Servidor.Servicio.csproj -c Release --no-restore
dotnet build .\Servidor\LanzadorScripts.Servidor.Administracion\LanzadorScripts.Servidor.Administracion.csproj -c Release --no-restore
dotnet test .\Pruebas\LanzadorScripts.Pruebas.csproj -c Release --no-restore
dotnet list .\Pruebas\LanzadorScripts.Pruebas.csproj package --vulnerable --include-transitive --no-restore
```

La publicacion usa exclusivamente Visual Studio Professional 2026, Visual Studio Installer Projects, C++ x64 y PowerShell 7.6.

```powershell
pwsh -NoProfile -File .\Herramientas\PublicarPortable.ps1
pwsh -NoProfile -File .\Herramientas\PublicarServidor.ps1
```

El certificado Authenticode firma MSI, EXE y scripts de distribucion. El certificado publico incluido permite comprobar el editor; `SHA256SUMS.txt` verifica integridad, pero no sustituye la confianza del certificado. Nunca se versionan o publican PFX, claves DPAPI, bases operativas, perfiles WebView2, `bin` u `obj`.

## Flujo Git

GitLab es el repositorio principal para ramas y merge requests. GitHub es una replica del mismo commit. Cada cambio debe superar pruebas, auditoria NuGet, Semgrep estricto, Gitleaks y revision CodeRabbit antes de fusionarse. No se utiliza Aikido.

La release `v1.8.1` debe publicar bytes identicos en ambos proveedores y contener los tres entregables, hashes, certificado publico y notas de despliegue.
