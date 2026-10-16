<div align="center">

<img src="Assets/logo.png" width="112" alt="Barep">

# Barep

Monitor, limpieza y optimización para **Windows 10 / 11**. Un solo `.exe`, sin instalar nada.

Mira qué ocupa tu disco en un mapa de burbujas, revisa todo lo que tienes instalado, desinstala
apps sin dejar restos, limpia la caché, mira qué se está ejecutando de verdad, vigila quién se
conecta a tu Wi-Fi, mide tu internet, optimiza Windows con un interruptor por ajuste, ve dónde
puedes ampliar RAM o SSD y comprueba a cuántos Hz va tu pantalla.

**v2.4**

[![Descargar](https://img.shields.io/badge/⬇%20Descargar-Barep.exe-7C3AED?style=for-the-badge)](https://github.com/Nakly1/SystemMonitorDesktop/releases/latest/download/Barep.exe)
[![Windows](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?style=for-the-badge&logo=windows)](#)
[![License](https://img.shields.io/badge/License-MIT-blue?style=for-the-badge)](#licencia)
[![Build](https://img.shields.io/github/actions/workflow/status/Nakly1/SystemMonitorDesktop/build.yml?branch=main&style=for-the-badge&logo=github)](https://github.com/Nakly1/SystemMonitorDesktop/actions/workflows/build.yml)

</div>

---

## Descargar y usar

1. Descarga **[Barep.exe](https://github.com/Nakly1/SystemMonitorDesktop/releases/latest/download/Barep.exe)**
   (o entra en [Releases](https://github.com/Nakly1/SystemMonitorDesktop/releases)).
2. Doble clic. Ya está.

No hay instalador y **no necesitas instalar .NET**: todo va dentro del ejecutable.
La primera vez Windows puede mostrar «Windows protegió tu PC» porque el `.exe` no está firmado:
pulsa **Más información → Ejecutar de todas formas**.

---

## Qué hace

### Resumen
Memoria y procesador en vivo con gráfico de los últimos 2 minutos, gráficas, red, batería y la
ficha del equipo (fabricante, modelo, placa base, BIOS).

### Memoria
Cada módulo de RAM por separado: fabricante, número de parte y de serie, ranura, tipo
(DDR4, DDR5, LPDDR5…), velocidad nominal y real, y ranuras libres para ampliar.
- **Liberar memoria**: hace que los programas suelten la RAM que tienen apartada sin usar y, como
  administrador, vacía la caché de archivos de Windows. Te dice cuánto se liberó.

### Almacenamiento
Volúmenes con su espacio y las unidades físicas con número de serie, interfaz y firmware.

### Lupa
El disco dibujado como **burbujas proporcionales a lo que ocupa cada carpeta**, con la lista
ordenada por tamaño al lado.
- Analiza una unidad, una carpeta o **solo las aplicaciones instaladas**, con su icono real.
- Entra en una app para ver su ficha y **desinstalarla por completo**: abre el desinstalador
  oficial y después busca los restos (AppData, ProgramData, accesos directos) para borrarlos.
- **Visor de fotos y vídeos**, con abrir, «abrir con…» y mostrar en el Explorador.
- **Limpiar caché**: temporales, caché de navegadores, Discord, Spotify, informes de errores,
  miniaturas, sombreadores y papelera. Te enseña qué va a borrar, marcas lo que quieras y ves
  una barra de progreso.
- **Revisar y eliminar** manda lo seleccionado a la papelera. Windows y las carpetas del sistema
  están protegidas.
- El análisis se guarda: al volver se abre al instante.

### Aplicaciones
Todo lo que tienes instalado, programas y apps de la Microsoft Store, con su icono real,
fabricante, versión, fecha y **lo que ocupa de verdad**: el programa, tus datos y la caché por
separado.
- **Borrar caché**: sólo las carpetas que son caché, registros o temporales. Te enseña qué va a
  borrar, desmarcas lo que quieras y ves la barra de progreso. Tus sesiones, contraseñas y
  archivos no se tocan.
- **Desinstalar por completo**: antes de empezar te dice **con qué está enlazada** — si está
  abierta ahora, si instaló un servicio, si arranca con Windows, si tiene tareas programadas, qué
  tipos de archivo abre, si hay otros programas dentro de su carpeta y qué datos tuyos dejará.
  Después abre el desinstalador oficial y busca los restos para mandarlos a la papelera.

### Procesos
Todo lo que se está ejecutando, **también lo que no se ve**: agrupado por programa, con memoria y
procesador en vivo.
- Filtros por aplicaciones, cosas en segundo plano y procesos de Windows, buscador y orden por
  memoria, CPU o nombre.
- Los «svchost» salen con el nombre del servicio que llevan dentro.
- Al elegir un programa ves cada uno de sus procesos con su PID, desde cuándo está abierto y de
  dónde sale; puedes finalizarlo entero. Lo que Windows necesita para funcionar no se deja cerrar.

### Red
Todo lo que pasa en tu red, en pestañas:
- **Aparatos**: cada móvil, PC, tele, consola, impresora o bombilla conectada, con su marca y,
  cuando el aparato lo anuncia, **su modelo exacto** (iPhone 15 Pro, Galaxy S23, Chromecast,
  el modelo del router…). Dice de dónde sale cada dato para no confundir una suposición con un
  hecho, y detecta las **direcciones privadas** que usan los móviles modernos. Le puedes poner
  nombre a cada uno («Móvil de mamá»), marcarlo como tuyo, ver sus puertos abiertos con lo que
  significa cada uno y buscar fotos del modelo.
- **Actividad**: vigila la red mientras Barep está abierto. Apunta quién entra y quién sale, cuánto
  tiempo lleva conectado cada aparato hoy y **avisa en la barra de arriba cuando aparece uno que
  nunca se había conectado**.
- **Velocidad**: test de bajada, subida, ping y variación con historial, explicado en cristiano
  («da para 4K en una pantalla…»).
- **Wi-Fi**: señal, banda, canal, estándar (Wi-Fi 5/6/7) y cifrado de tu red, las redes de
  alrededor con su seguridad y **el canal más libre** para tu router. Para que vaya más rápido:
  quita el ahorro de energía del adaptador, mide tu DNS contra Cloudflare, Google y Quad9 y
  cámbialo con un clic, y te dice qué tocar en el router.
- **Seguridad**: puntuación de 0 a 100 revisando el cifrado del Wi-Fi, el cortafuegos, los
  puertos abiertos del PC y del router (Telnet, UPnP, panel sin cifrar), Escritorio remoto, SMB1,
  proxy, archivo hosts, DNS, aparatos sin reconocer, depuración Android expuesta y **si alguien
  está suplantando al router** (ARP spoofing).
- **Conexiones**: con qué servidores habla cada programa del PC ahora mismo y qué puertos tiene
  abiertos a la red.

### Optimizar
Ajustes de Windows con su interruptor, en qué consiste cada uno en palabras sencillas y
**qué conviene saber antes** (qué cambia, cómo se verá, qué deja de funcionar).
- **Privacidad**: datos de diagnóstico, ID de publicidad, Bing en Inicio, historial de actividad…
- **Anuncios y sugerencias**: pantalla de bloqueo, apps que se instalan solas, consejos.
- **Barra de tareas**: widgets, «Finalizar tarea» con clic derecho.
- **Juegos**: Xbox Game Bar, modo de juego, aceleración del ratón, programación de GPU.
- **Rendimiento**: plan de energía, transparencias, animaciones, sensor de almacenamiento.
- **Explorador**: extensiones de archivo, menú de clic derecho clásico.

Si Windows no deja cambiar algo desde otra app, Barep abre directamente su página de
Configuración. Los que necesitan permisos piden abrir la app como administrador. Todo se
deshace con el mismo interruptor. No toca Defender, Windows Update ni servicios del sistema.

### Mi PC
Un **plano 2D de tu portátil o sobremesa** con el procesador, la gráfica, cada módulo de RAM,
los discos y la batería. Las **ranuras libres** (RAM o M.2) salen en verde para que sepas dónde
ampliar. Al pasar el ratón por cada pieza ves sus datos.

### Pantalla
Hz actuales y máximos, resolución nativa, pulgadas, escala, HDR, bits de color y fabricante del
panel. Si tu pantalla admite más Hz de los que usa, te lo dice y los sube con un clic; si la
imagen falla, vuelve sola a los anteriores en 15 segundos.

### Evidencia
Para cuando dejas el equipo en un servicio técnico, en dos pasos:
1. **Antes de dejarlo**: «Crear acta» guarda el número de serie de cada pieza (procesador, RAM,
   discos, placa, tarjeta de red) con una huella SHA-256 y un código corto para fotografiar.
2. **Al recogerlo**: eliges el acta y pulsas «Comprobar». El acta se ve en la app como un
   documento y cada pieza se marca como **igual**, **cambió**, **falta** o **nueva**.

Si alguien edita el archivo del acta, Barep lo detecta. Se puede guardar en texto para imprimirla
y firmarla, y también una ficha completa del equipo para enviársela a quien lo repare.

### Temas
Oscuro (negro) o claro (blanco), con acento morado. Se cambia desde la barra lateral.

---

## Compilar desde el código

Necesitas el [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```bash
git clone https://github.com/Nakly1/SystemMonitorDesktop.git
cd SystemMonitorDesktop
dotnet run                                        # abrir en modo desarrollo
dotnet publish -c Release -r win-x64 -o publish   # genera publish/Barep.exe (un solo archivo)
```

Cada etiqueta `vX.Y.Z` que se sube a GitHub publica automáticamente `Barep.exe` en Releases.

---

## Cómo funciona por dentro

- **WPF** sobre .NET 8, publicado como ejecutable único autocontenido
- **WMI** (`System.Management`) para CPU, RAM, GPU, discos, ranuras, placa y monitores (EDID)
- **API de configuración de pantalla** de Windows para Hz exactos, HDR y tipo de conexión
- **Registro de Windows** para los ajustes de Optimizar y el inventario de aplicaciones
- **API de procesos de Windows** para leer la ruta y el consumo de todo lo que se ejecuta
- **ARP, mDNS/Bonjour, UPnP/SSDP y NetBIOS** para descubrir y reconocer los aparatos de la red,
  con el registro de fabricantes del IEEE incluido en el ejecutable
- **Enumeración nativa de archivos** en paralelo para la Lupa, con caché comprimida en disco
- **Shell de Windows** para iconos reales, papelera de reciclaje y «abrir con»

---

## Preguntas frecuentes

**¿Necesita permisos de administrador?**
No para el uso normal. Algunos ajustes de Optimizar sí (la app lo indica y ofrece reabrirse como
administrador), y desinstalar programas puede pedir el permiso de Windows.

**¿Envía datos a internet?**
No. Todo lo que analiza se queda en tu equipo. Sólo se conecta a internet para el test de
velocidad y para ver tu IP pública (ambos contra los servidores de Cloudflare, sin mandar nada
tuyo), y abre el navegador si pulsas un botón de «Buscar» o «Ver fotos».

**¿Puedo ver lo que navegan los demás en mi Wi-Fi?**
No, y Barep no lo intenta: eso exigiría interceptar el tráfico de otras personas. Lo que sí ves
es quién está conectado, desde cuándo, qué es cada aparato y qué tiene abierto.

**¿Por qué algunos móviles salen sin modelo?**
Android e iPhone usan una MAC aleatoria y muchos no anuncian su modelo en la red. Barep enseña lo
que el aparato dice de sí mismo (nombre, servicios, ficha UPnP) y lo marca como «probable» cuando
lo deduce. Ponle un nombre y así lo reconocerás siempre.

**¿Borra algo sin preguntar?**
No. Todo lo que se borra se enseña antes en una lista para marcar o desmarcar. Lo de la Lupa y los
restos de una app desinstalada van a la papelera de reciclaje; la caché se borra del todo porque
los programas la regeneran.

**¿Puedo cargarme el equipo finalizando un proceso?**
Los procesos que Windows necesita están bloqueados, y de los demás la app te avisa de qué puede
dejar de funcionar. Nada de lo que se cierra ahí se desinstala: vuelve al reiniciar.

**¿El plano de Mi PC es exacto?**
Las piezas y las ranuras libres son las reales de tu equipo; su posición en el dibujo es la típica
de un portátil o una placa de sobremesa, porque Windows no informa de dónde está cada una.

**¿Funciona en Linux / macOS?**
No. Usa WMI y el registro de Windows, así que es solo para Windows 10 / 11.

---

## Licencia

**MIT** — haz con este código lo que quieras. Los avisos de terceros están en
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

<div align="center">

Si te es útil, deja una ⭐ en el repo. Reportes de bugs o sugerencias en **Issues**.

</div>
