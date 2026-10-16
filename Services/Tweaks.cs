using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Barep.Services;

public enum TweakRestart { None, Explorer, SignOut, Reboot }

public enum TweakOutcome { Done, NeedsAdmin, Blocked, Failed }

/// <summary>
/// Un ajuste de Windows que la sección Optimizar puede activar o desactivar.
/// El interruptor siempre refleja si la función de Windows está encendida;
/// <see cref="Recommended"/> dice cómo conviene dejarla (null = a tu gusto).
/// </summary>
public sealed class Tweak
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Title { get; init; }

    /// <summary>Qué es, en palabras llanas.</summary>
    public required string What { get; init; }

    /// <summary>Lo que conviene saber antes: cómo se verá después, qué deja de funcionar.</summary>
    public required string Note { get; init; }

    public bool? Recommended { get; init; }
    public bool NeedsAdmin { get; init; }
    public TweakRestart Restart { get; init; }

    /// <summary>Página de Configuración de Windows a la que ir si no se puede cambiar desde aquí.</summary>
    public string? SettingsUri { get; init; }

    /// <summary>Si es null, el ajuste no tiene interruptor: sólo abre la Configuración.</summary>
    public Func<bool?>? Read { get; init; }
    public Action<bool>? Write { get; init; }
    public Func<bool> Applies { get; init; } = () => true;

    public bool IsLinkOnly => Read is null;

    /// <summary>Valores de registro que toca (para la prueba de ida y vuelta).</summary>
    public IReadOnlyList<TweakCatalog.RegSpec> Specs { get; init; } = Array.Empty<TweakCatalog.RegSpec>();
}

