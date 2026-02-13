using LibreriaPerSql.DTO;
using System.Data;

namespace LibreriaPerSql.Services
{
    public interface IDbService
    {
        /// <summary>
        /// Metodo per eseguire query SQL asincrone (SELECT) e restituire i risultati come collezione di oggetti del tipo specificato.
        /// </summary>
        /// <param name="sql">la query T-SQL da eseguire (<see cref="string"/>)</param>
        /// <param name="parameters"> eventuali parametri da passare alla query (<see cref="object"/>)</param>
        /// <returns>una collezione di oggetti del tipo specificato (<see cref="IEnumerable{T}"/>)</returns>
        Task<IEnumerable<T>> ExecuteQueryAsync<T>(string sql, object? parameters = null, CancellationToken ct = default);

        /// <summary>
        /// Metodo per eseguire comandi sql asincroni (INSERT, UPDATE, DELETE).
        /// </summary>
        /// <param name="sql">la query T-SQL da eseguire (<see cref="string"/>)</param>
        /// <param name="parameters"> eventuali parametri da passare alla query (<see cref="object"/>)</param>
        /// <param name="transaction">eventuale transazione da utilizzare per l'esecuzione del comando (<see cref="IDbTransaction"/>)</param>
        /// <returns>il numero di righe interessate dall'operazione (<see cref="int"/>)</returns>
        Task<int> ExecuteCommandAsync(string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken ct = default);

        /// <summary>
        /// Metodo per ottenere lo schema del database in formato JSON.
        /// </summary>
        /// <param name="blackList">Lista opzionale di tabelle da includere nello schema (<see cref="IEnumerable{string}"/>).</param>
        /// <returns>Lo schema del database in formato JSON. (<see cref="string"/>)</returns>
        Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList, CancellationToken ct = default);

        /// <summary>
        /// Metodo per inserire o aggiornare le rappresentazioni vettoriali delle tabelle nel database.
        /// </summary>
        /// <param name="tables">Collezione di oggetti TableEmbeddingDTO contenenti le informazioni sulle tabelle e i loro embedding (<see cref="IEnumerable{TableEmbeddingDTO}"/>).</param>
        /// <param name="ct">Token di cancellazione per l'operazione asincrona.</param>
        Task UpsertTableEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> tables, CancellationToken ct = default);

        /// <summary>
        /// Metodo per recuperare tutte le rappresentazioni vettoriali delle tabelle dal database.
        /// </summary>
        /// <returns> Collezione di oggetti TableEmbeddingDTO contenenti le informazioni sulle tabelle e i loro embedding (<see cref="IEnumerable{TableEmbeddingDTO}"/>). </returns>
        Task<IEnumerable<TableEmbeddingDTO>> GetAllTableEmbeddingsAsync();

        /// <summary>
        /// Metodo utile per il caching del hash del database, per evitare di eseguire operazioni di embedding quando non è necessario.
        /// </summary>
        /// <returns>Numero di righe presenti nel db</returns>
        Task<int> CountTableEmbeddingsAsync();
    }
}
