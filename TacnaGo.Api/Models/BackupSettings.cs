namespace TacnaGo.Api.Models;

/// <summary>Sección "Backup" (en Azure: Backup__ConnectionString y Backup__Container, definidas por Terraform).</summary>
public class BackupSettings
{
    public string ConnectionString { get; set; } = string.Empty;
    public string Container { get; set; } = "cosmos-backups";
}

/// <summary>Un respaldo lógico guardado en Azure Blob Storage.</summary>
public record Respaldo(string Nombre, DateTimeOffset? Fecha, double TamanoKB, string? Nivel, int? Documentos);