/// <summary>
/// El catálogo de ajustes, sacado de guías y foros de optimización de Windows
/// 10/11 (TweakTown, ElevenForum, WinAero, WOSHub…). Sólo cosas reversibles y
/// que no rompen el sistema: nada de desactivar Defender, Windows Update ni
/// servicios a ciegas.
/// </summary>
public static class TweakCatalog
{
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    public static bool IsAdmin
    {
        get
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }
    }

    public static readonly string[] Categories =
    {
        "Privacidad", "Anuncios y sugerencias", "Barra de tareas", "Juegos", "Rendimiento", "Explorador de archivos"
    };

    public static IReadOnlyList<Tweak> All { get; } = Build().ToList();

    private static IEnumerable<Tweak> Build()
    {
        // ─────────────── Privacidad ───────────────
        yield return Reg("telemetry", "Privacidad", "Datos de diagnóstico opcionales",
            "Windows envía a Microsoft información sobre cómo usas el equipo: qué apps abres, errores y uso del hardware.",
            "Se sigue enviando sólo lo mínimo obligatorio para que las actualizaciones funcionen. No notarás ningún cambio al usar el PC.",
            false, "ms-settings:privacy-feedback", TweakRestart.None,
            new RegSpec(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 3, 1,
                MissingMeansOn: true, DeleteForOn: true),
            new RegSpec(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection",
                "AllowTelemetry", 3, 1, MissingMeansOn: true, DeleteForOn: true));

        yield return Reg("adid", "Privacidad", "ID de publicidad",
            "Un número que identifica tu equipo para que las apps te muestren anuncios según lo que haces.",
            "Seguirás viendo anuncios, pero ya no personalizados. Nada más cambia.",
            false, "ms-settings:privacy-general", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 1, 0, true));

        yield return Reg("tailored", "Privacidad", "Experiencias personalizadas",
            "Microsoft usa tus datos de diagnóstico para mostrarte consejos, anuncios y recomendaciones a tu medida.",
            "Los consejos que veas serán genéricos. No afecta a ninguna función.",
            false, "ms-settings:privacy-feedback", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Privacy",
                "TailoredExperiencesWithDiagnosticDataEnabled", 1, 0, true));

        yield return Reg("feedback", "Privacidad", "Preguntas de opinión de Microsoft",
            "Ventanas que aparecen de vez en cuando preguntando qué te parece Windows.",
            "Dejarán de salir esas encuestas. Puedes seguir opinando desde la app «Centro de opiniones» si quieres.",
            false, "ms-settings:privacy-feedback", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 1, 0, true,
                DeleteForOn: true));

        yield return Reg("activity", "Privacidad", "Historial de actividad",
            "Windows guarda una lista de los archivos, páginas y apps que abres para «continuar donde lo dejaste».",
            "Se pierde la función de retomar tareas en otro equipo con tu misma cuenta. El resto funciona igual.",
            false, "ms-settings:privacy-activityhistory", TweakRestart.None,
            new RegSpec(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 1, 0,
                true, DeleteForOn: true),
            new RegSpec(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 1, 0,
                true, DeleteForOn: true));

        yield return Reg("trackprogs", "Privacidad", "Recordar las apps que más usas",
            "El menú Inicio y la búsqueda anotan qué programas abres para sugerírtelos primero.",
            "Inicio dejará de mostrar la lista de «más usadas». Tus apps ancladas no cambian.",
            false, "ms-settings:start", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Adv, "Start_TrackProgs", 1, 0, true));

        yield return Reg("bing", "Privacidad", "Resultados de internet (Bing) al buscar en Inicio",
            "Cuando buscas algo en el menú Inicio, Windows también lo busca en Bing y te muestra resultados web y anuncios.",
            "La búsqueda de Inicio sólo mostrará cosas de tu PC y será más rápida. Para buscar en internet usa el navegador. Se ve tras reiniciar el Explorador.",
            false, null, TweakRestart.Explorer,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 0, 1,
                true, DeleteForOn: true),
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 1, 0, true));

        yield return Reg("copilot", "Privacidad", "Copilot de Windows",
            "El asistente con inteligencia artificial de Microsoft integrado en Windows.",
            "Si lo usas, déjalo activado. Al desactivarlo desaparece su botón; en versiones nuevas Copilot es una app y también puedes desinstalarla desde la Lupa.",
            null, null, TweakRestart.Explorer,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsCopilot", "TurnOffWindowsCopilot", 0, 1,
                true, DeleteForOn: true),
            new RegSpec(RegistryHive.CurrentUser, Adv, "ShowCopilotButton", 1, 0, true));

        yield return Reg("recall", "Privacidad", "Recall (capturas de pantalla con IA)",
            "En los PC «Copilot+», Windows puede hacer capturas de lo que ves para que luego lo busques con IA.",
            "Sólo existe en equipos Copilot+; en los demás este ajuste no cambia nada. Desactivado, no se guardan capturas.",
            false, "ms-settings:privacy-recall", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 0, 1,
                true, DeleteForOn: true)).OnlyIf(HasRecall);

        // ─────────────── Anuncios y sugerencias ───────────────
        yield return Reg("lockscreen-tips", "Anuncios y sugerencias", "Datos curiosos y consejos en la pantalla de bloqueo",
            "Los textos, preguntas y anuncios que aparecen sobre la foto de la pantalla de bloqueo.",
            "Tu fondo de pantalla de bloqueo se queda igual; sólo desaparecen los textos. Si usas «Contenido destacado de Windows» como fondo, puede que tengas que cambiarlo a «Imagen».",
            false, "ms-settings:lockscreen", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Cdm, "RotatingLockScreenOverlayEnabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SubscribedContent-338387Enabled", 1, 0, true));

        yield return Reg("lockscreen-widgets", "Anuncios y sugerencias", "Widgets en la pantalla de bloqueo",
            "El tiempo, la bolsa, el tráfico y noticias que se ven en la pantalla de bloqueo.",
            "La pantalla de bloqueo quedará más limpia, sólo con la hora y tu fondo. Si Windows no deja cambiarlo desde aquí, en Configuración pon «Estado de la pantalla de bloqueo» en «Ninguno».",
            false, "ms-settings:lockscreen", TweakRestart.None,
            new RegSpec(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\PersonalizationCSP",
                "LockScreenWidgetsEnabled", 1, 0, true, DeleteForOn: true),
            new RegSpec(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "DisableWidgetsOnLockScreen", 0, 1,
                true, DeleteForOn: true)).With11Only();

        yield return Reg("start-suggestions", "Anuncios y sugerencias", "Apps recomendadas en el menú Inicio",
            "Sugerencias de apps de la Tienda y «recomendaciones» que Windows mete en el menú Inicio.",
            "Inicio sólo mostrará tus apps y archivos. No desinstala nada.",
            false, "ms-settings:start", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SubscribedContent-338388Enabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SystemPaneSuggestionsEnabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Adv, "Start_IrisRecommendations", 1, 0, true));

        yield return Reg("silent-apps", "Anuncios y sugerencias", "Instalar apps promocionadas sin preguntar",
            "Windows descarga por su cuenta juegos y apps patrocinadas (como Candy Crush o TikTok) en el menú Inicio.",
            "Evita que aparezcan apps nuevas que no pediste. Las que ya estén instaladas no se borran: quítalas desde la Lupa.",
            false, null, TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SilentInstalledAppsEnabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "PreInstalledAppsEnabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "OemPreInstalledAppsEnabled", 1, 0, true));

        yield return Reg("tips", "Anuncios y sugerencias", "Consejos y trucos de Windows",
            "Notificaciones con consejos, la pantalla de «Termina de configurar tu dispositivo» y ofertas de Microsoft 365 u OneDrive.",
            "Dejarán de salir esos avisos después de las actualizaciones. Las notificaciones de tus apps no se tocan.",
            false, "ms-settings:notifications", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SubscribedContent-338389Enabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SoftLandingEnabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SubscribedContent-310093Enabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement",
                "ScoobeSystemSettingEnabled", 1, 0, true));

        yield return Reg("settings-suggestions", "Anuncios y sugerencias", "Sugerencias dentro de Configuración",
            "Recomendaciones y promociones que aparecen en la app de Configuración.",
            "La Configuración se verá más limpia. Ninguna opción desaparece.",
            false, null, TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SubscribedContent-338393Enabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SubscribedContent-353694Enabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, Cdm, "SubscribedContent-353696Enabled", 1, 0, true));

        // ─────────────── Barra de tareas ───────────────
        yield return Reg("widgets", "Barra de tareas", "Widgets (tiempo y noticias)",
            "El panel de noticias y el tiempo que se abre desde la esquina de la barra de tareas.",
            "Desaparece el botón del tiempo y el panel deja de cargarse en segundo plano, lo que ahorra algo de memoria. Windows a veces bloquea este cambio: si pasa, te abrimos la Configuración para quitarlo con un clic.",
            false, "ms-settings:taskbar", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Adv, "TaskbarDa", 1, 0, true)).With11Only();

        yield return Reg("news", "Barra de tareas", "Noticias e intereses",
            "El tiempo y las noticias que aparecen en la barra de tareas de Windows 10.",
            "Desaparece de la barra y deja de cargarse en segundo plano.",
            false, null, TweakRestart.Explorer,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Feeds", "ShellFeedsTaskbarViewMode", 0, 2,
                true)).With10Only();

        yield return Reg("chat", "Barra de tareas", "Botón de Chat de Teams",
            "El icono de Chat (Microsoft Teams personal) en la barra de tareas.",
            "Sólo quita el botón. Si usas Teams, sigue funcionando desde su propia app.",
            false, "ms-settings:taskbar", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Adv, "TaskbarMn", 1, 0, true)).OnlyIf(() => OsBuild is >= 22000 and < 26100);

        yield return Reg("endtask", "Barra de tareas", "«Finalizar tarea» al hacer clic derecho",
            "Añade la opción de cerrar a la fuerza un programa colgado haciendo clic derecho en su icono de la barra de tareas.",
            "Muy útil cuando algo se queda congelado: no hace falta abrir el Administrador de tareas.",
            true, null, TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Adv + @"\TaskbarDeveloperSettings", "TaskbarEndTask", 1, 0, false)).OnlyIf(() => OsBuild >= 22631);

        // ─────────────── Juegos ───────────────
        yield return Reg("gamebar", "Juegos", "Xbox Game Bar y grabación en segundo plano",
            "La barra que sale con Win + G para grabar partidas, ver FPS o chatear, y que graba en segundo plano.",
            "Ganarás algo de rendimiento en juegos. Ya no podrás grabar ni hacer capturas con Win + G. Los mandos de Xbox siguen funcionando.",
            false, "ms-settings:gaming-gamebar", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", 1, 0, true),
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 1, 0, true));

        yield return Reg("gamemode", "Juegos", "Modo de juego",
            "Cuando juegas, Windows da prioridad al juego y evita instalar actualizaciones o mostrar avisos.",
            "Conviene dejarlo activado. No afecta a nada cuando no estás jugando.",
            true, "ms-settings:gaming-gamemode", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1, 0, true));

        yield return new Tweak
        {
            Id = "mouseaccel", Category = "Juegos", Title = "Aceleración del ratón («Mejorar precisión del puntero»)",
            What = "Windows mueve el puntero más o menos lejos según lo rápido que muevas el ratón.",
            Note = "Desactivada, el puntero se mueve siempre la misma distancia: apuntar en juegos se vuelve más preciso. Al principio el ratón puede sentirse distinto; ajusta la velocidad si hace falta.",
            Recommended = false, SettingsUri = "ms-settings:mousetouchpad",
            Read = MouseAcceleration, Write = SetMouseAcceleration
        };

        yield return Reg("hags", "Juegos", "Programación de GPU acelerada por hardware",
            "La tarjeta gráfica organiza su propio trabajo en vez de hacerlo el procesador.",
            "Suele dar un poco más de rendimiento y menos retraso en juegos con tarjetas modernas. Se aplica al reiniciar el equipo. Si notas fallos gráficos, vuelve a desactivarla. Si en Configuración › Pantalla › Gráficos no aparece esta opción, tu tarjeta no la admite.",
            null, "ms-settings:display-advancedgraphics", TweakRestart.Reboot,
            new RegSpec(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", 2, 1, false))
            .OnlyIf(GpuSchedulingSupported);

        // ─────────────── Rendimiento ───────────────
        yield return new Tweak
        {
            Id = "power", Category = "Rendimiento", Title = "Plan de energía de alto rendimiento",
            What = "El procesador trabaja siempre a buena velocidad en vez de ahorrar energía.",
            Note = "Más fluidez en equipos de escritorio. En portátiles gasta más batería y calienta más: mejor déjalo desactivado si usas batería.",
            Recommended = null, SettingsUri = "ms-settings:powersleep",
            Read = HighPerformancePlan, Write = SetHighPerformancePlan
        };

        yield return Reg("transparency", "Rendimiento", "Efectos de transparencia",
            "El efecto de cristal translúcido de la barra de tareas, Inicio y ventanas.",
            "Desactivado, Windows se ve con colores sólidos y va un poco más ligero en equipos modestos. Es cuestión de gusto.",
            null, "ms-settings:colors", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency", 1, 0, true));

        yield return new Tweak
        {
            Id = "animations", Category = "Rendimiento", Title = "Animaciones de Windows",
            What = "Las transiciones al abrir, cerrar y minimizar ventanas y menús.",
            Note = "Desactivadas, todo aparece al instante y el equipo se siente más rápido, aunque menos vistoso.",
            Recommended = null, SettingsUri = "ms-settings:easeofaccess-visualeffects",
            Read = ClientAnimations, Write = SetClientAnimations
        };

        yield return Reg("menudelay", "Rendimiento", "Espera antes de abrir submenús",
            "Windows espera casi medio segundo antes de desplegar un submenú al pasar el ratón.",
            "Desactivada, los menús se abren al momento. Se nota al cerrar sesión o reiniciar.",
            false, null, TweakRestart.SignOut,
            new RegSpec(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", "400", "50", true,
                Kind: RegistryValueKind.String));

        yield return Reg("backgroundapps", "Rendimiento", "Apps de la Tienda en segundo plano",
            "Las apps de la Microsoft Store pueden seguir funcionando aunque no las tengas abiertas.",
            "Ahorra memoria y batería. Algunas apps (Correo, Teams, Fotos) pueden dejar de avisarte hasta que las abras. En Windows 11 reciente se ajusta app por app en Configuración.",
            null, "ms-settings:appsfeatures", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
                "GlobalUserDisabled", 0, 1, true)).With10Only();

        yield return new Tweak
        {
            Id = "backgroundapps11", Category = "Rendimiento", Title = "Apps en segundo plano",
            What = "En Windows 11 cada app de la Tienda decide si sigue funcionando cerrada; ya no hay un interruptor general.",
            Note = "En Configuración › Aplicaciones, abre una app, «Opciones avanzadas» y pon «Permisos de aplicaciones en segundo plano» en «Nunca» para las que no necesiten avisarte.",
            SettingsUri = "ms-settings:appsfeatures", Applies = () => IsWindows11
        };

        yield return Reg("storagesense", "Rendimiento", "Sensor de almacenamiento",
            "Windows vacía solo los temporales y la papelera antigua cuando el disco se va llenando.",
            "Recomendado: mantiene el disco limpio sin que hagas nada. No borra tus documentos.",
            true, "ms-settings:storagesense", TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy",
                "01", 1, 0, false));

        yield return Reg("delivery", "Rendimiento", "Compartir actualizaciones con otros PC",
            "Tu equipo usa tu internet para enviar trozos de actualizaciones de Windows a otros equipos.",
            "Desactivado, no gastas datos ni velocidad ayudando a otros. Tus actualizaciones siguen llegando normal.",
            false, "ms-settings:delivery-optimization", TweakRestart.None,
            new RegSpec(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 1, 0,
                true, DeleteForOn: true));

        yield return new Tweak
        {
            Id = "startup", Category = "Rendimiento", Title = "Programas que arrancan con Windows",
            What = "Apps que se abren solas al encender el equipo y hacen que tarde más en estar listo.",
            Note = "Desactiva las que no necesites nada más encender (Spotify, Discord, lanzadores de juegos…). Siguen funcionando si las abres tú.",
            SettingsUri = "ms-settings:startupapps"
        };

        // ─────────────── Explorador de archivos ───────────────
        yield return Reg("extensions", "Explorador de archivos", "Mostrar extensiones de archivo",
            "Ver el tipo real de cada archivo al final del nombre (.jpg, .pdf, .exe…).",
            "Recomendado por seguridad: así no te engañan con un «foto.jpg» que en realidad es «foto.jpg.exe». Los nombres se verán un poco más largos.",
            true, null, TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Adv, "HideFileExt", 0, 1, false));

        yield return Reg("thispc", "Explorador de archivos", "Abrir el Explorador en «Este equipo»",
            "Al abrir el Explorador, empezar en tus discos en lugar de en «Inicio» con archivos recientes.",
            "Es cuestión de gusto: verás tus unidades directamente y no la lista de archivos recientes.",
            null, null, TweakRestart.None,
            new RegSpec(RegistryHive.CurrentUser, Adv, "LaunchTo", 1, 2, false));

        yield return new Tweak
        {
            Id = "classicmenu", Category = "Explorador de archivos", Title = "Menú de clic derecho clásico",
            What = "Windows 11 esconde muchas opciones del clic derecho detrás de «Mostrar más opciones».",
            Note = "Activado, vuelve el menú completo de siempre, sin el paso extra. Se ve después de reiniciar el Explorador.",
            Recommended = null, Restart = TweakRestart.Explorer,
            Read = ClassicContextMenu, Write = SetClassicContextMenu, Applies = () => IsWindows11
        };
    }

    // ────────────────────────── Registro ──────────────────────────

    public sealed record RegSpec(RegistryHive Hive, string Path, string Name, object On, object Off, bool MissingMeansOn,
        bool DeleteForOn = false, RegistryValueKind Kind = RegistryValueKind.DWord);

    private static Tweak Reg(string id, string category, string title, string what, string note, bool? recommended,
        string? settingsUri, TweakRestart restart, params RegSpec[] specs) => new()
    {
        Id = id, Category = category, Title = title, What = what, Note = note, Recommended = recommended,
        SettingsUri = settingsUri, Restart = restart,
        NeedsAdmin = specs.Any(s => s.Hive == RegistryHive.LocalMachine),
        Specs = specs,
        Read = () => ReadAll(specs),
        Write = on =>
        {
            foreach (var spec in specs) WriteSpec(spec, on);
        }
    };

    private static int OsBuild => Environment.OSVersion.Version.Build;

    private static Tweak OnlyIf(this Tweak t, Func<bool> applies) => Clone(t, applies);

    /// <summary>Recall sólo existe en PC Copilot+: se busca su paquete del sistema.</summary>
    private static bool HasRecall()
    {
        try
        {
            var systemApps = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemApps");
            return System.IO.Directory.Exists(systemApps) &&
                   System.IO.Directory.EnumerateDirectories(systemApps, "MicrosoftWindows.Client.AIX*").Any();
        }
        catch { return false; }
    }

    /// <summary>Windows sólo ofrece la programación por hardware si el controlador gráfico la admite.</summary>
    private static bool GpuSchedulingSupported()
    {
        try
        {
            using var key = Root(RegistryHive.LocalMachine).OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            if (key?.GetValue("HwSchMode") is not null) return true;
            // Con controladores WDDM 2.7 o superiores aparece la opción en Configuración.
            using var scheduler = Root(RegistryHive.LocalMachine)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers\Scheduler");
            return scheduler is not null;
        }
        catch { return false; }
    }

    private static Tweak With11Only(this Tweak t) => Clone(t, () => IsWindows11);
    private static Tweak With10Only(this Tweak t) => Clone(t, () => !IsWindows11);

    private static Tweak Clone(Tweak t, Func<bool> applies) => new()
    {
        Id = t.Id, Category = t.Category, Title = t.Title, What = t.What, Note = t.Note, Recommended = t.Recommended,
        NeedsAdmin = t.NeedsAdmin, Restart = t.Restart, SettingsUri = t.SettingsUri, Read = t.Read, Write = t.Write,
        Specs = t.Specs, Applies = applies
    };

    private static RegistryKey Root(RegistryHive hive) => RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);

    /// <summary>
    /// Un ajuste sólo cuenta como desactivado si TODOS sus valores lo están.
    /// Si quedó a medias (uno sí, otro no) se muestra como activado, para que
    /// el interruptor permita terminar el cambio y no quede un «fantasma».
    /// </summary>
    private static bool? ReadAll(RegSpec[] specs)
    {
        var states = specs.Select(ReadSpec).ToList();
        if (states.Any(s => s is null)) return null;
        return states.Any(s => s == true);
    }

    /// <summary>Valor en bruto (o «(no existe)») para comprobar que no quedan restos.</summary>
    public static string RawValue(RegSpec spec)
    {
        try
        {
            using var root = Root(spec.Hive);
            using var key = root.OpenSubKey(spec.Path);
            return key?.GetValue(spec.Name) is { } v ? Convert.ToString(v) ?? "" : "(no existe)";
        }
        catch { return "(sin acceso)"; }
    }

    private static bool? ReadSpec(RegSpec spec)
    {
        try
        {
            using var root = Root(spec.Hive);
            using var key = root.OpenSubKey(spec.Path);
            var value = key?.GetValue(spec.Name);
            if (value is null) return spec.MissingMeansOn;
            return Convert.ToString(value) == Convert.ToString(spec.On);
        }
        catch { return null; }
    }

    private static void WriteSpec(RegSpec spec, bool on)
    {
        using var root = Root(spec.Hive);
        if (on && spec.DeleteForOn)
        {
            using var existing = root.OpenSubKey(spec.Path, writable: true);
            existing?.DeleteValue(spec.Name, throwOnMissingValue: false);
            return;
        }
        using var key = root.CreateSubKey(spec.Path, writable: true)
                        ?? throw new UnauthorizedAccessException();
        key.SetValue(spec.Name, on ? spec.On : spec.Off, spec.Kind);
    }

    // ────────────────────────── Ajustes especiales ──────────────────────────

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SpiArray(uint action, uint param, int[] pv, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SpiGetBool(uint action, uint param, ref int pv, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    private static extern bool SpiSetPtr(uint action, uint param, IntPtr pv, uint flags);

    private const uint SpiGetMouse = 0x0003, SpiSetMouse = 0x0004;
    private const uint SpiGetClientAreaAnimation = 0x1042, SpiSetClientAreaAnimation = 0x1043;
    private const uint SpifUpdateAndSend = 0x01 | 0x02;

    private static bool? MouseAcceleration()
    {
        var values = new int[3];
        return SpiArray(SpiGetMouse, 0, values, 0) ? values[2] != 0 : null;
    }

    private static void SetMouseAcceleration(bool on)
    {
        // Valores de fábrica de Windows (6, 10, 1) o todo a cero para desactivarla.
        var values = on ? new[] { 6, 10, 1 } : new[] { 0, 0, 0 };
        if (!SpiArray(SpiSetMouse, 0, values, SpifUpdateAndSend)) throw new InvalidOperationException();
    }

    private static bool? ClientAnimations()
    {
        var value = 0;
        return SpiGetBool(SpiGetClientAreaAnimation, 0, ref value, 0) ? value != 0 : null;
    }

    private static void SetClientAnimations(bool on)
    {
        if (!SpiSetPtr(SpiSetClientAreaAnimation, 0, on ? (IntPtr)1 : IntPtr.Zero, SpifUpdateAndSend))
            throw new InvalidOperationException();
        using var key = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop\WindowMetrics");
        key.SetValue("MinAnimate", on ? "1" : "0", RegistryValueKind.String);
    }

    private const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    private const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";

    private static bool? HighPerformancePlan()
    {
        var output = RunPowerCfg("/getactivescheme");
        if (output is null) return null;
        return output.Contains(HighPerformance, StringComparison.OrdinalIgnoreCase) ||
               output.Contains("e9a42b02-d5df-448d-aa00-03f14749eb61", StringComparison.OrdinalIgnoreCase); // máximo rendimiento
    }

    private static void SetHighPerformancePlan(bool on)
    {
        var guid = on ? HighPerformance : Balanced;
        if (RunPowerCfg($"/setactive {guid}", out var code) is null || code != 0)
        {
            // En muchos portátiles el plan viene oculto: se crea una copia y se activa.
            if (on && RunPowerCfg($"/duplicatescheme {HighPerformance} {HighPerformance}", out _) is not null &&
                RunPowerCfg($"/setactive {HighPerformance}", out code) is not null && code == 0)
                return;
            throw new InvalidOperationException("powercfg");
        }
    }

    private static string? RunPowerCfg(string args) => RunPowerCfg(args, out _);

    private static string? RunPowerCfg(string args, out int exitCode)
    {
        exitCode = -1;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            });
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            exitCode = p.ExitCode;
            return output;
        }
        catch { return null; }
    }

    private const string ClassicMenuKey = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    private static bool? ClassicContextMenu()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassicMenuKey + @"\InprocServer32");
            return key is not null;
        }
        catch { return null; }
    }

    private static void SetClassicContextMenu(bool on)
    {
        if (on)
        {
            using var key = Registry.CurrentUser.CreateSubKey(ClassicMenuKey + @"\InprocServer32");
            key.SetValue("", "", RegistryValueKind.String);
        }
        else
        {
            Registry.CurrentUser.DeleteSubKeyTree(ClassicMenuKey, throwOnMissingSubKey: false);
        }
    }
}

