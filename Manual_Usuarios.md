<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Uso del cliente LanzadorScripts 1.8.0. -->

# Manual de usuarios

## Elegir version

- Instalada: ejecutar `LanzadorScripts-1.8.0-x64.msi`. Conserva configuracion y runtimes.
- Portable: ejecutar `LanzadorScripts_Portable-1.8.0-x64.exe`. Elimina sus datos locales al cerrar.

Ambas variantes necesitan conexion de dominio con el servidor central y acceso de lectura a la carpeta compartida de scripts.

## Primer inicio

Si el administrador entrega `LanzadorScripts-Cliente.lanzadorconfig`, abrirlo o importarlo desde la aplicacion. El paquete configura el servidor, el puerto y la ruta compartida; no instala claves ni certificados privados.

La aplicacion usa automaticamente la cuenta de Windows iniciada. Si la cuenta no figura en la base central, los scripts aparecen bloqueados.

## Ejecutar scripts

1. Buscar el script por nombre.
2. Pulsar **Ejecutar script**.
3. Revisar la salida en la consola de la aplicacion.
4. Cerrar la consola solo cuando la ejecucion haya terminado o se desee cancelarla.

Antes de iniciar, el cliente valida permisos y SHA-256 contra el servidor y confirma el evento de auditoria. Si el servidor o la auditoria no responden, la ejecucion se bloquea.

## Bandeja de Windows

El boton de cerrar de la ventana principal oculta la aplicacion y la mantiene en la bandeja. El menu de bandeja permite mostrar, minimizar o cerrar. La opcion se llama **Cerrar** y solo avisa de cancelaciones cuando existen scripts activos.

## Auditoria

Los administradores pueden pulsar `Ctrl+Shift+M` para consultar la auditoria central. Los usuarios nominales no pueden abrir esa vista.

## Errores habituales

- **Servidor central no disponible**: comprobar red corporativa, DNS y VPN.
- **Acceso denegado**: solicitar al administrador que active la cuenta exacta `DOMINIO\usuario`.
- **Script modificado**: el archivo ya no coincide con el SHA-256 del catalogo; un administrador debe recrearlo.
- **No se pudo confirmar la auditoria**: el servicio central no pudo guardar el evento y bloquea la ejecucion por seguridad.
- **Ruta de scripts no disponible**: comprobar permisos de lectura sobre la carpeta compartida.

La version 1.8.0 no necesita `artefactos.key`, `permisos.json`, `catalogo-scripts.json` ni el certificado privado usado por versiones anteriores.
