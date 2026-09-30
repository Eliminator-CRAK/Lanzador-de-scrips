// (Autor: Alex Roman)
// Descripcion: Comprueba el cliente distribuido con API simulada, sin ejecutar scripts ni contactar con el servidor.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { chromium } = require('playwright');
const raiz = path.resolve(__dirname, '..');
const salida = path.join(raiz, 'obj', 'ValidacionCliente');

(async () => {
    const browser = await chromium.launch({ headless: true, channel: 'msedge' });
    try {
        const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
        const errores = [];
        page.on('pageerror', error => errores.push(error.message));
        let desconectado = false;
        await page.addInitScript(() => {
            window.chrome = { webview: { postMessage() {}, addEventListener() {} } };
            window.conexionesSse = 0;
            window.EventSource = class {
                constructor() {
                    window.conexionesSse++;
                    setTimeout(() => this.onmessage?.({data: JSON.stringify({mensaje: 'Salida conservada de la ejecucion', tipo: 'info'})}), 30);
                }
                close() {}
            };
        });
        await page.route('http://127.0.0.1:47899/**', async route => {
            const url = new URL(route.request().url());
            if (url.pathname.startsWith('/api/')) {
                let datos;
                let status = 200;
                if (url.pathname === '/api/scripts') {
                    if (desconectado) { status = 503; datos = { error: 'Servidor no disponible. Reintente.' }; }
                    else if (url.searchParams.get('buscar')) datos = [{ id: 'sub/profunda/Revisar.ps1', nombre: 'Revisar.ps1', tipo: 'PowerShell', carpeta: 'sub/profunda', estaBloqueado: false }];
                    else if (url.searchParams.get('carpeta') === 'sub') datos = [{ id: 'sub/Tarea.ps1', nombre: 'Tarea.ps1', tipo: 'PowerShell', carpeta: 'sub', estaBloqueado: false }];
                    else datos = [
                        {id: 'Demo.ps1', nombre: 'Demo.ps1', tipo: 'PowerShell', estaBloqueado: false},
                        {id: 'carpeta:sub', nombre: 'sub', tipo: 'carpeta', carpeta: 'sub', esCarpeta: true, estaBloqueado: false}
                    ];
                } else if (url.pathname === '/api/usuario') datos = {rol: 'admin', nombreUsuario: 'Pruebas', maxScriptsSimultaneos: 5, permiteDesbloqueoEmergencia: false};
                else if (url.pathname === '/api/ejecuciones') datos = {id: 'ejecucion-simulada'};
                else if (url.pathname === '/api/desarrollo-firmas') datos = {activo: false};
                else datos = {};
                await route.fulfill({status, contentType: 'application/json', body: JSON.stringify(datos)});
            } else if (url.pathname === '/mejoras.js') {
                await route.fulfill({contentType: 'text/javascript', body: fs.readFileSync(path.join(salida, 'mejoras.js'))});
            } else if (url.pathname.endsWith('.js')) {
                await route.fulfill({contentType: 'text/javascript', body: fs.readFileSync(path.join(salida, 'cliente.js'))});
            } else if (url.pathname.endsWith('.css')) {
                await route.fulfill({contentType: 'text/css', body: fs.readFileSync(path.join(raiz, 'ClienteWeb', url.pathname))});
            } else if (url.pathname === '/') {
                // Pagina fija de pruebas: no interpola datos en etiquetas ejecutables.
                await route.fulfill({contentType: 'text/html', body: '<!doctype html><html lang="es"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1"><script type="module" src="/assets/index-DgdNDMM1.js"></script><link rel="stylesheet" href="/assets/index-BPPI9pZI.css"></head><body><div id="root"></div><script src="/mejoras.js"></script></body></html>'});
            } else await route.fulfill({status: 404, body: ''});
        });
        await page.goto('http://127.0.0.1:47899/');
        await page.getByText('Demo.ps1', {exact: true}).waitFor();
        await page.getByRole('button', {name: 'Ejecutar Script'}).first().click();
        await page.getByText('Salida conservada de la ejecucion', {exact: true}).waitFor();
        await page.getByRole('button', {name: /Abrir carpeta/i}).click();
        await page.getByText('Tarea.ps1', {exact: true}).waitFor();
        assert.equal(await page.getByText('Salida conservada de la ejecucion', {exact: true}).count(), 1);
        assert.equal(await page.evaluate(() => window.conexionesSse), 1);
        await page.getByPlaceholder('Buscar scripts...').fill('Revisar');
        await page.getByText('Revisar.ps1', {exact: true}).waitFor();
        await page.getByText('sub/profunda', {exact: true}).waitFor();
        await page.screenshot({path: path.join(salida, 'busqueda-desktop.png'), fullPage: true});
        await page.getByPlaceholder('Buscar scripts...').fill('');
        await page.getByText('Tarea.ps1', {exact: true}).waitFor();
        desconectado = true;
        await page.evaluate(() => window.dispatchEvent(new Event('lanzador:carpeta')));
        await page.getByRole('button', {name: 'Reintentar', exact: true}).waitFor();
        assert.equal(await page.getByText('Tarea.ps1', {exact: true}).count(), 0);
        assert.equal(await page.getByText('Salida conservada de la ejecucion', {exact: true}).count(), 1);
        desconectado = false;
        await page.getByRole('button', {name: 'Reintentar', exact: true}).click();
        await page.getByText('Tarea.ps1', {exact: true}).waitFor();
        await page.setViewportSize({width: 390, height: 844});
        await page.screenshot({path: path.join(salida, 'listado-mobile.png'), fullPage: true});
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false);
        assert.deepEqual(errores, []);
        process.stdout.write('Navegacion, consola, busqueda global, reconexion y viewport: correctos.\n');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
