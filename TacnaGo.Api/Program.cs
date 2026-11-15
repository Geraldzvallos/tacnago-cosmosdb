using System.Globalization;
using System.Text.Json;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;
using TacnaGo.Api.Models;
using TacnaGo.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuración de Cosmos DB: sección "Cosmos" de appsettings.json.
// En Azure se sobrescribe con las variables de entorno Cosmos__Endpoint y Cosmos__Key (las define Terraform).
builder.Services.Configure<CosmosSettings>(builder.Configuration.GetSection("Cosmos"));

// Un solo CosmosClient para toda la aplicación: es thread-safe y reutiliza las conexiones.
builder.Services.AddSingleton(sp =>
{
    var s = sp.GetRequiredService<IOptions<CosmosSettings>>().Value;
    if (string.IsNullOrWhiteSpace(s.Endpoint) || string.IsNullOrWhiteSpace(s.Key))
        throw new InvalidOperationException("Falta configurar Cosmos:Endpoint y Cosmos:Key.");

    return new CosmosClient(s.Endpoint, s.Key, new CosmosClientOptions
    {
        ApplicationName = "TacnaGo",
        // JSON en camelCase ("id", "categoria"): coincide con la clave de partición /categoria
        UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    });
});
builder.Services.AddSingleton<LugaresRepository>();

// La plantilla de .NET 9 ya no trae Swagger: se usa Swashbuckle para /swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => c.SwaggerDoc("v1", new()
{
    Title = "TacnaGo API",
    Version = "v1",
    Description = "Guía de lugares turísticos de Tacna sobre Azure Cosmos DB for NoSQL. " +
                  "Cada respuesta incluye el encabezado x-ru-charge con el costo de la operación en Request Units."
}));

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseDefaultFiles();   // sirve wwwroot/index.html en la raíz
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { estado = "ok" })).ExcludeFromDescription();

var api = app.MapGroup("/api").WithTags("Lugares");

api.MapGet("/lugares", async (string? categoria, LugaresRepository repo, HttpContext ctx) =>
        Ok(ctx, await repo.ListarAsync(categoria)))
    .WithSummary("Lista los lugares. Con ?categoria= la consulta va a una sola partición.");

api.MapGet("/lugares/buscar", async (string tag, LugaresRepository repo, HttpContext ctx) =>
        Ok(ctx, await repo.BuscarPorTagAsync(tag)))
    .WithSummary("Busca lugares por etiqueta (ARRAY_CONTAINS).");

api.MapGet("/categorias", async (LugaresRepository repo, HttpContext ctx) =>
        Ok(ctx, await repo.CategoriasAsync()))
    .WithSummary("Cantidad de lugares por categoría (GROUP BY).");

api.MapGet("/lugares/{categoria}/{id}", async (string categoria, string id, LugaresRepository repo, HttpContext ctx) =>
    {
        var r = await repo.ObtenerAsync(categoria, id);
        return r is null ? Results.NotFound() : Ok(ctx, r);
    })
    .WithSummary("Lectura puntual por clave de partición + id.");

api.MapPost("/lugares", async (Lugar lugar, LugaresRepository repo, HttpContext ctx) =>
    {
        var error = Validar(lugar);
        if (error is not null) return Results.BadRequest(new { error });

        var r = await repo.CrearAsync(lugar);
        if (r is null) return Results.Conflict(new { error = $"Ya existe un lugar con id '{lugar.Id}' en la categoría '{lugar.Categoria}'." });

        Ru(ctx, r.Ru);
        return Results.Created($"/api/lugares/{r.Datos.Categoria}/{r.Datos.Id}", r.Datos);
    })
    .WithSummary("Crea un lugar. Si no se envía id, se genera uno.");

api.MapPut("/lugares/{categoria}/{id}", async (string categoria, string id, Lugar lugar, LugaresRepository repo, HttpContext ctx) =>
    {
        if (string.IsNullOrWhiteSpace(lugar.Categoria)) lugar.Categoria = categoria;
        var error = Validar(lugar);
        if (error is not null) return Results.BadRequest(new { error });

        // La clave de partición de un documento no se puede cambiar: hay que borrarlo y crearlo de nuevo.
        if (LugaresRepository.Normalizar(lugar.Categoria) != LugaresRepository.Normalizar(categoria))
            return Results.BadRequest(new { error = "La categoría es la clave de partición y no se puede cambiar. Elimine el lugar y créelo en la nueva categoría." });

        var r = await repo.ReemplazarAsync(id, lugar);
        return r is null ? Results.NotFound() : Ok(ctx, r);
    })
    .WithSummary("Reemplaza un lugar (la categoría no puede cambiar).");

api.MapDelete("/lugares/{categoria}/{id}", async (string categoria, string id, LugaresRepository repo, HttpContext ctx) =>
    {
        var ru = await repo.EliminarAsync(categoria, id);
        if (ru is null) return Results.NotFound();
        Ru(ctx, ru.Value);
        return Results.NoContent();
    })
    .WithSummary("Elimina un lugar.");

app.Run();

// ---------- funciones auxiliares ----------
static IResult Ok<T>(HttpContext ctx, Resultado<T> r)
{
    Ru(ctx, r.Ru);
    return Results.Ok(r.Datos);
}

static void Ru(HttpContext ctx, double ru) =>
    ctx.Response.Headers["x-ru-charge"] = ru.ToString("0.##", CultureInfo.InvariantCulture);

static string? Validar(Lugar l)
{
    if (string.IsNullOrWhiteSpace(l.Nombre)) return "El nombre es obligatorio.";
    if (l.Nombre.Length > 120) return "El nombre no puede tener más de 120 caracteres.";
    if (string.IsNullOrWhiteSpace(l.Categoria)) return "La categoría es obligatoria (es la clave de partición).";
    if (l.Categoria.Length > 40) return "La categoría no puede tener más de 40 caracteres.";
    return null;
}
