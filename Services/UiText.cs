namespace Barep.Services;

/// <summary>
/// Las dos cosas que se escriben igual en toda la app: un tamaño y un plural.
/// Viven aquí porque también hacen falta fuera de la interfaz, cuando un
/// servicio tiene que redactar un mensaje para el usuario.
/// </summary>
public static class UiText
{
    /// <summary>Bytes a la unidad que toque: 832 MB, 49,3 GB, 1,2 TB.</summary>
    public static string Size(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;
        return bytes switch
        {
            >= (long)TB => $"{bytes / TB:0.0} TB",
            >= (long)GB => $"{bytes / GB:0.0} GB",
            >= (long)(100 * MB) => $"{bytes / MB:0} MB",
            >= (long)MB => $"{bytes / MB:0.0} MB",
            >= (long)KB => $"{bytes / KB:0} KB",
            _ => $"{bytes} B"
        };
    }

    /// <summary>
    /// «1 módulo» / «2 módulos». Escribir "módulo(s)" es más rápido de programar
    /// y peor de leer; la app habla como una persona.
    /// </summary>
    public static string Plural(int count, string singular, string plural) =>
        count == 1 ? $"1 {singular}" : $"{count:N0} {plural}";
}
