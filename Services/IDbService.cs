using LibreriaPerSql.DTO;
using System.Data;

namespace LibreriaPerSql.Services
{
    public interface IDbService
    {
        Task<IEnumerable<T>> ExecuteQueryAsync<T>(string sql, object? parameters = null);

        Task<int> ExecuteCommandAsync(string sql, object? parameters = null, IDbTransaction? transaction = null);

        Task<string> GetSchemaJsonAsync(IEnumerable<string>? tablesToInclude);
        Task UpsertTableEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> tables);

    }
}
