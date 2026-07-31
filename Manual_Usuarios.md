<!-- (Autor: Alex Roman) -->
<!-- Descripcion: Manual de uso para usuarios normales del lanzador. -->

# Manual De Usuario

## Objetivo

LanzadorScripts permite ejecutar scripts autorizados desde una interfaz local. La aplicacion valida permisos, integridad y disponibilidad antes de permitir una ejecucion.

## Inicio

1. Abra `LanzadorScripts.exe`.
2. Acepte el aviso de administrador de Windows si aparece.
3. Espere a que finalice la pantalla de preparacion.
4. Si aparece un aviso de permisos, WebView2 o conexion, no ejecute scripts y contacte con el administrador.

## Cambios En La Version 1.5.3

- WebView2 utiliza un perfil nuevo y repara sus permisos antes de abrir Microsoft Edge.
- El arranque y la limpieza ya no muestran ventanas de progreso separadas.
- Cerrar desde la bandeja es directo cuando no hay scripts activos; la confirmacion aparece solo si se cancelara alguna ejecucion.

## Cambios En La Version 1.5.2

- El nuevo icono de consola utiliza un fondo negro opaco en el ejecutable, la ventana, la barra de tareas y la bandeja.

## Cambios En La Version 1.5.1

- LanzadorScripts utiliza un nuevo icono propio de consola, mas reconocible en el ejecutable, la ventana, la barra de tareas y la bandeja.

## Cambios En La Version 1.5.0

- La aplicacion muestra una ventana de preparacion incluso mientras extrae los componentes internos.
- El icono de LanzadorScripts permanece en la bandeja mientras la aplicacion este ejecutandose.
- Minimizar conserva la aplicacion en la barra de tareas. Cerrar la ventana la deja en segundo plano sin detener scripts.
- El menu de bandeja permite restaurar, maximizar, minimizar o cerrar definitivamente.
- El cierre definitivo solicita confirmacion y muestra los scripts que se cancelaran cuando existen ejecuciones activas.
- Una segunda apertura de LanzadorScripts restaura la ventana existente.
- El usuario no debe modificar `permisos.json`, `catalogo-scripts.json` ni crear carpetas de configuracion.
- La aplicacion ya no se abre automaticamente con Windows; debe iniciarse desde el EXE distribuido.
- La configuracion, WebView2, temporales y logs se guardan en las zonas protegidas de `ProgramData` y `Program Files`, no en el perfil AppData del usuario.
- La configuracion local se guarda de forma atomica y coordina las peticiones paralelas para evitar bloqueos de `configuracion.dat`.
- WebView2 usa un perfil nuevo con permisos compatibles con sus procesos aislados; el usuario no debe modificar la carpeta.
- Si un script cambia, queda bloqueado hasta que un administrador publique de nuevo el catalogo.
- Si falta la clave local, la aplicacion intenta recuperarla automaticamente del paquete corporativo autorizado. No pide al usuario la clave AES.
- Los permisos y el catalogo corporativos antiguos se pueden leer durante la migracion si conservan sus firmas validas y comparten la misma clave.
- Si el paquete no existe, la cuenta no pertenece al grupo autorizado o los permisos no se pueden validar, la aplicacion bloquea la ejecucion y el usuario debe comunicar el mensaje exacto.

## Ejecutar Un Script

1. Abra la carpeta autorizada que contiene el script.
2. Use `← Volver` para subir una carpeta o `Principal` para regresar al inicio.
3. Busque el script en la lista.
4. Revise si aparece bloqueado y lea el motivo.
5. Pulse ejecutar solo si el script esta permitido.
6. Mantenga la consola visible hasta ver el evento final.
7. Revise el codigo de salida cuando termine.

## Entrada Interactiva

Algunos scripts pueden pedir datos. Escriba solo la informacion solicitada por el procedimiento operativo. No introduzca contrasenas, tokens o claves si el script no esta aprobado para ello.

## Cancelar Una Ejecucion

Use cancelar solo si el script se ha quedado bloqueado o si el procedimiento lo exige. La cancelacion queda auditada con usuario, equipo, script y hora.

## Estados Habituales

| Estado | Accion |
|---|---|
| Script bloqueado por permisos | Solicitar autorizacion al administrador. |
| Script bloqueado por firma/hash | No ejecutar. El administrador debe revisar integridad. |
| Backend local no disponible | Reiniciar la app y avisar a soporte si se repite. |
| Carpeta remota de scripts no disponible | Esperar recuperacion de red; la interfaz seguira visible pero los scripts quedan bloqueados. |
| Carpeta remota de permisos no disponible | No ejecutar. Solo administracion puede activar emergencia temporal; los cambios de permisos quedan bloqueados mientras la carpeta siga inaccesible. |
| Permisos ausentes o no validos | No ejecutar y comunicar el mensaje exacto al administrador. |
| Falta `artefactos.key` | Reiniciar conectado a la red corporativa. La app debe aprovisionarla automaticamente; si falla, comunicar el mensaje al administrador. |
| WebView2 no disponible | Reiniciar la app. Si se repite, avisar a soporte con la ruta de logs. |
| El error muestra una ruta dentro de AppData | El EXE es anterior a la version 1.5.0 o se ha abierto el componente interno. Cierre la app y use uno de los EXE distribuidos. |

## Logs

Los logs de ejecucion se guardan en `%ProgramData%\LanzadorScripts\Usuarios\<id-SID>\Logs`. No modifique ni borre logs salvo indicacion del administrador.

## Buenas Practicas

- Ejecute solo scripts necesarios para su tarea.
- No comparta capturas con tokens, rutas internas o datos sensibles.
- Puede ocultar la ventana mientras un script trabaja, pero no use `Cerrar LanzadorScripts` en la bandeja hasta que termine.
- Informe cualquier mensaje inesperado o bloqueo repetido.
