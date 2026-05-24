/**
 * @license
 * SPDX-License-Identifier: Apache-2.0
 */

import { useState, useEffect, useRef, type FC, type KeyboardEvent } from 'react';
import { 
  X,
  Search, 
  Terminal, 
  Square, 
  Play, 
  Trash2, 
  Activity, 
  Settings, 
  Clock, 
  FileText,
  Monitor,
  LayoutGrid,
  Lock,
  AlertTriangle
} from 'lucide-react';
import { motion, AnimatePresence } from 'motion/react';

interface Script {
  id: string;
  nombre: string;
  tipo: 'powershell' | 'batch';
  ultimaEjecucion?: string;
  estaBloqueado?: boolean;
}

interface EntradaRegistro {
  tipo: 'info' | 'error' | 'exito';
  mensaje: string;
  color?: string;
}

interface EstadoConsola {
  id: number;
  scriptActivo?: Script;
  ejecucionId?: string;
  finalizado?: boolean;
  registros: EntradaRegistro[];
}

export default function App() {
  const [scripts, setScripts] = useState<Script[]>([]);
  const [busqueda, setBusqueda] = useState('');
  const [consolas, setConsolas] = useState<EstadoConsola[]>([
    { id: 1, registros: [] },
  ]);
  const [conteoEjecucion, setConteoEjecucion] = useState(0);
  const fuentesEventosRef = useRef<Map<number, EventSource>>(new Map());

  const [vistaActual, setVistaActual] = useState<'scripts' | 'ajustes'>('scripts');
  const [anchoVentana, setAnchoVentana] = useState(typeof window !== 'undefined' ? window.innerWidth : 1280);
  
  const [errorConexion, setErrorConexion] = useState(false);
  
  // Settings / Permissions
  const [rolUsuario, setRolUsuario] = useState<string>('nominal');
  const [maxScripts, setMaxScripts] = useState(5);
  const [datosAjustes, setDatosAjustes] = useState<any>(null);
  const [datosConfiguracionApp, setDatosConfiguracionApp] = useState<any>(null);
  const [errorAjustes, setErrorAjustes] = useState('');
  const [nombreUsuario, setNombreUsuario] = useState<string>('');
  const [permiteDesbloqueoEmergencia, setPermiteDesbloqueoEmergencia] = useState(false);
  const [mostrarDesbloqueoMaestro, setMostrarDesbloqueoMaestro] = useState(false);
  const [tokenMaestro, setTokenMaestro] = useState('');
  const [mensajeTokenMaestro, setMensajeTokenMaestro] = useState('');

  const fetchConToken = (url: string, options: RequestInit = {}) => {
    const token = localStorage.getItem('admin_token');
    const headers = new Headers(options.headers || {});
    if (token) {
      headers.set('Authorization', `Bearer ${token}`);
    }
    return fetch(url, { ...options, headers });
  };

  const obtenerScripts = () => {
    fetchConToken('/api/scripts')
      .then(res => res.json())
      .then(data => {
        if (!data.error) setScripts(data);
      })
      .catch(err => console.error('Error fetching scripts:', err));
  };

  useEffect(() => {
    fetchConToken('/api/usuario')
      .then(async res => {
        const data = await res.json();
        if (!res.ok) {
          throw new Error(data.error || 'Server error');
        }
        return data;
      })
      .then(data => {
        setRolUsuario(data.rol || 'nominal');
        setMaxScripts(data.maxScriptsSimultaneos || 5);
        if (data.nombreUsuario) {
          setNombreUsuario(data.nombreUsuario);
        }
        setPermiteDesbloqueoEmergencia(!!data.permiteDesbloqueoEmergencia);
        setErrorConexion(false);
      })
      .catch(err => {
        console.error('Error fetching user rol:', err);
        setErrorConexion(true);
      });

    obtenerScripts();
  }, []);

  useEffect(() => {
    const manejarRedimension = () => setAnchoVentana(window.innerWidth);
    window.addEventListener('resize', manejarRedimension);
    return () => window.removeEventListener('resize', manejarRedimension);
  }, []);

  const isCompact = anchoVentana < 768;

  const scriptsFiltrados = scripts.filter(s => 
    s.nombre.toLowerCase().includes(busqueda.toLowerCase())
  );

  const ejecutarScript = async (script: Script) => {
    if (script.estaBloqueado) return;
    
    const currentlyRunning = consolas.filter(c => c.scriptActivo && !c.finalizado).length;
    if (currentlyRunning >= maxScripts) {
      alert(`Has alcanzado el límite máximo de ${maxScripts} scripts simultáneos perimitido por tu usuario.`);
      return;
    }

    const targetIndex = consolas.findIndex(c => !c.scriptActivo);
    const useId = targetIndex !== -1 ? consolas[targetIndex].id : (consolas.length > 0 ? Math.max(...consolas.map(c => c.id)) + 1 : 1);

    const newLogs: EntradaRegistro[] = [
      { tipo: 'info', mensaje: `Font font JetBrains Mono` },
      { tipo: 'info', mensaje: `### Script-${script.nombre}` },
      { tipo: 'exito', mensaje: `> Iniciando ${script.nombre}... (#B5CEA8)`, color: '#B5CEA8' },
      { tipo: 'info', mensaje: `> Conectando a servidor...` },
    ];

    setConsolas(curr => {
      const idx = curr.findIndex(c => c.id === useId);
      if (idx !== -1) {
        const siguiente = [...curr];
        siguiente[idx] = {
          ...siguiente[idx],
          scriptActivo: script,
          finalizado: false,
          registros: newLogs
        };
        return siguiente;
      } else {
        return [...curr, { id: useId, scriptActivo: script, finalizado: false, registros: newLogs }];
      }
    });

    setConteoEjecucion(prev => prev + 1);

    try {
      const respuesta = await fetchConToken('/api/ejecuciones', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ scriptId: script.id })
      });
      const datos = await respuesta.json();

      if (!respuesta.ok || datos.error) {
        añadirRegistro(useId, { tipo: 'error', mensaje: `> ${datos.error || 'Error al iniciar la ejecución.'}`, color: '#F44747' });
        marcarConsolaFinalizada(useId);
        return;
      }

      setConsolas(curr => curr.map(c => c.id === useId ? { ...c, ejecucionId: datos.id } : c));
      conectarEventosEjecucion(useId, datos.id);
    } catch (err) {
      añadirRegistro(useId, { tipo: 'error', mensaje: '> No se pudo conectar con el backend de ejecución.', color: '#F44747' });
      marcarConsolaFinalizada(useId);
    }
  };

  const añadirRegistro = (consoleId: number, log: EntradaRegistro) => {
    setConsolas(curr => {
      const idx = curr.findIndex(c => c.id === consoleId);
      if (idx === -1) return curr;
      const siguiente = [...curr];
      siguiente[idx] = {
        ...siguiente[idx],
        registros: [...siguiente[idx].registros, log]
      };
      return siguiente;
    });
  };

  const marcarConsolaFinalizada = (consoleId: number) => {
    fuentesEventosRef.current.get(consoleId)?.close();
    fuentesEventosRef.current.delete(consoleId);
    setConsolas(curr => curr.map(c => c.id === consoleId ? { ...c, finalizado: true } : c));
    setConteoEjecucion(prev => Math.max(0, prev - 1));
  };

  const conectarEventosEjecucion = (consoleId: number, ejecucionId: string) => {
    let terminado = false;
    let reconexionAvisada = false;
    const fuente = new EventSource(`/api/ejecuciones/${ejecucionId}/eventos`);
    fuentesEventosRef.current.set(consoleId, fuente);

    fuente.onopen = () => {
      reconexionAvisada = false;
    };

    fuente.onmessage = (evento) => {
      const datos = JSON.parse(evento.data);
      añadirRegistro(consoleId, {
        tipo: datos.tipo || 'info',
        mensaje: datos.mensaje || '',
        color: datos.color
      });

      if (datos.finalizado) {
        terminado = true;
        marcarConsolaFinalizada(consoleId);
      }
    };

    fuente.onerror = () => {
      if (terminado) {
        fuente.close();
        fuentesEventosRef.current.delete(consoleId);
        return;
      }

      if (!reconexionAvisada) {
        reconexionAvisada = true;
        añadirRegistro(consoleId, { tipo: 'info', mensaje: '> Reconectando con la ejecución...' });
      }
    };
  };

  const limpiarConsola = (id: number) => {
    const consoleToClear = consolas.find(c => c.id === id);
    if (consoleToClear?.ejecucionId && !consoleToClear.finalizado) {
      fetchConToken(`/api/ejecuciones/${consoleToClear.ejecucionId}/cancelar`, { method: 'POST' }).catch(() => {});
    }
    fuentesEventosRef.current.get(id)?.close();
    fuentesEventosRef.current.delete(id);

    setConsolas(curr => {
      if (curr.length > 1) {
        return curr.filter(c => c.id !== id);
      }
      return curr.map(c => 
        c.id === id ? { ...c, registros: [], scriptActivo: undefined } : c
      );
    });
    setConteoEjecucion(prev => {
      return consoleToClear?.scriptActivo && !consoleToClear.finalizado ? Math.max(0, prev - 1) : prev;
    });
  };

  const detenerTodo = () => {
    consolas.forEach(c => {
      if (c.ejecucionId && !c.finalizado) {
        fetchConToken(`/api/ejecuciones/${c.ejecucionId}/cancelar`, { method: 'POST' }).catch(() => {});
      }
    });
    fuentesEventosRef.current.forEach(fuente => fuente.close());
    fuentesEventosRef.current.clear();
    setConsolas([{ id: 1, registros: [] }]);
    setConteoEjecucion(0);
  };

  const enviarEntradaConsola = async (consola: EstadoConsola, texto: string) => {
    if (!consola.ejecucionId || consola.finalizado || !texto.trim()) return;
    añadirRegistro(consola.id, { tipo: 'info', mensaje: '> Entrada enviada' });
    await fetchConToken(`/api/ejecuciones/${consola.ejecucionId}/entrada`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ texto })
    }).catch(() => {
      añadirRegistro(consola.id, { tipo: 'error', mensaje: '> No se pudo enviar la entrada al proceso.', color: '#F44747' });
    });
  };

  const guardarAjustes = async () => {
    if (!datosAjustes || !datosConfiguracionApp) return;
    setErrorAjustes('');

    const permisosNormalizados = {
      ...datosAjustes,
      usuarios: (datosAjustes.usuarios || [])
        .map((usuario: any) => ({
          ...usuario,
          nombreUsuario: (usuario.nombreUsuario || '').trim(),
          rol: usuario.rol || 'nominal',
          maxScriptsSimultaneos: Math.max(1, parseInt(usuario.maxScriptsSimultaneos, 10) || 1)
        }))
        .filter((usuario: any) => usuario.nombreUsuario.length > 0)
    };

    try {
      const respuestaConfiguracion = await fetchConToken('/api/configuracion-app', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(datosConfiguracionApp)
      });
      const datosConfiguracion = await respuestaConfiguracion.json();
      if (datosConfiguracion.error) {
        setErrorAjustes(datosConfiguracion.error);
        return;
      }

      const respuestaPermisos = await fetchConToken('/api/ajustes', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(permisosNormalizados)
      });
      const datosPermisos = await respuestaPermisos.json();
      if (datosPermisos.error) setErrorAjustes(datosPermisos.error);
      else window.location.reload();
    } catch {
      setErrorAjustes('Error al guardar.');
    }
  };

  const guardarConEnter = (evento: KeyboardEvent<HTMLInputElement>) => {
    if (evento.key !== 'Enter') return;
    evento.preventDefault();
    guardarAjustes();
  };

  const exportarConfiguracion = async () => {
    setErrorAjustes('');
    try {
      const respuesta = await fetchConToken('/api/configuracion-paquete/exportar');
      const datos = await respuesta.json();
      if (!respuesta.ok || datos.error) {
        setErrorAjustes(datos.error || 'No se pudo exportar la configuración.');
        return;
      }

      const bytes = Uint8Array.from(atob(datos.contenidoBase64), caracter => caracter.charCodeAt(0));
      const blob = new Blob([bytes], { type: 'application/octet-stream' });
      const url = URL.createObjectURL(blob);
      const enlace = document.createElement('a');
      enlace.href = url;
      enlace.download = datos.nombreArchivo || 'LanzadorScripts.lanzadorconfig';
      enlace.click();
      URL.revokeObjectURL(url);
    } catch {
      setErrorAjustes('No se pudo exportar la configuración.');
    }
  };

  const añadirUsuario = () => {
    setDatosAjustes({
      ...datosAjustes,
      usuarios: [
        ...(datosAjustes?.usuarios || []),
        { id: Date.now().toString(), nombreUsuario: '', rol: 'nominal', maxScriptsSimultaneos: 5 }
      ]
    });
  };

  const desbloquearTokenMaestro = async () => {
    setMensajeTokenMaestro('');
    try {
      const respuesta = await fetch('/api/token-maestro/desbloquear', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: tokenMaestro.trim() })
      });
      const datos = await respuesta.json();
      if (!respuesta.ok || datos.error) {
        setMensajeTokenMaestro(datos.error || 'Token maestro no valido.');
        return;
      }

      setRolUsuario('admin');
      setMostrarDesbloqueoMaestro(false);
      setTokenMaestro('');
      obtenerScripts();
    } catch {
      setMensajeTokenMaestro('No se pudo validar el token maestro.');
    }
  };

  return (
    <div className="flex bg-[#0f1115] w-full h-screen overflow-hidden relative">
      {mostrarDesbloqueoMaestro && (
        <div className="fixed inset-0 z-[100] bg-black/60 backdrop-blur-sm flex items-center justify-center">
          <div className="bg-[#1e2028] border border-white/10 rounded-xl p-5 shadow-2xl max-w-sm w-full mx-4">
            <h3 className="text-lg font-semibold text-white mb-4">Token de emergencia</h3>
            <textarea
              value={tokenMaestro}
              onChange={(e) => setTokenMaestro(e.target.value)}
              className="w-full h-28 bg-[#0f1115] border border-white/10 rounded-lg px-3 py-2 text-xs text-white focus:outline-none focus:ring-1 focus:ring-brand-accent transition-all font-mono resize-none"
              placeholder="Pega aqui el token de emergencia firmado"
            />
            {mensajeTokenMaestro && (
              <p className="text-xs text-brand-danger mt-3">{mensajeTokenMaestro}</p>
            )}
            <div className="flex gap-3 justify-end items-center">
              <button
                onClick={() => {
                  setMostrarDesbloqueoMaestro(false);
                  setMensajeTokenMaestro('');
                }}
                className="px-4 py-2 rounded-lg text-sm font-medium text-gray-300 hover:text-white hover:bg-white/5 transition-colors"
              >
                Cancelar
              </button>
              <button
                onClick={desbloquearTokenMaestro}
                className="px-4 py-2 rounded-lg bg-brand-accent text-white hover:bg-brand-accent/90 transition-colors text-sm font-medium"
              >
                Desbloquear
              </button>
            </div>
          </div>
        </div>
      )}

        <div className="flex-1 flex flex-col min-w-0 overflow-hidden bg-brand-bg relative">
          
          {errorConexion && (
            <div className="absolute top-0 left-0 right-0 z-50 bg-red-500/10 border-b border-red-500/20 text-red-200 px-6 py-2.5 flex items-center justify-between backdrop-blur-md shadow-[0_4px_15px_rgba(239,68,68,0.1)]">
              <div className="flex items-center gap-2 text-sm max-w-full">
                <AlertTriangle className="w-4 h-4 text-red-400 shrink-0" />
                <span className="font-medium truncate">No se puede establecer conexión con el servidor. Modo Offline activado.</span>
              </div>
              <button onClick={() => setErrorConexion(false)} className="p-1 hover:bg-white/10 rounded-md transition-colors shrink-0">
                <X className="w-3.5 h-3.5" />
              </button>
            </div>
          )}

          {/* Header Section */}
          <header className={`h-14 flex items-center justify-between px-6 border-b border-white/5 bg-brand-bg/50 flex-shrink-0 transition-transform ${errorConexion ? 'mt-10' : ''}`}>
            <div className="flex items-center gap-4">
              <h1 className="text-lg font-medium tracking-tight text-gray-100">Hola, {nombreUsuario}</h1>
            </div>
            
            <div className="flex items-center gap-4">
              <button 
                onClick={detenerTodo}
                className="flex items-center gap-2 px-3 py-1.5 rounded-md border border-brand-danger/30 text-brand-danger hover:bg-brand-danger/10 transition-colors text-xs font-medium"
              >
                <Square className="w-3.5 h-3.5" />
                Detener Todo
              </button>
              <div className="flex items-center gap-2 text-[11px] font-mono">
                <span className="text-gray-500">Ejecutando:</span>
                <span className="px-2 py-0.5 rounded bg-gray-800 text-gray-300">{conteoEjecucion}</span>
                <span className="text-gray-500 ml-2 hidden sm:inline">Pendientes:</span>
                <span className="px-2 py-0.5 rounded bg-gray-800 text-gray-300 hidden sm:inline">0</span>
                <span className="text-gray-500 ml-2 hidden sm:inline">Max:</span>
                <span className="px-2 py-0.5 rounded bg-gray-800 text-gray-300 hidden sm:inline">{maxScripts}</span>
              </div>
            </div>
          </header>

          {/* Workspace Layout */}
          <div className={`flex-1 flex overflow-hidden min-h-0 p-4 sm:p-6 gap-4 sm:gap-6 ${isCompact ? 'flex-col' : 'flex-row'}`}>
            {vistaActual === 'ajustes' ? (
              <div className="flex-1 bg-brand-card/30 border border-white/5 rounded-xl p-6 sm:p-8 overflow-y-auto custom-scrollbar flex flex-col relative w-full h-full max-w-4xl mx-auto">
                <div className="flex justify-between items-center mb-8 border-b border-white/5 pb-4">
                  <div>
                    <h2 className="text-2xl font-semibold text-white tracking-tight">Configuración Avanzada</h2>
                    <p className="text-sm text-gray-400 mt-1">Ajustes globales y gestión de permisos</p>
                  </div>
                  <button onClick={() => setVistaActual('scripts')} className="p-2 rounded-lg bg-white/5 hover:bg-white/10 text-gray-300 transition-colors group flex items-center gap-2">
                    <X className="w-5 h-5 group-hover:text-white" />
                    <span className="text-sm pr-1">Volver</span>
                  </button>
                </div>
                
                {errorAjustes && (
                  <div className="p-3 mb-6 text-sm text-red-400 bg-red-400/10 border border-red-400/20 rounded-lg">
                    {errorAjustes}
                  </div>
                )}

                <div className="space-y-8 max-w-2xl">
                  {/* General ajustes */}
                  <section>
                    <h3 className="text-sm font-medium text-gray-200 uppercase tracking-wider mb-4">Ajustes Generales</h3>
                    <div className="bg-black/20 border border-white/5 rounded-lg p-5 space-y-5">
                      <div className="flex items-center justify-between">
                        <div>
                          <label className="text-sm font-medium text-gray-200 block">Abrir automáticamente al iniciar Windows</label>
                          <span className="text-xs text-gray-500">Ejecutar esta aplicación en segundo plano al encender el equipo.</span>
                        </div>
                        <label className="relative inline-flex items-center cursor-pointer">
                          <input 
                            type="checkbox" 
                            className="sr-only peer"
                            checked={datosAjustes?.inicioAutomaticoWindows || false}
                            onChange={(e) => setDatosAjustes({ ...datosAjustes, inicioAutomaticoWindows: e.target.checked })}
                          />
                          <div className="w-11 h-6 bg-gray-700 peer-focus:outline-none rounded-full peer peer-checked:after:translate-x-full peer-checked:after:border-white after:content-[''] after:absolute after:top-[2px] after:left-[2px] after:bg-white after:border-gray-300 after:border after:rounded-full after:h-5 after:w-5 after:transition-all peer-checked:bg-brand-accent"></div>
                        </label>
                      </div>
                    </div>
                  </section>

                  {/* Config files ajustes */}
                  <section>
                    <h3 className="text-sm font-medium text-gray-200 uppercase tracking-wider mb-4">Rutas de Configuración</h3>
                    <div className="bg-black/20 border border-white/5 rounded-lg p-5 space-y-4">
                      <div>
                        <label className="text-sm font-medium text-gray-200 block mb-1">Ruta del archivo de Permisos (JSON)</label>
                        <input
                          type="text"
                          value={datosConfiguracionApp?.rutaPermisos || 'permissions.json'}
                          onChange={(e) => setDatosConfiguracionApp({ ...datosConfiguracionApp, rutaPermisos: e.target.value })}
                          onKeyDown={guardarConEnter}
                          className="w-full bg-[#0f1115] border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:ring-1 focus:ring-brand-accent transition-all font-mono"
                          placeholder="./permissions.json"
                        />
                        <p className="mt-1 text-xs text-gray-500">Ruta relativa o absoluta para almacenar permissions.json en el servidor</p>
                      </div>
                      <div>
                        <label className="text-sm font-medium text-gray-200 block mb-1">Ruta de la carpeta de Scripts</label>
                        <input
                          type="text"
                          value={datosConfiguracionApp?.carpetaScripts || './scripts'}
                          onChange={(e) => setDatosConfiguracionApp({ ...datosConfiguracionApp, carpetaScripts: e.target.value })}
                          onKeyDown={guardarConEnter}
                          className="w-full bg-[#0f1115] border border-white/10 rounded-lg px-3 py-2 text-sm text-white focus:outline-none focus:ring-1 focus:ring-brand-accent transition-all font-mono"
                          placeholder="./scripts"
                        />
                        <p className="mt-1 text-xs text-gray-500">Ruta relativa o absoluta para la carpeta que contiene los scripts</p>
                      </div>
                      <div className="pt-2">
                        <button
                          onClick={exportarConfiguracion}
                          className="px-3 py-2 rounded-lg bg-gray-800 text-gray-300 hover:bg-gray-700 hover:text-white transition-colors text-xs font-medium"
                        >
                          Exportar configuración
                        </button>
                      </div>
                    </div>
                  </section>

                  {/* Users / Permissions ajustes */}
                  <section>
                    <h3 className="text-sm font-medium text-gray-200 uppercase tracking-wider mb-4">Permisos y Usuarios</h3>
                    <div className="bg-black/20 border border-white/5 rounded-lg overflow-hidden">
                      <div className="p-5">
                        <h4 className="text-xs font-medium text-gray-400 mb-3">LISTA DE USUARIOS</h4>
                        <div className="space-y-3">
                          {datosAjustes?.usuarios?.map((usr: any, idx: number) => (
                            <div key={idx} className="flex items-center justify-between p-3 rounded-lg border border-white/5 bg-black/40">
                              <div className="flex-1 mr-4">
                                <input
                                  type="text"
                                  value={usr.nombreUsuario}
                                  onChange={(e) => {
                                    const newUsers = [...datosAjustes.usuarios];
                                    newUsers[idx].nombreUsuario = e.target.value;
                                    setDatosAjustes({ ...datosAjustes, usuarios: newUsers });
                                  }}
                                  onKeyDown={guardarConEnter}
                                  className="bg-transparent border-b border-white/10 focus:border-brand-accent text-sm text-gray-300 font-medium focus:outline-none w-full"
                                  placeholder="Usuario Windows"
                                />
                              </div>
                              <div className="flex items-center gap-3">
                                <div className="flex items-center gap-2" title="Max scripts">
                                  <span className="text-xs text-gray-500 hidden sm:inline">Max:</span>
                                  <input
                                    type="number"
                                    min={1}
                                    value={usr.maxScriptsSimultaneos || 5}
                                    onChange={(e) => {
                                    const newUsers = [...datosAjustes.usuarios];
                                    newUsers[idx].maxScriptsSimultaneos = parseInt(e.target.value) || 1;
                                    setDatosAjustes({ ...datosAjustes, usuarios: newUsers });
                                  }}
                                    onKeyDown={guardarConEnter}
                                    className="bg-[#1e2028] border border-white/10 rounded px-2 py-1 text-xs text-white w-14 focus:outline-none focus:ring-1 focus:ring-brand-accent"
                                  />
                                </div>
                                <select
                                  value={usr.rol}
                                  onChange={(e) => {
                                    const newUsers = [...datosAjustes.usuarios];
                                    newUsers[idx].rol = e.target.value;
                                    setDatosAjustes({ ...datosAjustes, usuarios: newUsers });
                                  }}
                                  className="bg-[#1e2028] border border-white/10 rounded text-xs text-gray-300 px-2 py-1 focus:outline-none focus:ring-1 focus:ring-brand-accent"
                                >
                                  <option value="admin">Admin</option>
                                  <option value="nominal">Nominal</option>
                                </select>
                                <button
                                  onClick={() => {
                                    const newUsers = datosAjustes.usuarios.filter((_: any, i: number) => i !== idx);
                                    setDatosAjustes({ ...datosAjustes, usuarios: newUsers });
                                  }}
                                  className="text-red-400/70 hover:text-red-400 p-1 transition-colors"
                                  title="Eliminar usuario"
                                >
                                  <Trash2 className="w-4 h-4" />
                                </button>
                              </div>
                            </div>
                          ))}
                          <button
                            onClick={añadirUsuario}
                            className="mt-3 text-xs text-brand-accent hover:text-brand-accent/80 font-medium px-2 py-1 rounded-lg hover:bg-white/5 transition-colors"
                          >
                            + Añadir usuario
                          </button>
                        </div>
                      </div>
                    </div>
                  </section>
                </div>

                <div className="fixed bottom-8 right-8 flex gap-3">
                  <button
                    onClick={() => setVistaActual('scripts')}
                    className="px-5 py-2.5 rounded-lg text-sm font-medium text-gray-300 bg-black/50 border border-white/10 hover:text-white hover:bg-white/5 transition-colors shadow-xl backdrop-blur-sm"
                  >
                    Descartar Cambios
                  </button>
                  <button
                    onClick={guardarAjustes}
                    className="px-5 py-2.5 rounded-lg bg-brand-accent text-white hover:bg-brand-accent/90 transition-colors text-sm font-medium shadow-xl shadow-brand-accent/20 border border-transparent backdrop-blur-sm"
                  >
                    Guardar Cambios
                  </button>
                </div>
              </div>
            ) : (
            <>
            {/* Script Library Sidebar */}
            <aside className={`flex flex-col shrink-0 min-h-0 overflow-hidden ${isCompact ? 'w-full h-[45%]' : 'w-80 line-clamp-none'}`}>
              <div className="relative group shrink-0 mb-4">
                <Search className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-gray-500 group-focus-within:text-brand-accent transition-colors" />
                <input 
                  type="text" 
                  placeholder="Buscar scripts..." 
                  value={busqueda}
                  onChange={(e) => setBusqueda(e.target.value)}
                  className="w-full bg-brand-card/50 border border-white/10 rounded-lg pl-10 pr-4 py-2 text-sm focus:outline-none focus:ring-1 focus:ring-brand-accent transition-all ring-offset-0"
                />
              </div>

              <div className="flex-1 min-h-0 overflow-y-auto pr-2 space-y-4 custom-scrollbar">
                {scriptsFiltrados.map(script => {
                  const conteoActivo = consolas.filter(c => c.scriptActivo?.id === script.id && !c.finalizado).length;
                  return (
                    <ElementoScript 
                      key={script.id} 
                      script={script} 
                      alEjecutar={() => ejecutarScript(script)} 
                      conteoActivo={conteoActivo}
                    />
                  );
                })}
              </div>

              <div className="pt-4 mt-4 border-t border-white/5 flex items-center justify-between shrink-0 pb-1">
                {rolUsuario === 'admin' ? (
                  <button 
                    onClick={() => {
                      Promise.all([
                        fetchConToken('/api/ajustes').then(r => r.json()),
                        fetchConToken('/api/configuracion-app').then(r => r.json())
                      ]).then(([settingsResponse, configResponse]) => {
                        if (settingsResponse.error) setErrorAjustes(settingsResponse.error);
                        else {
                          setDatosAjustes(settingsResponse.permisos);
                          setDatosConfiguracionApp(configResponse);
                          setVistaActual('ajustes');
                        }
                      }).catch(err => setErrorAjustes('Error al cargar configuración.'));
                    }}
                    className="flex items-center gap-2 text-gray-500 hover:text-white transition-colors text-xs p-2 rounded-lg hover:bg-white/5"
                  >
                    <Settings className="w-4 h-4" />
                    <span>Configuración Avanzada</span>
                  </button>
                ) : permiteDesbloqueoEmergencia ? (
                  <button
                    onClick={() => setMostrarDesbloqueoMaestro(true)}
                    className="text-xs text-gray-600 flex items-center gap-2 p-2 rounded-lg hover:bg-white/5 hover:text-gray-300 transition-colors"
                  >
                    <Lock className="w-3.5 h-3.5" />
                    <span>Token de emergencia</span>
                  </button>
                ) : (
                  <div className="text-xs text-gray-600 flex items-center gap-2 p-2">
                    <Lock className="w-3.5 h-3.5" />
                    <span>Sin permisos de configuración</span>
                  </div>
                )}
                <div className="text-[10px] text-gray-600 font-mono">v1.2.0</div>
              </div>
            </aside>

            {/* Consola Grid */}
            <section className={`flex-1 min-h-0 grid gap-4 overflow-y-auto pr-2 custom-scrollbar ${
              consolas.length === 1 ? 'grid-cols-1' : 
              (consolas.length === 2 && !isCompact) ? 'grid-cols-1 xl:grid-cols-2' :
              !isCompact ? 'grid-cols-1 xl:grid-cols-2 auto-rows-[45%]' : 
              'grid-cols-1 auto-rows-[300px]'
            }`}>
              <AnimatePresence mode="popLayout">
                {consolas.map((c) => (
                  <motion.div
                    key={c.id}
                    layout
                    initial={{ opacity: 0, scale: 0.95 }}
                    animate={{ opacity: 1, scale: 1 }}
                    exit={{ opacity: 0, scale: 0.95 }}
                    transition={{ duration: 0.2 }}
                    className="min-h-[300px] h-full"
                  >
                    <Consola data={c} alCerrar={() => limpiarConsola(c.id)} alEnviarEntrada={(texto) => enviarEntradaConsola(c, texto)} />
                  </motion.div>
                ))}
              </AnimatePresence>
            </section>
            </>
            )}
          </div>
        </div>
    </div>
  );
}

