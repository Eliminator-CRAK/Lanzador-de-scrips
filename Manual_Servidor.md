<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Instalacion y operacion de LanzadorScripts Servidor 1.9.0. -->

# Manual del servidor

## Requisitos

- Windows Server x64 unido al dominio.
- Cuenta de administrador local para instalar.
- Ruta local que contenga los scripts.
- DNS corporativo y TCP 47831 accesible desde los clientes.
- PowerShell 7 para el script automatico, o la consola grafica para instalacion interactiva.

No se necesita SQL Server, Internet, certificado privado de artefactos ni contraseña AES.

## Instalacion grafica

1. Extraer `LanzadorScripts_Servidor-1.9.0-x64.zip` en una carpeta local.
2. Ejecutar `LanzadorScripts.Servidor.exe` como administrador.
3. Pulsar **Instalar** y despues **Iniciar** si no se inicia automaticamente.
4. Comprobar que el resumen indica base integra y `Kerberos remoto preparado`.
5. Revisar **Usuarios**, **Catalogo**, **Auditoria** y **Actualizaciones**.

## Instalacion automatica

```powershell
pwsh -NoProfile -File .\Instalar-Servidor.ps1 `
  -Puerto 47831 `
  -RutaScripts 'R:\SCRIPS'
```

El script copia los binarios a `C:\Program Files\LanzadorScriptsServidor`, crea el servicio automatico retrasado bajo `LocalSystem`, configura recuperacion, agrega una regla de firewall solo para el perfil de dominio y crea el acceso del menu Inicio. Tambien prepara `C:\ProgramData\LanzadorScriptsServidor\Actualizaciones` y el recurso oculto `LanzadorScriptsActualizaciones$` de solo lectura para usuarios autenticados.

La primera ejecucion crea automaticamente la configuracion, la base y la clave DPAPI. La cuenta elevada que instala o inicia el servicio queda como primer administrador. Su identidad se protege con DPAPI en un archivo de un solo uso que se elimina tras inicializar la base; `configuracion-servidor.json` no contiene administradores. Si la carpeta de scripts existe, genera el catalogo inicial.

El servicio registra automaticamente los SPN `LanzadorScripts/<FQDN>` y `LanzadorScripts/<nombre-corto>` en su cuenta de equipo de Active Directory. Si el controlador de dominio aun no esta disponible durante el arranque, empieza a reintentar a los 30 segundos y aumenta progresivamente la espera hasta cinco minutos, sin aceptar NTLM como sustituto de Kerberos.

## Configurar clientes

```powershell
pwsh -NoProfile -File .\Crear-ConfiguracionCliente.ps1 `
  -ServidorCentral 'MAD002MICROPRU.mad.ae.aena.es' `
  -Puerto 47831 `
  -RutaScripts '\\MAD002MICROPRU.mad.ae.aena.es\R$\SCRIPS' `
  -Salida '.\LanzadorScripts-Cliente.lanzadorconfig'
```

Distribuir el `.lanzadorconfig` junto con el cliente. El archivo no es secreto, pero debe proceder de una ubicacion administrada para evitar que un usuario apunte a otro servidor.

Actualizar primero el paquete servidor y despues los clientes. La version 1.9.0 utiliza el SPN propio `LanzadorScripts/<servidor>`; mantiene `HOST/<servidor>` unicamente como compatibilidad de migracion y siempre exige autenticacion mutua.

## Operacion diaria

- **Resumen**: estado del servicio, integridad, puerto, usuarios y eventos.
- **Usuarios**: altas, bajas, roles, limites y subcarpetas.
- **Auditoria**: filtros y detalle de cada ejecucion.
- **Catalogo**: hashes vigentes y recreacion tras cambios autorizados.
- **Actualizaciones**: carpeta, recurso UNC, version activa, firma, SHA-256 y errores de validacion.
- **Mantenimiento**: integridad, copia de seguridad y servicio.

## Publicar una actualizacion

1. Copiar un MSI firmado llamado `LanzadorScripts-X.Y.Z-x64.msi` a `C:\ProgramData\LanzadorScriptsServidor\Actualizaciones`.
2. Abrir **Actualizaciones** y pulsar **Volver a validar**.
3. Confirmar version, tamano, firma y SHA-256 antes de comunicarla a usuarios.

El servidor publica automaticamente la version valida mas alta. Conserva las anteriores para rollback manual. No existe un interruptor de publicacion: renombrar o eliminar el MSI retira esa version. La carpeta y el recurso se conservan durante actualizaciones y desinstalaciones normales; `-EliminarDatos` realiza el purgado explicito.

El cliente MSI 1.9.0 debe instalarse manualmente una vez. Las versiones posteriores se ofrecen de forma opcional en la barra superior. La portable nunca consulta este repositorio.

## Copias y recuperacion

Crear copias desde la consola antes de cambios de usuarios o catalogo. Los ZIP se guardan en:

```text
C:\ProgramData\LanzadorScriptsServidor\CopiasSeguridad
```

Conservar juntos la base, la clave DPAPI y la configuracion. La clave esta ligada al servidor actual. Para recuperar en otra maquina seria necesario un procedimiento de migracion de clave distinto; copiar el ZIP a otro servidor no basta.

## Desinstalacion

```powershell
pwsh -NoProfile -File .\Desinstalar-Servidor.ps1
```

Este comando retira servicio, firewall, acceso y binarios, pero conserva datos, copias y paquetes de actualizacion. Para una retirada definitiva y consciente:

```powershell
pwsh -NoProfile -File .\Desinstalar-Servidor.ps1 -EliminarDatos
```

Ejecutar el desinstalador desde el ZIP original, no desde `Program Files`.

## Diagnostico

- Revisar `C:\ProgramData\LanzadorScriptsServidor\Logs`.
- Confirmar `Get-Service LanzadorScriptsServidor`.
- Probar `Test-NetConnection MAD002MICROPRU.mad.ae.aena.es -Port 47831` desde un cliente.
- Comprobar en **Resumen** que aparece `Kerberos remoto preparado`.
- Consultar `setspn -Q LanzadorScripts/MAD002MICROPRU.mad.ae.aena.es` si el estado sigue pendiente.
- Comprobar que la cuenta exacta de Windows figura activa en **Usuarios**.
- Comprobar que la ruta local del catalogo y la UNC del cliente representan los mismos archivos.

No editar manualmente `LanzadorScripts.db` ni `base-datos.key.dpapi`.
