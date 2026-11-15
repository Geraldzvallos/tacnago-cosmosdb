using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using TacnaGo.Api.Models;

namespace TacnaGo.Api.Services;

/// <summary>Lista los respaldos lógicos de Cosmos DB que el workflow backup.yml sube a Blob Storage.</summary>
public class RespaldosService
{
    private readonly BlobContainerClient? _container;

    public RespaldosService(IOptions<BackupSettings> options)
    {
        var s = options.Value;
        if (!string.IsNullOrWhiteSpace(s.ConnectionString))
            _container = new BlobContainerClient(s.ConnectionString, s.Container);
    }

    public bool Configurado => _container is not null;

    public async Task<List<Respaldo>> ListarAsync(int maximo = 30)
    {
        var lista = new List<Respaldo>();
        if (_container is null) return lista;

        await foreach (var blob in _container.GetBlobsAsync(BlobTraits.Metadata, BlobStates.None, prefix: "tacnago/"))
        {
            int? docs = blob.Metadata.TryGetValue("documentos", out var d) && int.TryParse(d, out var n) ? n : null;
            lista.Add(new Respaldo(
                blob.Name,
                blob.Properties.LastModified,
                Math.Round((blob.Properties.ContentLength ?? 0) / 1024.0, 1),
                blob.Properties.AccessTier?.ToString(),
                docs));
        }
        return lista.OrderByDescending(r => r.Fecha).Take(maximo).ToList();
    }
}
