using LibreriaPerSql.DTO;
using LibreriaPerSql.Executors;
using Microsoft.Extensions.Logging;

namespace LibreriaPerSql.Services
{
    /// <summary>
    /// Orchestratore leggero che delega tutto all'IDbExecutor iniettato.
    /// Non contiene logica SQL né MongoDB — è completamente provider-agnostic.
    /// 
    /// Aggiungere un nuovo provider significa solo creare un nuovo IDbExecutor:
    /// questo service non cambia mai.
    /// </summary>
    public class DbService : IDbService
    {
        private readonly IDbExecutor _executor;
        private readonly ILogger<DbService> _logger;

        public DbService(IDbExecutor executor, ILogger<DbService> logger)
        {
            _executor = executor;
            _logger = logger;
        }

        public Task<IEnumerable<dynamic>> ExecuteQueryAsync(object query, object? parameters = null, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(query);
            return _executor.ExecuteQueryAsync(query, parameters, ct);
        }

        public Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList = null, CancellationToken ct = default)
            => _executor.GetSchemaJsonAsync(blackList, ct);

        public Task UpsertEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> embeddings, CancellationToken ct = default)
            => _executor.UpsertEmbeddingsAsync(embeddings, ct);

        public Task<IEnumerable<TableEmbeddingDTO>> GetAllEmbeddingsAsync(CancellationToken ct = default)
            => _executor.GetAllEmbeddingsAsync(ct);

        public Task<int> CountEmbeddingsAsync(CancellationToken ct = default)
            => _executor.CountEmbeddingsAsync(ct);
    }
}
