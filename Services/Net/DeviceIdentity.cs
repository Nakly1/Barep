using System.Globalization;
using System.Text.RegularExpressions;

namespace Barep.Services.Net;

/// <summary>
/// Junta todas las pistas de un aparato (su nombre, lo que anuncia por mDNS y
/// UPnP, sus puertos y el fabricante de su tarjeta de red) y decide qué es,
/// de qué marca y, si se puede saber, qué modelo. Anota de dónde salió el
/// modelo para no hacer pasar una suposición por un dato.
/// </summary>
public static class DeviceIdentity
{
    public const string SourceSelf = "Lo anuncia el propio aparato";
    public const string SourceName = "Por el nombre que usa en la red";
    public const string SourceVendor = "Por el fabricante de su tarjeta de red";
    public const string SourceRouter = "Por la página de acceso del router";

    public static void Identify(NetDevice d)
    {
        d.Vendor = ShortVendor(d.VendorFull);
        // Intel, Realtek y compañía fabrican la tarjeta Wi-Fi, no el portátil.
        d.Brand = d.Vendor is not null && ChipMakers.Contains(d.Vendor) ? null : d.Vendor;

        var name = FirstHuman(d);
        d.FriendlyName = name;

        // 1. Lo que el aparato dice de sí mismo, de más a menos fiable.
        if (!FromApple(d) && !FromCast(d) && !FromPrinterTxt(d) && !FromUpnp(d))
            FromHostName(d);

        if (d.IsGateway)
        {
            d.Kind = DeviceKind.Router;
            if (d.Model is null && d.WebTitle is { } title)
            {
                d.Model = title;
                d.ModelSource = SourceRouter;
                d.Brand ??= BrandIn(title);
            }
        }
        else if (d.IsSelf)
        {
            d.Kind = DeviceKind.Computer;
        }

        // 2. Qué es, si aún no se sabe.
        if (d.Kind == DeviceKind.Unknown) d.Kind = KindFromServices(d);
        if (d.Kind == DeviceKind.Unknown && HostKind(d) is { } hostKind) d.Kind = hostKind;
        if (d.Kind == DeviceKind.Unknown)
        {
            d.Kind = KindFromVendor(d.Vendor);
            d.KindIsGuess = d.Kind != DeviceKind.Unknown;
        }
        if (d.Kind == DeviceKind.Unknown && d.RandomMac)
        {
            // Casi todo lo que usa dirección privada es un móvil o una tableta.
            d.Kind = DeviceKind.Phone;
            d.KindIsGuess = true;
        }

        // Sin modelo, la marca sale sólo de la tarjeta de red: se dice así.
        if (d.Model is null && d.Brand is not null && d.Brand == d.Vendor) d.ModelSource ??= SourceVendor;

        // La marca deducida de la tarjeta no vale cuando el fabricante es de chips.
        if (d.Kind == DeviceKind.Unknown && d.Vendor is not null && ChipMakers.Contains(d.Vendor))
        {
            d.Kind = DeviceKind.Computer;
            d.KindIsGuess = true;
        }
    }

    // ────────────────────────── Apple ──────────────────────────

