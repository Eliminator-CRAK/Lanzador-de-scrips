// (Autor: Alex Roman)
// Descripcion: Adapta el componente de aplicacion del bundle heredado sin modificar las librerias React.

using System.IO;

namespace LanzadorScripts.Servicios;

internal static class AdaptadorNavegacionCliente
{
    private const string Ancla = "const T=b<768,U=i.filter";
    private const string Efecto = """
        G.useEffect(()=>{
            let controlador=null,temporizador=null,vigente=true;
            const refrescar=()=>{
                controlador?.abort();
                const peticion=new AbortController();controlador=peticion;
                l([]);
                at('/api/scripts?buscar='+encodeURIComponent(o),{signal:peticion.signal})
                    .then(async respuesta=>{
                        const datos=await respuesta.json();
                        if(!respuesta.ok||!Array.isArray(datos))throw new Error(datos.error||'Servidor no disponible');
                        if(!vigente||peticion.signal.aborted)return;
                        l(datos);O(false);
                        window.dispatchEvent(new CustomEvent('lanzador:listado-estado',{detail:{mensaje:''}}));
                    }).catch(error=>{
                        if(!vigente||peticion.signal.aborted)return;
                        l([]);O(true);
                        window.dispatchEvent(new CustomEvent('lanzador:listado-estado',{detail:{mensaje:error.message}}));
                    });
            };
            const navegar=()=>{clearTimeout(temporizador);refrescar();};
            temporizador=setTimeout(refrescar,o?200:0);
            window.addEventListener('lanzador:carpeta',navegar);
            return()=>{vigente=false;clearTimeout(temporizador);controlador?.abort();window.removeEventListener('lanzador:carpeta',navegar);};
        },[o]);
        """;

    public static string Aplicar(string bundle)
    {
        // Las anclas exactas hacen fallar la compilacion de pruebas si cambia el bundle de origen.
        bundle = SustituirUnaVez(bundle, "Tt(!0),O(!0)}),mt()", "Tt(false),O(true)})");
        bundle = SustituirUnaVez(bundle, "children:i.tipo})]})]})",
            "children:i.tipo}),i.carpeta&&!i.esCarpeta&&z.jsx('p',{className:'text-[10px] text-gray-400 break-all',title:i.id,children:i.carpeta})]})]})");
        bundle = SustituirUnaVez(bundle,
            "const pt=await(await at(\"/api/configuracion-app\",{method:\"POST\",headers:{\"Content-Type\":\"application/json\"},body:JSON.stringify(K)})).json();if(pt.error){F(pt.error);return}", "");
        bundle = SustituirUnaVez(bundle, ":window.location.reload()}catch{F(\"Error al guardar.\")}",
            ":(v('scripts'),window.dispatchEvent(new Event('lanzador:carpeta')))}catch{F(\"Error al guardar.\")}");
        const string cabecera = "z.jsxs(\"section\",{children:[z.jsx(\"h3\",{className:\"text-sm font-medium text-gray-200 uppercase tracking-wider mb-4\",children:";
        var inicio = bundle.IndexOf(cabecera + "\"Rutas de Configuración\"", StringComparison.Ordinal);
        var fin = bundle.IndexOf(cabecera + "\"Permisos y Usuarios\"", StringComparison.Ordinal);
        if (inicio < 0 || fin <= inicio) throw new InvalidDataException("No se encontro el formulario heredado de rutas.");
        bundle = bundle.Remove(inicio, fin - inicio);
        return SustituirUnaVez(bundle, Ancla, Efecto + Ancla);
    }

    private static string SustituirUnaVez(string texto, string anterior, string nuevo)
    {
        var indice = texto.IndexOf(anterior, StringComparison.Ordinal);
        if (indice < 0 || texto.IndexOf(anterior, indice + anterior.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidDataException("El bundle cliente no coincide con el adaptador de navegacion.");
        return texto[..indice] + nuevo + texto[(indice + anterior.Length)..];
    }
}
