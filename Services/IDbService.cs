using LibreriaPerSql.DTO;
using System.Data;

namespace LibreriaPerSql.Services
{
    public interface IDbService
    {
        /*
         * Metodo per eseguire query sql asincrone
         * @param sql: la query T-SQL da eseguire (string)
         * @param parameters: eventuali parametri da passare alla query (object)
         * @return: una collezione di oggetti del tipo specificato (IEnumerable<T>)
         */
        Task<IEnumerable<T>> ExecuteQueryAsync<T>(string sql, object? parameters = null);


        /*
         * Metodo per eseguire comandi sql asincroni (INSERT, UPDATE, DELETE)
         * @param sql: il comando T-SQL da eseguire (string)
         * @param parameters: eventuali parametri da passare al comando (object)
         * @return: il numero di righe interessate dall'operazione (int)
         */
        Task<int> ExecuteCommandAsync(string sql, object? parameters = null, IDbTransaction? transaction = null);

        /*
         * Metodo per ottenere lo schema del database in formato JSON
         * @param tablesToInclude: elenco opzionale di tabelle da includere nello schema (IEnumerable<string>)
         * @return: lo schema del database in formato JSON (string)
         */
        Task<string> GetSchemaJsonAsync(IEnumerable<string>? tablesToInclude);

        /*
         * Metodo per inserire o aggiornare le rappresentazioni vettoriali delle tabelle nel database
         * @param tables: collezione di oggetti TableEmbeddingDTO contenenti le informazioni sulle tabelle e i loro embedding (IEnumerable<TableEmbeddingDTO>)
         */
        Task UpsertTableEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> tables);

        /*
         * Metodo per recuperare tutte le rappresentazioni vettoriali delle tabelle dal database
         * @return: collezione di oggetti TableEmbeddingDTO contenenti le informazioni sulle tabelle e i loro embedding (IEnumerable<TableEmbeddingDTO>)
         */
        Task<IEnumerable<TableEmbeddingDTO>> GetAllTableEmbeddingsAsync();

    }
}