const ElementoScript: FC<{ script: Script; alEjecutar: () => void | Promise<void>; conteoActivo: number }> = ({ script, alEjecutar, conteoActivo }) => {
  return (
    <motion.div 
      layout
      className={`shrink-0 bg-brand-card/30 border border-white/5 rounded-xl p-4 flex flex-col gap-4 transition-colors ${script.estaBloqueado ? 'opacity-50 grayscale select-none' : 'hover:border-white/10'}`}
    >
      <div className="flex items-start justify-between">
        <div className="flex items-center gap-3">
          <div className={`p-2 rounded-lg ${script.tipo === 'powershell' ? 'bg-blue-500/10 text-blue-400' : 'bg-gray-500/10 text-gray-400'}`}>
            {script.tipo === 'powershell' ? <Terminal className="w-5 h-5" /> : <Monitor className="w-5 h-5" />}
          </div>
          <div>
            <h3 className="text-sm font-medium flex items-center gap-2">
              {script.nombre}
              {script.estaBloqueado && <Lock className="w-3.5 h-3.5 text-red-400" />}
            </h3>
            <p className="text-[10px] text-gray-500 font-mono uppercase tracking-wider">{script.tipo}</p>
          </div>
        </div>
        {conteoActivo > 0 && !script.estaBloqueado && (
          <motion.div 
            initial={{ scale: 0.8, opacity: 0 }}
            animate={{ scale: 1, opacity: 1 }}
            className="text-[10px] bg-sky-500/10 text-sky-400 px-2 py-0.5 rounded border border-sky-500/20"
          >
            Ejecutando: {conteoActivo}
          </motion.div>
        )}
      </div>

      <div className="flex gap-2">
        <button 
          onClick={script.estaBloqueado ? undefined : alEjecutar}
          disabled={script.estaBloqueado}
          className={`flex-1 flex items-center justify-center gap-2 py-2 rounded-lg transition-all group ${
            script.estaBloqueado 
            ? 'bg-gray-800/50 text-gray-600 cursor-not-allowed' 
            : 'bg-gray-800 text-gray-400 hover:bg-gray-700 hover:text-white'
          }`}
        >
          {!script.estaBloqueado && <Play className="w-3.5 h-3.5 group-hover:fill-current" />}
          <span className="text-xs">{script.estaBloqueado ? 'Acceso Denegado' : 'Ejecutar Script'}</span>
        </button>
      </div>
    </motion.div>
  );
};