    private static readonly Dictionary<string, string> AppleModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["iPhone12,1"] = "iPhone 11", ["iPhone12,3"] = "iPhone 11 Pro", ["iPhone12,5"] = "iPhone 11 Pro Max",
        ["iPhone12,8"] = "iPhone SE (2.ª gen.)",
        ["iPhone13,1"] = "iPhone 12 mini", ["iPhone13,2"] = "iPhone 12", ["iPhone13,3"] = "iPhone 12 Pro",
        ["iPhone13,4"] = "iPhone 12 Pro Max",
        ["iPhone14,4"] = "iPhone 13 mini", ["iPhone14,5"] = "iPhone 13", ["iPhone14,2"] = "iPhone 13 Pro",
        ["iPhone14,3"] = "iPhone 13 Pro Max", ["iPhone14,6"] = "iPhone SE (3.ª gen.)",
        ["iPhone14,7"] = "iPhone 14", ["iPhone14,8"] = "iPhone 14 Plus", ["iPhone15,2"] = "iPhone 14 Pro",
        ["iPhone15,3"] = "iPhone 14 Pro Max",
        ["iPhone15,4"] = "iPhone 15", ["iPhone15,5"] = "iPhone 15 Plus", ["iPhone16,1"] = "iPhone 15 Pro",
        ["iPhone16,2"] = "iPhone 15 Pro Max",
        ["iPhone17,3"] = "iPhone 16", ["iPhone17,4"] = "iPhone 16 Plus", ["iPhone17,1"] = "iPhone 16 Pro",
        ["iPhone17,2"] = "iPhone 16 Pro Max", ["iPhone17,5"] = "iPhone 16e",
        ["MacBookAir10,1"] = "MacBook Air (M1, 2020)", ["Mac14,2"] = "MacBook Air 13\" (M2)",
        ["Mac14,15"] = "MacBook Air 15\" (M2)", ["Mac15,12"] = "MacBook Air 13\" (M3)",
        ["Mac15,13"] = "MacBook Air 15\" (M3)", ["MacBookPro17,1"] = "MacBook Pro 13\" (M1)",
        ["MacBookPro18,1"] = "MacBook Pro 16\" (M1 Pro)", ["MacBookPro18,2"] = "MacBook Pro 16\" (M1 Max)",
        ["MacBookPro18,3"] = "MacBook Pro 14\" (M1 Pro)", ["MacBookPro18,4"] = "MacBook Pro 14\" (M1 Max)",
        ["Macmini9,1"] = "Mac mini (M1)", ["iMac21,1"] = "iMac 24\" (M1)", ["iMac21,2"] = "iMac 24\" (M1)",
        ["AppleTV5,3"] = "Apple TV HD", ["AppleTV6,2"] = "Apple TV 4K", ["AppleTV11,1"] = "Apple TV 4K (2.ª gen.)",
        ["AppleTV14,1"] = "Apple TV 4K (3.ª gen.)",
        ["AudioAccessory1,1"] = "HomePod", ["AudioAccessory5,1"] = "HomePod mini",
        ["AudioAccessory6,1"] = "HomePod (2.ª gen.)"
    };

    private static bool FromApple(NetDevice d)
    {
        var id = new[] { "model", "rpMd", "am" }
            .Select(k => d.Txt.TryGetValue(k, out var v) ? v : null)
            .FirstOrDefault(v => v is not null && Regex.IsMatch(v, @"^(iPhone|iPad|iPod|Mac|MacBook\w*|iMac\w*|AppleTV|AudioAccessory|Watch)\d+,\d+$"));
        if (id is null) return false;

        d.Brand = "Apple";
        d.Model = AppleModels.TryGetValue(id, out var name) ? name : AppleFamily(id) + $" ({id})";
        d.ModelSource = SourceSelf;
        d.Kind = id switch
        {
            _ when id.StartsWith("iPhone") || id.StartsWith("iPod") => DeviceKind.Phone,
            _ when id.StartsWith("iPad") => DeviceKind.Tablet,
            _ when id.StartsWith("AppleTV") => DeviceKind.Tv,
            _ when id.StartsWith("AudioAccessory") => DeviceKind.Speaker,
            _ when id.StartsWith("Watch") => DeviceKind.Watch,
            _ => DeviceKind.Computer
        };
        return true;
    }

    private static string AppleFamily(string id) => id switch
    {
        _ when id.StartsWith("iPhone") => "iPhone",
        _ when id.StartsWith("iPad") => "iPad",
        _ when id.StartsWith("MacBookAir") => "MacBook Air",
        _ when id.StartsWith("MacBookPro") => "MacBook Pro",
        _ when id.StartsWith("iMac") => "iMac",
        _ when id.StartsWith("AppleTV") => "Apple TV",
        _ when id.StartsWith("AudioAccessory") => "HomePod",
        _ when id.StartsWith("Watch") => "Apple Watch",
        _ => "Mac"
    };

    // ────────────────────────── Chromecast / Google TV ──────────────────────────

    private static bool FromCast(NetDevice d)
    {
        if (!d.Txt.TryGetValue("md", out var model) || string.IsNullOrWhiteSpace(model)) return false;

        d.Model = model;
        d.ModelSource = SourceSelf;
        if (d.Txt.TryGetValue("fn", out var friendly) && friendly.Length > 0) d.FriendlyName = friendly;

        d.Brand = BrandIn(model) ?? (model.Contains("Chromecast", StringComparison.OrdinalIgnoreCase) ||
                                     model.Contains("Nest", StringComparison.OrdinalIgnoreCase)
            ? "Google"
            : d.Vendor);
        d.Kind = Regex.IsMatch(model, @"Nest (Mini|Audio|Hub)|Google Home|Speaker|Soundbar|Audio", RegexOptions.IgnoreCase)
            ? DeviceKind.Speaker
            : DeviceKind.Tv;
        return true;
    }

    // ────────────────────────── Impresoras ──────────────────────────

    private static bool FromPrinterTxt(NetDevice d)
    {
        var model = new[] { "ty", "product", "usb_MDL" }
            .Select(k => d.Txt.TryGetValue(k, out var v) ? v.Trim('(', ')', ' ') : null)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (model is null) return false;

        d.Model = model;
        d.ModelSource = SourceSelf;
        d.Brand = BrandIn(model) ?? (d.Txt.TryGetValue("usb_MFG", out var mfg) ? mfg : d.Vendor);
        d.Kind = DeviceKind.Printer;
        return true;
    }

    // ────────────────────────── UPnP ──────────────────────────

    private static bool FromUpnp(NetDevice d)
    {
        if (d.UpnpModel is null && d.UpnpName is null) return false;

        if (d.UpnpModel is not null)
        {
            d.Model = d.UpnpModel;
            d.ModelSource = SourceSelf;
        }
        if (d.UpnpManufacturer is { } maker) d.Brand = ShortVendor(maker) ?? maker;
        if (d.FriendlyName is null && d.UpnpName is not null) d.FriendlyName = d.UpnpName;

        var type = d.UpnpType ?? "";
        if (type.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("WLANAccessPoint", StringComparison.OrdinalIgnoreCase))
            d.Kind = DeviceKind.Router;
        else if (type.Contains("Printer", StringComparison.OrdinalIgnoreCase))
            d.Kind = DeviceKind.Printer;
        else if (type.Contains("MediaRenderer", StringComparison.OrdinalIgnoreCase) ||
                 type.Contains("dial", StringComparison.OrdinalIgnoreCase))
            d.Kind = DeviceKind.Tv;
        else if (type.Contains("MediaServer", StringComparison.OrdinalIgnoreCase) ||
                 type.Contains("Storage", StringComparison.OrdinalIgnoreCase))
            d.Kind = DeviceKind.Storage;

        return d.Model is not null;
    }

    // ────────────────────────── Nombre de red ──────────────────────────

    /// <summary>
    /// Regla de nombre de red. Si <paramref name="IsModel"/> es falso, el texto no es un
    /// modelo sino una descripción («Móvil Android»): se usa como nombre y el modelo
    /// queda en blanco, para no inventarlo.
    /// </summary>
    private sealed record HostRule(Regex Pattern, string Brand, string Prefix, DeviceKind Kind, bool IsModel);

    private static HostRule Rule(string pattern, string brand, string prefix, DeviceKind kind, bool model = true) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), brand, prefix, kind, model);

    /// <summary>Nombres que Android, iOS y los fabricantes ponen por defecto al conectarse.</summary>
    private static readonly HostRule[] HostRules =
    {
        Rule(@"^(?:samsung-)?galaxy-(?<m>.+)$", "Samsung", "Galaxy ", DeviceKind.Phone),
        Rule(@"^(?:samsung-)?(?<m>SM-[A-Z]\d{3,4}\w*)$", "Samsung", "Galaxy ", DeviceKind.Phone),
        Rule(@"^redmi-(?<m>.+)$", "Xiaomi", "Redmi ", DeviceKind.Phone),
        Rule(@"^poco-(?<m>.+)$", "Xiaomi", "POCO ", DeviceKind.Phone),
        Rule(@"^(?:xiaomi|mi)-(?<m>.+)$", "Xiaomi", "", DeviceKind.Phone),
        Rule(@"^huawei-(?<m>.+)$", "Huawei", "", DeviceKind.Phone),
        Rule(@"^honor-(?<m>.+)$", "Honor", "", DeviceKind.Phone),
        Rule(@"^oppo-(?<m>.+)$", "OPPO", "", DeviceKind.Phone),
        Rule(@"^realme-(?<m>.+)$", "realme", "", DeviceKind.Phone),
        Rule(@"^vivo-(?<m>.+)$", "vivo", "", DeviceKind.Phone),
        Rule(@"^moto-(?<m>.+)$", "Motorola", "moto ", DeviceKind.Phone),
        Rule(@"^motorola-(?<m>.+)$", "Motorola", "", DeviceKind.Phone),
        Rule(@"^oneplus-?(?<m>.+)$", "OnePlus", "", DeviceKind.Phone),
        Rule(@"^pixel-(?<m>.+)$", "Google", "Pixel ", DeviceKind.Phone),
        Rule(@"^nokia-(?<m>.+)$", "Nokia", "", DeviceKind.Phone),
        Rule(@"^(?<m>infinix-.+)$", "Infinix", "", DeviceKind.Phone),
        Rule(@"^(?<m>tecno-.+)$", "TECNO", "", DeviceKind.Phone),
        Rule(@"^(?:.*-)?iphone(?:-de-.+|-.+)?$", "Apple", "iPhone", DeviceKind.Phone),
        Rule(@"^(?:.*-)?ipad(?:-.+)?$", "Apple", "iPad", DeviceKind.Tablet),
        Rule(@"^(?:.*-)?(?:macbook-?pro|macbook-?air|macbook)(?:-.+)?$", "Apple", "MacBook", DeviceKind.Computer),
        Rule(@"^(?:.*-)?imac(?:-.+)?$", "Apple", "iMac", DeviceKind.Computer),
        Rule(@"^(?:.*-)?apple-?tv(?:-.+)?$", "Apple", "Apple TV", DeviceKind.Tv),
        Rule(@"^(?:.*-)?apple-?watch(?:-.+)?$", "Apple", "Apple Watch", DeviceKind.Watch),
        Rule(@"^android-[0-9a-f]{6,}$", "", "Móvil Android", DeviceKind.Phone, model: false),
        Rule(@"^(?:desktop|laptop)-[a-z0-9]{5,}$", "", "", DeviceKind.Computer, model: false),
        Rule(@"^chromecast", "Google", "Chromecast", DeviceKind.Tv),
        Rule(@"^google-?(?:home|nest)", "Google", "Google Nest", DeviceKind.Speaker),
        Rule(@"^(?:lgwebostv|lg-?tv|webos)", "LG", "Smart TV (webOS)", DeviceKind.Tv),
        Rule(@"^(?:samsung-?tv|\[tv\]|tizen)", "Samsung", "Smart TV", DeviceKind.Tv),
        Rule(@"^bravia", "Sony", "BRAVIA", DeviceKind.Tv),
        Rule(@"^roku", "Roku", "Roku", DeviceKind.Tv),
        Rule(@"^(?:amazon-|fire-?tv|aft)", "Amazon", "Fire TV", DeviceKind.Tv),
        Rule(@"^echo", "Amazon", "Echo", DeviceKind.Speaker),
        Rule(@"^xbox", "Microsoft", "Xbox", DeviceKind.Console),
        Rule(@"^(?:ps4|ps5|playstation)", "Sony", "PlayStation", DeviceKind.Console),
        Rule(@"^(?:nintendo|switch)", "Nintendo", "Nintendo Switch", DeviceKind.Console),
        Rule(@"^(?:esp|espressif)[-_]", "Espressif", "Placa ESP (domótica)", DeviceKind.SmartHome, model: false),
        Rule(@"^(?:shelly|sonoff|tasmota|tuya|wiz|tapo|kasa|smart-?plug)", "", "Aparato de domótica", DeviceKind.SmartHome, model: false),
        Rule(@"^brn[0-9a-f]{6,}", "Brother", "Impresora Brother", DeviceKind.Printer, model: false),
        Rule(@"^(?:hp[0-9a-f]{6}|npi[0-9a-f]{6})", "HP", "Impresora HP", DeviceKind.Printer, model: false),
        Rule(@"^(?:epson|canon)", "", "Impresora", DeviceKind.Printer, model: false),
        Rule(@"^raspberrypi", "Raspberry Pi", "Raspberry Pi", DeviceKind.Computer)
    };

    private static void FromHostName(NetDevice d)
    {
        foreach (var raw in Names(d))
        {
            var host = raw.Replace('_', '-').Replace(' ', '-');
            foreach (var rule in HostRules)
            {
                var match = rule.Pattern.Match(host);
                if (!match.Success) continue;

                if (rule.Brand.Length > 0) d.Brand = rule.Brand;
                d.Kind = rule.Kind;

                if (!rule.IsModel)
                {
                    // «DESKTOP-AB12CD3» es el nombre del PC que su dueño reconoce.
                    d.FriendlyName ??= rule.Prefix.Length > 0 ? rule.Prefix : raw.ToUpperInvariant();
                    return;
                }

                var model = match.Groups["m"].Success ? Pretty(match.Groups["m"].Value) : "";
                d.Model = (rule.Prefix + model).Trim();
                if (d.Model.Length == 0) d.Model = null;
                d.ModelSource = SourceName;
                return;
            }
        }
    }

    private static DeviceKind? HostKind(NetDevice d)
    {
        foreach (var raw in Names(d))
        {
            var host = raw.ToLowerInvariant();
            if (Regex.IsMatch(host, @"phone|movil|celular|android")) return DeviceKind.Phone;
            if (Regex.IsMatch(host, @"tablet|tab-|ipad")) return DeviceKind.Tablet;
            if (Regex.IsMatch(host, @"tv|television|roku|cast")) return DeviceKind.Tv;
            if (Regex.IsMatch(host, @"printer|impresora|laserjet|deskjet|officejet")) return DeviceKind.Printer;
            if (Regex.IsMatch(host, @"cam|camara|dvr|nvr")) return DeviceKind.Camera;
            if (Regex.IsMatch(host, @"laptop|desktop|pc-|-pc|notebook|portatil|workstation")) return DeviceKind.Computer;
            if (Regex.IsMatch(host, @"nas|diskstation|synology|qnap")) return DeviceKind.Storage;
            if (Regex.IsMatch(host, @"watch|reloj|band")) return DeviceKind.Watch;
            if (Regex.IsMatch(host, @"repeater|repetidor|extender|mesh|deco")) return DeviceKind.Router;
        }
        return null;
    }

    private static IEnumerable<string> Names(NetDevice d)
    {
        foreach (var n in new[] { d.MdnsHost, d.HostName, d.NetbiosName })
            if (!string.IsNullOrWhiteSpace(n)) yield return n;
        foreach (var n in d.MdnsInstances) yield return n;
    }

    /// <summary>«note-12-pro» → «Note 12 Pro»; «s23-ultra» → «S23 Ultra».</summary>
    private static string Pretty(string text)
    {
        var words = text.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length <= 3 && w.Any(char.IsDigit) || w.All(char.IsUpper)
                ? w.ToUpperInvariant()
                : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return string.Join(' ', words);
    }

    /// <summary>
    /// Un nombre puesto por una persona: «iPhone de Ana», «Salón», «Nakly's
    /// MacBook». Los nombres de fábrica («Galaxy-S23», «android-3f2a…») no
    /// cuentan: para esos es mejor mostrar marca y modelo.
    /// </summary>
    private static string? FirstHuman(NetDevice d)
    {
        var announced = d.MdnsInstances.Concat(new[] { d.UpnpName })
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!.Trim());
        foreach (var name in announced)
            if (LooksHuman(name, announced: true)) return name.Replace('_', ' ');

        foreach (var name in new[] { d.MdnsHost, d.HostName })
        {
            if (string.IsNullOrWhiteSpace(name) || !LooksHuman(name, announced: false)) continue;
            return name.Replace('_', ' ').Replace('-', ' ');
        }

        // El nombre NetBIOS es el «nombre del equipo» de Windows: su dueño lo reconoce.
        return string.IsNullOrWhiteSpace(d.NetbiosName) ? null : d.NetbiosName;
    }

    private static bool LooksHuman(string name, bool announced)
    {
        if (name.Contains('@')) return false;                                                     // «mac@ip»
        if (Regex.IsMatch(name, @"^[0-9a-f\-:]{12,}$", RegexOptions.IgnoreCase)) return false;  // identificadores
        if (Regex.IsMatch(name, @"^(android|esp|brn|hp|npi)[-_]?[0-9a-f]{6,}$", RegexOptions.IgnoreCase)) return false;
        if (announced) return true;

        // De un nombre de host sólo valen los posesivos: «iPhone-de-Ana», «Ana-s-iPhone».
        return Regex.IsMatch(name, @"(^|[-_ ])(de|of)([-_ ]|$)|[-_]s[-_]|'s[-_ ]", RegexOptions.IgnoreCase);
    }

    // ────────────────────────── Servicios y puertos ──────────────────────────

    private static DeviceKind KindFromServices(NetDevice d)
    {
        bool Has(params string[] services) => services.Any(d.Services.Contains);
        bool Port(params int[] ports) => ports.Any(d.OpenPorts.Contains);

        if (Has("_ipp._tcp", "_ipps._tcp", "_printer._tcp", "_pdl-datastream._tcp", "_scanner._tcp") || Port(9100, 631))
            return DeviceKind.Printer;
        if (Has("_sonos._tcp") || Port(1400)) { d.Brand ??= "Sonos"; return DeviceKind.Speaker; }
        if (Has("_googlecast._tcp", "_androidtvremote2._tcp", "_amzn-wplay._tcp", "_mediaremotetv._tcp") ||
            Port(8008, 8009, 8060))
            return DeviceKind.Tv;
        if (Has("_apple-mobdev2._tcp") || Port(62078))
        {
            d.Brand = "Apple";
            var isPad = Names(d).Any(n => n.Contains("ipad", StringComparison.OrdinalIgnoreCase));
            d.Model ??= isPad ? "iPad" : "iPhone";
            d.ModelSource ??= SourceSelf;
            return isPad ? DeviceKind.Tablet : DeviceKind.Phone;
        }
        if (Has("_airplay._tcp", "_raop._tcp"))
            return d.Vendor == "Apple" ? DeviceKind.Computer : DeviceKind.Tv;
        if (Has("_adb-tls-connect._tcp", "_adb-tls-pairing._tcp") || Port(5555)) return DeviceKind.Phone;
        if (Port(554)) return DeviceKind.Camera;
        if (Has("_hap._tcp", "_homekit._tcp", "_matter._tcp", "_hue._tcp", "_miio._udp", "_esphomelib._tcp"))
            return DeviceKind.SmartHome;
        if (Has("_smb._tcp", "_workstation._tcp", "_rdlink._tcp") || Port(3389, 445) || d.NetbiosName is not null)
            return DeviceKind.Computer;
        if (Has("_companion-link._tcp") && d.Vendor == "Apple") return DeviceKind.Phone;
        return DeviceKind.Unknown;
    }

    // ────────────────────────── Fabricante ──────────────────────────

    private static readonly (string Match, string Brand)[] Brands =
    {
        ("APPLE", "Apple"), ("SAMSUNG", "Samsung"), ("XIAOMI", "Xiaomi"), ("BEIJING XIAOMI", "Xiaomi"),
        ("HUAWEI", "Huawei"), ("HONOR DEVICE", "Honor"), ("GUANGDONG OPPO", "OPPO"), ("OPPO", "OPPO"),
        ("VIVO MOBILE", "vivo"), ("REALME", "realme"), ("MOTOROLA", "Motorola"), ("LENOVO", "Lenovo"),
        ("ONEPLUS", "OnePlus"), ("GOOGLE", "Google"), ("NOKIA", "Nokia"), ("HMD GLOBAL", "Nokia"),
        ("TRANSSION", "TECNO / Infinix"), ("ZTE", "ZTE"), ("TP-LINK", "TP-Link"), ("TPLINK", "TP-Link"),
        ("TENDA", "Tenda"), ("NETGEAR", "Netgear"), ("ASUSTEK", "ASUS"), ("D-LINK", "D-Link"),
        ("UBIQUITI", "Ubiquiti"), ("ROUTERBOARD", "MikroTik"), ("MIKROTIK", "MikroTik"), ("ARRIS", "Arris"),
        ("TECHNICOLOR", "Technicolor"), ("SAGEMCOM", "Sagemcom"), ("MERCUSYS", "Mercusys"), ("LINKSYS", "Linksys"),
        ("CISCO", "Cisco"), ("INTEL", "Intel"), ("REALTEK", "Realtek"), ("LITE-ON", "Lite-On"),
        ("LITEON", "Lite-On"), ("AZUREWAVE", "AzureWave"), ("HON HAI", "Foxconn"), ("FOXCONN", "Foxconn"),
        ("QUALCOMM", "Qualcomm"), ("MEDIATEK", "MediaTek"), ("BROADCOM", "Broadcom"), ("LG ELECTRONICS", "LG"),
        ("LG INNOTEK", "LG"), ("SONY INTERACTIVE", "Sony PlayStation"), ("SONY", "Sony"), ("MICROSOFT", "Microsoft"),
        ("NINTENDO", "Nintendo"), ("AMAZON", "Amazon"), ("ROKU", "Roku"), ("ESPRESSIF", "Espressif"),
        ("RASPBERRY", "Raspberry Pi"), ("HEWLETT", "HP"), ("HP INC", "HP"), ("SEIKO EPSON", "Epson"),
        ("EPSON", "Epson"), ("CANON", "Canon"), ("BROTHER", "Brother"), ("XEROX", "Xerox"), ("DELL", "Dell"),
        ("TCL", "TCL"), ("HISENSE", "Hisense"), ("VIZIO", "Vizio"), ("SONOS", "Sonos"), ("BELKIN", "Belkin"),
        ("TUYA", "Tuya"), ("SHELLY", "Shelly"), ("ITEAD", "Sonoff"), ("SYNOLOGY", "Synology"), ("QNAP", "QNAP"),
        ("WESTERN DIGITAL", "Western Digital"), ("HIKVISION", "Hikvision"), ("DAHUA", "Dahua"),
        ("EZVIZ", "EZVIZ"), ("REOLINK", "Reolink"), ("WYZE", "Wyze"), ("PHILIPS", "Philips"),
        ("SIGNIFY", "Philips Hue"), ("ACER", "Acer"), ("MSI", "MSI"), ("GIGA-BYTE", "Gigabyte"),
        ("ASROCK", "ASRock"), ("VMWARE", "VMware"), ("NVIDIA", "NVIDIA"), ("XBOX", "Microsoft Xbox")
    };

    private static readonly HashSet<string> ChipMakers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Intel", "Realtek", "Lite-On", "AzureWave", "Foxconn", "Qualcomm", "MediaTek", "Broadcom"
    };

    public static string? ShortVendor(string? full)
    {
        if (string.IsNullOrWhiteSpace(full)) return null;
        var upper = full.ToUpperInvariant();
        foreach (var (match, brand) in Brands)
            if (upper.Contains(match)) return brand;

        // Sin marca conocida: «SHENZHEN BILIAN ELECTRONIC CO.,LTD» → «Shenzhen Bilian».
        var clean = Regex.Replace(full, @"[,.]?\s*(CO\.?,?\s*LTD\.?|LTD\.?|INC\.?|LLC|GMBH|CORP(ORATION)?\.?|S\.?A\.?|LIMITED|TECHNOLOG(Y|IES)|ELECTRONICS?|COMPANY|INTERNATIONAL)\b.*$",
            "", RegexOptions.IgnoreCase).Trim(' ', ',', '.');
        if (clean.Length == 0) clean = full;
        var words = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2);
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(string.Join(' ', words).ToLowerInvariant());
    }

    private static string? BrandIn(string text)
    {
        var upper = text.ToUpperInvariant();
        foreach (var (match, brand) in Brands)
            if (upper.Contains(match)) return brand;
        return null;
    }

    private static DeviceKind KindFromVendor(string? vendor) => vendor switch
    {
        "Samsung" or "Xiaomi" or "OPPO" or "vivo" or "realme" or "Motorola" or "Honor" or "OnePlus" or "Huawei"
            or "Google" or "Nokia" or "TECNO / Infinix" or "Apple" => DeviceKind.Phone,
        "Espressif" or "Tuya" or "Shelly" or "Sonoff" or "Philips Hue" => DeviceKind.SmartHome,
        "Nintendo" or "Sony PlayStation" or "Microsoft Xbox" => DeviceKind.Console,
        "Amazon" or "Sonos" => DeviceKind.Speaker,
        "Roku" or "TCL" or "Hisense" or "Vizio" or "LG" => DeviceKind.Tv,
        "Raspberry Pi" or "Intel" or "Realtek" or "Lite-On" or "AzureWave" or "Foxconn" or "Qualcomm" or "MediaTek"
            or "Dell" or "Lenovo" or "Acer" or "MSI" or "Gigabyte" or "ASRock" => DeviceKind.Computer,
        "HP" or "Epson" or "Canon" or "Brother" or "Xerox" => DeviceKind.Printer,
        "Synology" or "QNAP" or "Western Digital" => DeviceKind.Storage,
        "Hikvision" or "Dahua" or "EZVIZ" or "Reolink" or "Wyze" => DeviceKind.Camera,
        _ => DeviceKind.Unknown
    };

    // ────────────────────────── Textos ──────────────────────────

    public static string KindName(DeviceKind kind) => kind switch
    {
        DeviceKind.Router => "Router",
        DeviceKind.Phone => "Móvil",
        DeviceKind.Tablet => "Tableta",
        DeviceKind.Computer => "Ordenador",
        DeviceKind.Tv => "TV o reproductor",
        DeviceKind.Console => "Consola",
        DeviceKind.Printer => "Impresora",
        DeviceKind.Speaker => "Altavoz",
        DeviceKind.Camera => "Cámara",
        DeviceKind.Watch => "Reloj",
        DeviceKind.SmartHome => "Domótica",
        DeviceKind.Storage => "Almacenamiento (NAS)",
        _ => "Desconocido"
    };

    /// <summary>El nombre que se muestra: el puesto por el usuario, el que anuncia o «Marca Modelo».</summary>
    public static string DisplayName(NetDevice d, string? alias)
    {
        if (!string.IsNullOrWhiteSpace(alias)) return alias;
        if (d.IsSelf) return "Este PC";
        if (d.FriendlyName is { } friendly) return friendly;
        if (d.Model is { } model)
            return d.Brand is not null && !model.StartsWith(d.Brand, StringComparison.OrdinalIgnoreCase) &&
                   d.Brand != "Apple" ? $"{d.Brand} {model}" : model;
        if (d.Brand is { } brand) return d.Kind == DeviceKind.Unknown ? $"Aparato {brand}" : $"{KindName(d.Kind)} {brand}";
        if (d.IsGateway) return "Router";
        if (d.RandomMac && d.Kind is DeviceKind.Phone or DeviceKind.Unknown) return "Móvil o tableta (dirección privada)";
        return d.Kind != DeviceKind.Unknown ? KindName(d.Kind) : $"Aparato en {d.IpText}";
    }
}
