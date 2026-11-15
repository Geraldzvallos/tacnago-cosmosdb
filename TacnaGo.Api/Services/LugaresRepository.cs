using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TacnaGo.Api.Models;

namespace TacnaGo.Api.Services;

/// <summary>Datos devueltos por Cosmos DB junto con su costo en Request Units (RU).</summary>
public record Resultado<T>(T Datos, double Ru);

/// <summary>Acceso al contenedor "Lugares" de Azure Cosmos DB for NoSQL.</summary>
public class LugaresRepository
{
    private readonly Container _container;

    public LugaresRepository(CosmosClient client, IOptions<CosmosSettings> options)
    {
        _container = client.GetContainer(options.Value.Database, options.Value.Container);
    }

    /// <summary>
    /// Sin categoría: consulta entre particiones (cross-partition).
    /// Con categoría: la consulta se envía a una sola partición, que es más barata en RU.
    /// </summary>
    public Task<Resultado<List<Lugar>>> ListarAsync(string? categoria)
    {
        if (string.IsNullOrWhiteSpace(categoria))
            return ConsultarAsync<Lugar>(new QueryDefinition("SELECT * FROM c ORDER BY c.nombre"));

        var cat = Normalizar(categoria);
        var query = new QueryDefinition("SELECT * FROM c WHERE c.categoria = @categoria ORDER BY c.nombre")
            .WithParameter("@categoria", cat);
        return ConsultarAsync<Lugar>(query, new QueryRequestOptions { PartitionKey = new PartitionKey(cat) });
    }

    /// <summary>Búsqueda por etiqueta dentro del arreglo "tags".</summary>
    public Task<Resultado<List<Lugar>>> BuscarPorTagAsync(string tag)
    {
        var query = new QueryDefinition("SELECT * FROM c WHERE ARRAY_CONTAINS(c.tags, @tag) ORDER BY c.nombre")
            .WithParameter("@tag", tag.Trim().ToLowerInvariant());
        return ConsultarAsync<Lugar>(query);
    }

    /// <summary>Cantidad de lugares por categoría (GROUP BY).</summary>
    public async Task<Resultado<List<CategoriaResumen>>> CategoriasAsync()
    {
        var r = await ConsultarAsync<CategoriaResumen>(
            new QueryDefinition("SELECT c.categoria, COUNT(1) AS total FROM c GROUP BY c.categoria"));
        return r with { Datos = r.Datos.OrderBy(x => x.Categoria).ToList() };
    }

    /// <summary>Lectura puntual por id + clave de partición: la operación más barata de Cosmos DB (~1 RU).</summary>
    public async Task<Resultado<Lugar>?> ObtenerAsync(string categoria, string id)
    {
        try
        {
            var resp = await _container.ReadItemAsync<Lugar>(id, new PartitionKey(Normalizar(categoria)));
            return new Resultado<Lugar>(resp.Resource, resp.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<Resultado<Lugar>?> CrearAsync(Lugar lugar)
    {
        Preparar(lugar);
        lugar.Id = string.IsNullOrWhiteSpace(lugar.Id) ? Guid.NewGuid().ToString("n") : lugar.Id.Trim();
        lugar.CreadoEn ??= DateTime.UtcNow;
        try
        {
            var resp = await _container.CreateItemAsync(lugar, new PartitionKey(lugar.Categoria));
            return new Resultado<Lugar>(resp.Resource, resp.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.Conflict)
        {
            return null;
        }
    }

    public async Task<Resultado<Lugar>?> ReemplazarAsync(string id, Lugar lugar)
    {
        Preparar(lugar);
        lugar.Id = id;
        try
        {
            var resp = await _container.ReplaceItemAsync(lugar, id, new PartitionKey(lugar.Categoria));
            return new Resultado<Lugar>(resp.Resource, resp.RequestCharge);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<double?> EliminarAsync(string categoria, string id)
    {
        try
        {
            var resp = await _container.DeleteItemAsync<Lugar>(id, new PartitionKey(Normalizar(categoria)));
            return resp.RequestCharge;
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Recorre todas las páginas de una consulta y suma el costo en RU.</summary>
    private async Task<Resultado<List<T>>> ConsultarAsync<T>(QueryDefinition query, QueryRequestOptions? options = null)
    {
        var items = new List<T>();
        double ru = 0;
        using var feed = _container.GetItemQueryIterator<T>(query, requestOptions: options);
        while (feed.HasMoreResults)
        {
            var page = await feed.ReadNextAsync();
            items.AddRange(page);
            ru += page.RequestCharge;
        }
        return new Resultado<List<T>>(items, ru);
    }

    private static void Preparar(Lugar lugar)
    {
        lugar.Categoria = Normalizar(lugar.Categoria);
        lugar.Nombre = lugar.Nombre.Trim();
        lugar.Tags = lugar.Tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    /// <summary>"Gastronomía " -> "gastronomia": así una categoría siempre cae en la misma partición.</summary>
    public static string Normalizar(string texto)
    {
        var sb = new StringBuilder();
        foreach (var ch in texto.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.IsWhiteSpace(ch) ? '-' : ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