/// <summary>Aplica ajustes, comprueba que se quedaron y avisa a Windows del cambio.</summary>
public static class TweakService
{
    public static TweakOutcome Apply(Tweak tweak, bool on)
    {
        if (tweak.Write is null) return TweakOutcome.Blocked;
        if (tweak.NeedsAdmin && !TweakCatalog.IsAdmin) return TweakOutcome.NeedsAdmin;

        try
        {
            tweak.Write(on);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return tweak.NeedsAdmin && !TweakCatalog.IsAdmin ? TweakOutcome.NeedsAdmin : TweakOutcome.Blocked;
        }
        catch
        {
            return TweakOutcome.Failed;
        }

        Broadcast();

        // Algunas claves las protege Windows (p. ej. los widgets): se escribe «bien»
        // pero no se queda. Se comprueba leyendo de nuevo.
        var now = tweak.Read?.Invoke();
        return now is null || now == on ? TweakOutcome.Done : TweakOutcome.Blocked;
    }

    public static void OpenSettings(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return;
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    /// <summary>Cierra y vuelve a abrir el Explorador para que la barra de tareas y los menús se refresquen.</summary>
    public static async Task RestartExplorerAsync()
    {
        foreach (var p in Process.GetProcessesByName("explorer"))
        {
            try { p.Kill(); } catch { }
        }
        await Task.Delay(1500);
        if (Process.GetProcessesByName("explorer").Length == 0)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true }); } catch { }
        }
    }

    /// <summary>Vuelve a abrir la app como administrador, directamente en Optimizar.</summary>
    /// <param name="page">Sección con la que abrir de nuevo: «optimizar», «memoria» o «red».</param>
    public static bool RelaunchAsAdmin(string page = "optimizar")
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;
            Process.Start(new ProcessStartInfo(exe, $"--open {page}") { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch { return false; }   // el usuario dijo que no en el aviso de Windows
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint flags, uint timeout, out IntPtr result);

    private static void Broadcast()
    {
        foreach (var area in new[] { "TraySettings", "Policy", "ImmersiveColorSet", "WindowsThemeElement" })
        {
            try { SendMessageTimeout((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, area, 0x0002, 300, out _); } catch { }
        }
    }
}
