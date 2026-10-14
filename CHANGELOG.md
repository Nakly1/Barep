# Changelog

Todos los cambios notables de este proyecto se documentan aqui.

El formato sigue [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/) y
el versionado sigue [SemVer](https://semver.org/lang/es/).

## [Unreleased]

## [2.3.0] - 2026-10-14

### Added
- **Aplicaciones**: lista de todo lo instalado (programas y apps de la
  Microsoft Store) con su icono, fabricante, version, fecha y lo que ocupa
  entre programa, datos y cache.
- Borrado de la cache de una aplicacion concreta, con revision previa de lo que
  se va a borrar y barra de progreso.
- Antes de desinstalar por completo, ventana con **con que esta enlazada**: si
  esta abierta, servicios, arranque de Windows, tareas programadas, tipos de
  archivo que abre, programas dentro de su carpeta y datos que dejara.

### Changed
- **Procesos** muestra ahora todo lo que se ejecuta, incluido lo que trabaja sin
  ventana: agrupado por programa, con CPU y memoria en vivo, buscador, filtros
  (aplicaciones / segundo plano / Windows), orden y ficha con cada PID.
- Los procesos de Windows se identifican por su servicio (ya no son seis
  «svchost» sin nombre) y los que el sistema necesita no se pueden finalizar.
- La lectura de cada dos segundos ya no enumera todos los procesos: el resumen
  arranca y se actualiza mas rapido.
- Nombre interno del proyecto unificado a `Barep` (`Barep.csproj`).

### Removed
- Codigo sin uso: limpieza de temporales antigua, dos ayudantes de formato y una
  propiedad del arbol de la Lupa que ya no se consultaba.

## [2.2.0] - 2026-09-19

### Added
- **Mi PC**: plano 2D del equipo (portatil o sobremesa) con sus piezas reales;
  las ranuras de RAM y M.2 libres se marcan en verde.
- **Pantalla**: Hz actuales y maximos, resolucion nativa, tamano, escala, HDR,
  bits de color y fabricante del panel; cambio de Hz con vuelta atras en 15 s.
- Tema claro (blanco con morado) y tema oscuro en negro puro, con boton en la
  barra lateral.
- `dotnet run -- --probar-ajustes`: prueba de ida y vuelta de todos los ajustes
  de Optimizar, con informe en `prueba-ajustes.txt`.

### Changed
- La app pasa a llamarse **Barep** y se publica como un solo `Barep.exe`
  autocontenido: ya no hace falta instalar .NET para usarla.
- Tema oscuro en negro y grises neutros; barras de progreso rectangulares e
  iconos de la barra lateral con borde.
- Optimizar con diseno de lista agrupada (sin ovalos) y filtro segmentado.
- Los ajustes leen todos sus valores: uno aplicado a medias ya no aparece como
  optimizado. Se ocultan los que no hacen nada en tu version de Windows.

## [2.1.0] - 2026-09-19

### Added
- **Lupa**: mapa de burbujas del disco al estilo de CleanMyMac. Cada carpeta es
  una esfera cuyo tamano es proporcional a lo que ocupa; a la izquierda, la
  lista completa ordenada por peso. Se entra en cualquier carpeta pulsando su
  burbuja o su fila, con atras/adelante y migas de pan.
- Analisis de unidades completas, de una carpeta concreta o solo de las
  aplicaciones instaladas. Incluye archivos ocultos y de sistema; no cuenta dos
  veces las uniones de Windows ni los archivos de OneDrive que solo estan en la nube.
- Iconos reales de cada aplicacion y archivo, y ficha de la aplicacion al
  entrar en su carpeta (editor, version, fecha de instalacion, tamano, ubicacion).
- **Desinstalar por completo**: lanza el desinstalador oficial y despues busca
  los restos (AppData, ProgramData, carpeta de instalacion, accesos directos)
  para borrarlos tambien.
- **Limpiar caché** dentro de la Lupa (sustituye a «Limpiar temporales» de
  Herramientas): temporales, cachés de navegadores y programas, informes de
  errores, miniaturas, sombreadores y papelera, con casillas para elegir.
- Los analisis se guardan y se reabren al instante; visor de fotos y videos.
- **Optimizar**: 34 ajustes de Windows con interruptor, explicacion sencilla y aviso
  de lo que cambia; abre Configuracion si Windows bloquea el cambio y permite
  reabrir la app como administrador.
- Barra de progreso al limpiar la cache.
- **Revisar y eliminar**: seleccion de carpetas y archivos que se mueven a la
  papelera de reciclaje. Windows, las carpetas de sistema y las raices del
  usuario estan protegidas y no se pueden seleccionar.

## [2.0.0] - 2026-08-09

### Added
- **Evidencia de hardware**: actas con el numero de serie de procesador, cada
  modulo de RAM, GPUs, discos, placa base, BIOS y MAC de cada adaptador.
  Se guardan como `.smev.json` verificable y `.txt` imprimible en
  `Documentos\System Monitor\Evidencias`.
- Huella SHA-256 en cada acta: detecta si el archivo fue editado despues de
  generarse.
- Verificacion automatica contra un acta guardada, con veredicto pieza a pieza
  (intacta / cambiada / falta / nueva) y exportacion del informe.
- Detalle por modulo fisico de RAM: fabricante, numero de parte, numero de
  serie, ranura, banco, capacidad, tipo, velocidad nominal y configurada,
  formato y voltaje.
- Traduccion de codigos JEDEC de fabricante de RAM a la marca comercial.
- Inventario de unidades fisicas con serial, interfaz y firmware.
- Placa base, BIOS y UUID del equipo en el resumen.
- Navegacion lateral con seis secciones y vistas independientes.
- Grafico historico tambien para CPU.

### Changed
- Rediseno completo: paleta negra, morada y blanca; tipografia Segoe UI
  Variable Display/Text con cifras tabulares; iconografia propia; chrome de
  ventana personalizado con esquinas redondeadas de Windows 11.
- El panel de memoria pasa de una tarjeta de tres datos a una seccion propia
  con uso en vivo e inventario de modulos.
- Un unico `MonitorService` muestrea el sistema y reparte la lectura por
  evento, en lugar de un temporizador acoplado a la ventana.
- La lectura de hardware se reparte en `HardwareModels`, `HardwareService`,
  `JedecVendors`, `EvidenceService` y `SystemReport`.
- Manifiesto con reconocimiento de PPP por monitor (PerMonitorV2).

### Added (infraestructura)
- Estructura inicial de archivos de la comunidad: `LICENSE`, `CONTRIBUTING.md`,
  `CHANGELOG.md`, plantillas de issues y PR.

## [1.0.0] - 2026-04-18

### Added
- Primera version publica.
- Monitor en tiempo real de RAM, CPU, GPU, red, bateria, discos y procesos.
- Mini grafico historico de RAM (60 s).
- Top 10 procesos por uso de memoria con boton para finalizarlos.
- Acciones: limpiar archivos temporales, forzar Garbage Collection y exportar
  informe a `.txt`.
- Lectura de hardware via WMI, registro de Windows y P/Invoke.
- Build self-contained-false para .NET 8 Desktop Runtime.

[Unreleased]: https://github.com/Nakly1/SystemMonitorDesktop/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/Nakly1/SystemMonitorDesktop/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/Nakly1/SystemMonitorDesktop/releases/tag/v1.0.0
