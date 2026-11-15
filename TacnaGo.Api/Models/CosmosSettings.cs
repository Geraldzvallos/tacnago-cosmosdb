namespace TacnaGo.Api.Models;

/// <summary>Sección "Cosmos" de la configuración (en Azure: variables Cosmos__Endpoint, Cosmos__Key, ...).</summary>
public class CosmosSettings
{
    public string Endpoint { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Database { get; set; } = "TacnaGo";
    public string Container { get; set; } = "Lugares";
}
