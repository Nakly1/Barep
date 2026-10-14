namespace Barep.Services;

/// <summary>Qué representa un nodo del mapa de espacio.</summary>
public enum SpaceNodeKind
{
    /// <summary>Raíz de una unidad (C:\).</summary>
    Drive,
    /// <summary>Raíz virtual con todas las aplicaciones instaladas.</summary>
    AppsRoot,
    Folder,
    File,
    /// <summary>Aplicación registrada sin carpeta conocida: sólo se sabe su tamaño declarado.</summary>
    AppEntry,
    /// <summary>«N archivos más pequeños»: los que no merecen fila propia.</summary>
    Aggregate
}

/// <summary>
/// Un elemento del árbol de espacio. Las carpetas guardan todas sus subcarpetas,
/// pero de los archivos sólo los más grandes: en un disco de sistema hay más de
/// un millón y guardar cada uno costaría cientos de MB de memoria para mostrar
/// filas que nadie va a mirar.
/// </summary>
public sealed class SpaceNode
{
    public SpaceNode(string name, string fullPath, SpaceNodeKind kind, SpaceNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        Kind = kind;
        Parent = parent;
    }

    public string Name { get; }
    public string FullPath { get; }
    public SpaceNodeKind Kind { get; }
    public SpaceNode? Parent { get; internal set; }

    /// <summary>Bytes que ocupa, incluido todo lo que cuelga de él.</summary>
    public long Size { get; internal set; }

    /// <summary>Archivos y carpetas que contiene, a cualquier profundidad.</summary>
    public long ItemCount { get; internal set; }

    public DateTime Modified { get; internal set; }

    /// <summary>Archivos de la nube que no ocupan disco (OneDrive «sólo en línea»).</summary>
    public long CloudOnlyCount { get; internal set; }

    public List<SpaceNode> Children { get; } = new();

    /// <summary>Aplicación instalada que vive en esta carpeta, si la hay.</summary>
    public InstalledApp? App { get; internal set; }

    /// <summary>Se puede entrar en él y ver lo que contiene.</summary>
    public bool IsBrowsable => Kind is SpaceNodeKind.Drive or SpaceNodeKind.AppsRoot
        or SpaceNodeKind.Folder or SpaceNodeKind.AppEntry or SpaceNodeKind.Aggregate;

    /// <summary>Carpeta real en disco (el grupo «N archivos más» vive en la de su padre).</summary>
    public string FolderPath => Kind == SpaceNodeKind.Aggregate ? Parent?.FullPath ?? "" : FullPath;


    /// <summary>Nombre para mostrar: el de la aplicación si la carpeta es una.</summary>
    public string DisplayName => App is not null && Kind != SpaceNodeKind.Drive
        ? App.DisplayName
        : Name;

    public bool IsAncestorOf(SpaceNode other)
    {
        for (var p = other.Parent; p is not null; p = p.Parent)
            if (ReferenceEquals(p, this)) return true;
        return false;
    }

    internal void SortChildren() =>
        Children.Sort((a, b) => b.Size.CompareTo(a.Size));

    /// <summary>
    /// Quita el nodo del árbol y descuenta su peso de todos sus antepasados,
    /// para que el mapa refleje el borrado sin volver a analizar el disco.
    /// </summary>
    public void Detach()
    {
        var parent = Parent;
        if (parent is null) return;

        parent.Children.Remove(this);
        for (var p = parent; p is not null; p = p.Parent)
        {
            p.Size = Math.Max(0, p.Size - Size);
            p.ItemCount = Math.Max(0, p.ItemCount - ItemCount - 1);
        }
        Parent = null;
    }
}

/// <summary>Una aplicación tal como la registra su instalador en Windows.</summary>
public sealed record InstalledApp(
    string DisplayName,
    string? Publisher,
    string? Version,
    string? InstallLocation,
    string? IconPath,
    string? UninstallString,
    string? QuietUninstallString,
    long EstimatedSizeBytes,
    DateTime? InstallDate,
    string RegistryKeyPath,
    string RegistryHive,
    string? HelpLink,
    bool IsPortable = false)
{
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallString);
}

public sealed record ScanProgress(string CurrentPath, long Items, long Bytes);

/// <summary>Un resto de una aplicación desinstalada: carpeta o acceso directo.</summary>
public sealed record Leftover(string Path, long Size, bool IsDirectory, string Reason);
