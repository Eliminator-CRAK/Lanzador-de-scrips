import express from "express";
import path from "path";
import fs from "fs";
import os from "os";
import crypto from "crypto";
import { createServer as createViteServer } from "vite";

async function iniciarServidor() {
  const app = express();
  const PUERTO = 3000;
  
  app.use(express.json());

  // Funcion auxiliar para leer la configuracion de la aplicacion
  const obtenerConfiguracionApp = () => {
    try {
      const datos = fs.readFileSync(path.join(process.cwd(), 'app-config.json'), 'utf8');
      return JSON.parse(datos);
    } catch (err) {
      return { rutaPermisos: 'permisos.json', carpetaScripts: './scripts' };
    }
  };

  // Funcion auxiliar para leer los permisos
  const obtenerPermisos = () => {
    try {
      const config = obtenerConfiguracionApp();
      const rutaPermisos = path.isAbsolute(config.rutaPermisos) ? config.rutaPermisos : path.join(process.cwd(), config.rutaPermisos);
      if (!fs.existsSync(rutaPermisos)) {
        return { __error: "Archivo de permisos no encontrado" };
      }
      const datos = fs.readFileSync(rutaPermisos, 'utf8');
      return JSON.parse(datos);
    } catch (err) {
      console.error('Error leyendo el archivo de permisos:', err);
      return { __error: "Error leyendo el archivo de permisos" };
    }
  };

  const obtenerUsuarioOSAcutal = () => {
    return process.env.USERNAME || process.env.USER || (os.userInfo ? os.userInfo().username : 'desconocido') || 'desconocido';
  };

  const obtenerRolUsuario = (req?: express.Request) => {
    const permisos = obtenerPermisos();
    if (permisos.__error) return 'nominal';
    const nombreUsuarioOS = obtenerUsuarioOSAcutal();
    const usuario = permisos.usuarios?.find((u: any) => u.nombreUsuario.toLowerCase() === nombreUsuarioOS.toLowerCase());
    return usuario ? usuario.rol : (permisos.rolUsuarioActual || 'nominal');
  };

  // Rutas de la API
  app.get("/api/usuario", (req, res) => {
    const permisos = obtenerPermisos();
    const nombreUsuarioOS = obtenerUsuarioOSAcutal();

    if (permisos.__error) {
      return res.status(500).json({ error: "No se puede establecer conexion con el servidor" });
    }
    
    const usuario = permisos.usuarios?.find((u: any) => u.nombreUsuario.toLowerCase() === nombreUsuarioOS.toLowerCase());
    const rol = usuario ? usuario.rol : (permisos.rolUsuarioActual || 'nominal');
    
    res.json({ 
      nombreUsuario: usuario ? usuario.nombreUsuario : nombreUsuarioOS,
      rol: rol,
      maxScriptsSimultaneos: usuario ? (usuario.maxScriptsSimultaneos || 5) : (permisos.maxScriptsSimultaneos || 5)
    });
  });

  const obtenerListaScripts = () => {
    try {
      const config = obtenerConfiguracionApp();
      const carpetaScripts = config.carpetaScripts ? (path.isAbsolute(config.carpetaScripts) ? config.carpetaScripts : path.join(process.cwd(), config.carpetaScripts)) : path.join(process.cwd(), 'scripts');
      
      if (!fs.existsSync(carpetaScripts)) {
        return [];
      }

      const archivos = fs.readdirSync(carpetaScripts);
      return archivos.map(archivo => {
        const ext = path.extname(archivo).toLowerCase();
        let tipo = 'desconocido';
        if (ext === '.ps1') tipo = 'powershell';
        else if (ext === '.bat' || ext === '.cmd') tipo = 'batch';
        
        const rutaRelativa = (config.carpetaScripts || './scripts') + '/' + archivo;
        const rutaNormalizada = rutaRelativa.replace(/\\/g, '/').replace(/\/\//g, '/').replace(/^\.\//, '');

        return {
          id: rutaNormalizada,
          nombre: archivo,
          tipo: tipo
        };
      });
    } catch (err) {
      console.error('Error leyendo la carpeta de scripts, retornando array vacio:', err);
      return [];
    }
  };

  app.get("/api/scripts", (req, res) => {
    const rolUsuario = obtenerRolUsuario(req);
    const permisos = obtenerPermisos();
    const scriptsAdmin = permisos.scriptsAdmin || [];
    const scripts = obtenerListaScripts();

    const scriptsMapeados = scripts.map((s: any) => {
      const rolRequerido = scriptsAdmin.includes(s.id) ? 'admin' : 'nominal';
      return {
        id: s.id,
        nombre: s.nombre,
        tipo: s.tipo,
        estaBloqueado: rolRequerido !== rolUsuario && rolUsuario !== 'admin'
      };
    });

    res.json(scriptsMapeados);
  });
  
  app.get("/api/ajustes", (req, res) => {
    const rol = obtenerRolUsuario(req);
    if (rol !== 'admin') {
      return res.status(403).json({ error: "Acceso denegado. Solo administradores." });
    }
    res.json({ 
      permisos: obtenerPermisos(),
      mensaje: "Datos de ajustes cargados exitosamente." 
    });
  });

  app.post("/api/ajustes", (req, res) => {
    const rol = obtenerRolUsuario(req);
    if (rol !== 'admin') {
      return res.status(403).json({ error: "Acceso denegado. Solo administradores." });
    }
    const nuevosPermisos = req.body;
    try {
      const config = obtenerConfiguracionApp();
      const rutaPermisos = path.isAbsolute(config.rutaPermisos) ? config.rutaPermisos : path.join(process.cwd(), config.rutaPermisos);
      fs.writeFileSync(rutaPermisos, JSON.stringify(nuevosPermisos, null, 2));
      res.json({ exito: true, mensaje: "Ajustes guardados exitosamente." });
    } catch (err) {
      res.status(500).json({ error: "Error al guardar ajustes." });
    }
  });

  app.get("/api/configuracion-app", (req, res) => {
    const rol = obtenerRolUsuario(req);
    if (rol !== 'admin') {
      return res.status(403).json({ error: "Acceso denegado. Solo administradores." });
    }
    res.json(obtenerConfiguracionApp());
  });

  app.post("/api/configuracion-app", (req, res) => {
    const rol = obtenerRolUsuario(req);
    if (rol !== 'admin') {
      return res.status(403).json({ error: "Acceso denegado. Solo administradores." });
    }
    try {
      const nuevaConfig = req.body;
      fs.writeFileSync(path.join(process.cwd(), 'app-config.json'), JSON.stringify(nuevaConfig, null, 2));
      res.json({ exito: true, mensaje: "Configuracion de la aplicacion guardada exitosamente." });
    } catch (err) {
      res.status(500).json({ error: "Error al guardar la configuracion." });
    }
  });

  app.get("/api/salud", (req, res) => {
    res.json({ estado: "ok" });
  });

  // Middleware Vite para desarrollo
  if (process.env.NODE_ENV !== "production") {
    const vite = await createViteServer({
      server: { middlewareMode: true, hmr: false },
      appType: "spa",
    });
    app.use(vite.middlewares);
  } else {
    const distPath = path.join(process.cwd(), 'dist');
    app.use(express.static(distPath));
    app.get('*', (req, res) => {
      res.sendFile(path.join(distPath, 'index.html'));
    });
  }

  app.listen(PUERTO, "0.0.0.0", () => {
    console.log(`Servidor ejecutandose en http://localhost:${PUERTO}`);
  });
}

iniciarServidor();