function Consola({ data, alCerrar, alEnviarEntrada }: { data: EstadoConsola; alCerrar: () => void; alEnviarEntrada: (texto: string) => void | Promise<void> }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const [showConfirm, setShowConfirm] = useState(false);
  const [entrada, setEntrada] = useState('');

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [data.registros]);

  const handleClose = () => {
    if (data.scriptActivo && !data.finalizado) {
      setShowConfirm(true);
    } else {
      alCerrar();
    }
  };

  const enviarEntrada = () => {
    const texto = entrada.trimEnd();
    if (!texto) return;
    setEntrada('');
    alEnviarEntrada(texto);
  };

  return (
    <div className="bg-brand-card/40 border border-white/5 rounded-xl flex flex-col h-full overflow-hidden relative group">
      <div className="flex items-center justify-between px-4 py-3 border-b border-white/5 bg-white/2">
        <div className="flex items-center gap-2">
          <span className="text-xs font-medium text-gray-400">Consola #{data.id}</span>
          {data.scriptActivo && (
            <span className="text-xs text-brand-accent font-mono truncate max-w-[120px]">
              - {data.scriptActivo.nombre}
            </span>
          )}
        </div>
        <button 
          onClick={handleClose}
          className="p-1 rounded bg-gray-800/50 hover:bg-brand-danger/20 text-gray-500 hover:text-brand-danger transition-colors group/close"
          title="Cerrar y detener script"
        >
          <X className="w-3.5 h-3.5" />
        </button>
      </div>

      <div className="flex-1 flex flex-col min-h-0 bg-black/20">
        <div 
          ref={scrollRef}
          className="flex-1 p-4 font-mono text-[11px] overflow-y-auto space-y-1.5 leading-relaxed custom-scrollbar relative"
        >
        <AnimatePresence>
          {showConfirm && (
            <motion.div 
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              exit={{ opacity: 0 }}
              className="absolute inset-0 z-10 backdrop-blur-sm bg-black/60 flex flex-col items-center justify-center p-6 text-center font-sans"
            >
              <div className="bg-[#1e2028] border border-white/10 rounded-xl p-5 shadow-2xl max-w-[240px] w-full">
                <h3 className="text-sm font-semibold text-white mb-2">¿Detener ejecución?</h3>
                <p className="text-xs text-gray-400 mb-5 leading-relaxed">
                  Vas a detener "{data.scriptActivo?.nombre}". Esto matará el proceso actual.
                </p>
                <div className="flex gap-2 justify-center">
                  <button
                    onClick={() => setShowConfirm(false)}
                    className="flex-1 py-2 rounded-lg text-xs font-medium text-gray-300 hover:text-white hover:bg-white/5 transition-colors"
                  >
                    Cancelar
                  </button>
                  <button
                    onClick={() => {
                      setShowConfirm(false);
                      alCerrar();
                    }}
                    className="flex-1 py-2 rounded-lg bg-brand-danger/20 text-brand-danger border border-brand-danger/30 hover:bg-brand-danger hover:text-white transition-colors text-xs font-medium"
                  >
                    Detener
                  </button>
                </div>
              </div>
            </motion.div>
          )}
        </AnimatePresence>
        
        <AnimatePresence mode="popLayout">
          {data.registros.length === 0 ? (
            <motion.div 
              initial={{ opacity: 0 }}
              animate={{ opacity: 1 }}
              className="text-gray-700 h-full flex items-center justify-center italic"
            >
              Consola lista...
            </motion.div>
          ) : (
            data.registros.map((log, i) => (
              <motion.div 
                key={i}
                initial={{ opacity: 0, x: -5 }}
                animate={{ opacity: 1, x: 0 }}
                transition={{ duration: 0.1 }}
                style={{ color: log.color }}
                className={`${
                  log.tipo === 'error' ? 'text-red-400' : 
                  log.tipo === 'exito' ? 'text-green-400' : 
                  'text-gray-400'
                } whitespace-pre-wrap break-words`}
              >
                {log.mensaje}
              </motion.div>
            ))
          )}
        </AnimatePresence>
        </div>

        <div className="border-t border-white/5 p-2 bg-black/30 flex items-center gap-2">
          <span className="text-gray-600 font-mono text-xs">$</span>
          <input
            value={entrada}
            onChange={(e) => setEntrada(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') {
                e.preventDefault();
                enviarEntrada();
              }
            }}
            disabled={!data.ejecucionId || !!data.finalizado}
            className="flex-1 bg-transparent text-xs text-gray-300 font-mono focus:outline-none disabled:text-gray-700"
            placeholder={data.ejecucionId && !data.finalizado ? 'Enviar entrada al script...' : 'Sin proceso activo'}
          />
          <button
            onClick={enviarEntrada}
            disabled={!data.ejecucionId || !!data.finalizado || !entrada.trim()}
            className="px-3 py-1.5 rounded-md text-xs bg-gray-800 text-gray-300 hover:bg-gray-700 disabled:opacity-40 disabled:cursor-not-allowed"
          >
            Enviar
          </button>
        </div>
      </div>
    </div>
  );
}
