import fs from 'fs';

let content = fs.readFileSync('src/App.tsx', 'utf-8');

const wordReplacements = {
  'Script': 'Script',
  'lastRun': 'ultimaEjecucion',
  'isLocked': 'estaBloqueado',
  'LogEntry': 'EntradaRegistro',
  'ConsoleState': 'EstadoConsola',
  'activeScript': 'scriptActivo',
  'logs': 'registros',
  'consoles': 'consolas',
  'setConsoles': 'setConsolas',
  'runningCount': 'conteoEjecucion',
  'setRunningCount': 'setConteoEjecucion',
  'windowState': 'estadoVentana',
  'setWindowState': 'setEstadoVentana',
  'minimized': 'minimizado',
  'closed': 'cerrado',
  'isFullscreen': 'esPantallaCompleta',
  'setIsFullscreen': 'setEsPantallaCompleta',
  'showGlobalCloseConfirm': 'mostrarConfirmacionCierreGlobal',
  'setShowGlobalCloseConfirm': 'setMostrarConfirmacionCierreGlobal',
  'currentView': 'vistaActual',
  'setCurrentView': 'setVistaActual',
  'settings': 'ajustes',
  'connectionError': 'errorConexion',
  'setConnectionError': 'setErrorConexion',
  'userRole': 'rolUsuario',
  'setUserRole': 'setRolUsuario',
  'maxScripts': 'maxScripts',
  'setMaxScripts': 'setMaxScripts',
  'settingsData': 'datosAjustes',
  'setSettingsData': 'setDatosAjustes',
  'appConfigData': 'datosConfiguracionApp',
  'setAppConfigData': 'setDatosConfiguracionApp',
  'settingsError': 'errorAjustes',
  'setSettingsError': 'setErrorAjustes',
  'username': 'nombreUsuario',
  'setUsername': 'setNombreUsuario',
  'preFullscreenState': 'estadoPrevioPantallaCompleta',
  'setPreFullscreenState': 'setEstadoPrevioPantallaCompleta',
  'search': 'busqueda',
  'setSearch': 'setBusqueda',
  'fetchWithToken': 'fetchConToken',
  'fetchScripts': 'obtenerScripts',
  'filteredScripts': 'scriptsFiltrados',
  'runScript': 'ejecutarScript',
  'addLog': 'añadirRegistro',
  'clearConsole': 'limpiarConsola',
  'stopAll': 'detenerTodo',
  'handleGlobalClose': 'manejarCierreGlobal',
  'confirmGlobalClose': 'confirmarCierreGlobal',
  'toggleFullscreen': 'alternarPantallaCompleta',
  'ScriptItem': 'ElementoScript',
  'onRun': 'alEjecutar',
  'activeCount': 'conteoActivo',
  'Console': 'Consola',
  'onClose': 'alCerrar',
  'permissionsPath': 'rutaPermisos',
  'scriptsFolder': 'carpetaScripts',
  'autoStartOnWindows': 'inicioAutomaticoWindows',
  'currentUserRole': 'rolUsuarioActual',
  'maxSimultaneousScripts': 'maxScriptsSimultaneos',
  'users': 'usuarios'
};

for (let [key, val] of Object.entries(wordReplacements)) {
  const regex = new RegExp(`\\b${key}\\b`, 'g');
  content = content.replace(regex, val);
}

const exactReplacements = {
  'name: string': 'nombre: string',
  "type: 'powershell' | 'batch'": "tipo: 'powershell' | 'batch'",
  "type: 'info' | 'error' | 'success'": "tipo: 'info' | 'error' | 'exito'",
  'message: string': 'mensaje: string',
  "'normal' | 'minimized' | 'closed'": "'normal' | 'minimizado' | 'cerrado'",
  "'scripts' | 'settings'": "'scripts' | 'ajustes'",
  '/api/user': '/api/usuario',
  '/api/settings': '/api/ajustes',
  '/api/app-config': '/api/configuracion-app',
  'data.role': 'data.rol',
  'script.name': 'script.nombre',
  'script.type': 'script.tipo',
  's.name': 's.nombre',
  's.type': 's.tipo',
  'log.type': 'log.tipo',
  'log.message': 'log.mensaje',
  'c.id': 'c.id',
  'settingsResponse.permissions': 'settingsResponse.permisos',
  "'success'": "'exito'",
  "'info'": "'info'",
  "'error'": "'error'"
};

for (let [key, val] of Object.entries(exactReplacements)) {
  content = content.split(key).join(val);
}

fs.writeFileSync('src/App.tsx', content);
console.log('Refactor completed');
