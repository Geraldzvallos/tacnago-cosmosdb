namespace TacnaGo.Api.Models;

/// <summary>
/// Documento JSON que se guarda en el contenedor "Lugares" de Cosmos DB.
/// La clave de partición es /categoria: todos los lugares de una categoría viven en la misma partición lógica.
/// </summary>
public class Lugar
{
    /// <summary>Identificador del documento. Si no se envía, el API lo genera.</summary>
    public string? Id { get; set; }

    /// <summary>Clave de partición (por ejemplo: historia, museos, playas, gastronomia, naturaleza, pueblos).</summary>
    public string Categoria { get; set; } = string.Empty;

    public string Nombre { get; set; } = string.Empty;

    public string? Descripcion { get; set; }

    public string? Provincia { get; set; }

    public string? Distrito { get; set; }

    /// <summary>Etiquetas para búsquedas con ARRAY_CONTAINS.</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>Campos libres: cada documento puede tener datos distintos (esquema flexible).</summary>
    public Dictionary<string, string>? Detalles { get; set; }

    public DateTime? CreadoEn { get; set; }
}

/// <summary>Resultado de la consulta GROUP BY por categoría.</summary>
public class CategoriaResumen
{
    public string Categoria { get; set; } = string.Empty;
    public int Total { get; set; }
}
