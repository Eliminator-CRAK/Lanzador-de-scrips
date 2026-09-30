<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Uso del cliente LanzadorScripts 2.0.2. -->

# Manual de usuarios

## Elegir version

- Instalada: ejecutar `LanzadorScripts-2.0.2-x64.msi`. Conserva sus archivos instalados; los datos de sesion son temporales.
- Portable: ejecutar `LanzadorScripts_Portable-2.0.2-x64.exe`. Elimina sus datos locales al cerrar.

Ambas variantes necesitan conexion de dominio con el servidor central y acceso de lectura a la carpeta compartida de scripts.

## Primer inicio

El cliente obtiene la configuracion del servidor central. No importa archivos locales de configuracion. Ambos clientes son WPF nativos y no necesitan WebView2.

La aplicacion usa automaticamente la cuenta de Windows iniciada. Si la cuenta no figura en la base central, los scripts aparecen bloqueados.

## Ejecutar scripts

1. Buscar el script por nombre.
2. Pulsar el icono de ejecutar del script.
3. Revisar la salida en la consola de la aplicacion.
4. Escribir respuestas en el campo inferior y pulsar Enter; una respuesta vacia continua una pausa. Tambien funciona con scripts elevados autorizados.
5. Cerrar la consola solo cuando la ejecucion haya terminado o se desee cancelarla.

Antes de iniciar, el cliente valida permisos y SHA-256 contra el servidor y confirma el evento de auditoria. Si el servidor o la auditoria no responden, la ejecucion se bloquea.

**Detener todo** cancela las ejecuciones activas tras confirmar y conserva sus salidas. El boton con papelera retira solo las consolas finalizadas; no detiene scripts activos. Las consolas se organizan en dos columnas cuando hay espacio y vuelven a una columna al reducir la ventana.

## Cierre y bandeja de Windows

En la version instalada, el boton de cerrar oculta la aplicacion y la mantiene en la bandeja. Su menu permite mostrar, minimizar o cerrar. La opcion se llama **Cerrar** y solo avisa de cancelaciones cuando existen scripts activos.

La portable no trabaja en segundo plano ni crea icono de bandeja. Su boton rojo finaliza la aplicacion y activa la limpieza de la sesion temporal.

## Actualizaciones

La version instalada comprueba una sola vez al iniciar si el servidor ofrece un MSI posterior. Cuando existe, aparece **Actualizar a X.Y.Z** en la barra superior. El boton es opcional: ignorarlo no bloquea el uso ni muestra recordatorios emergentes.

No se puede comenzar la actualizacion mientras haya scripts activos. Al pulsar el boton, la aplicacion muestra el progreso de descarga, verificacion e instalacion. Si Windows requiere reinicio, debe reiniciarse antes de volver a abrir LanzadorScripts. La portable no consulta ni instala actualizaciones.

## Auditoria

Los administradores pueden pulsar el icono de auditoria del encabezado o `Ctrl+Shift+M` para consultar la auditoria central. Los usuarios nominales no pueden abrir esa vista.

## Errores habituales

- **Servidor central no disponible**: comprobar red corporativa, DNS y VPN.
- **Acceso denegado**: solicitar al administrador que active la cuenta exacta `DOMINIO\usuario`.
- **Script modificado**: el archivo ya no coincide con el SHA-256 del catalogo; un administrador debe recrearlo.
- **No se pudo confirmar la auditoria**: el servicio central no pudo guardar el evento y bloquea la ejecucion por seguridad.
- **Ruta de scripts no disponible**: comprobar permisos de lectura sobre la carpeta compartida.

La version 2.0.0 no necesita `artefactos.key`, `permisos.json`, `catalogo-scripts.json` ni el certificado privado usado por versiones anteriores.
