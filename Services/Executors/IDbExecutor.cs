using LibreriaPerSql.DTO;

namespace LibreriaPerSql.Executors
{
    /// <summary>
    /// Interfaccia Strategy per l'esecuzione delle operazioni sul database.
    /// 
    /// RESPONSABILITÀ DIVISE IN DUE SEZIONI:
    /// 
    /// 1. LETTURA (esposta verso l'esterno tramite IDbService)
    ///    Esegue query di sola lettura generate dall'AI a partire da richieste utente.
    ///    L'utente può SOLO visualizzare e analizzare dati, mai modificarli.
    /// 
    /// 2. CACHE INTERNA (usata solo internamente dalla libreria)
    ///    Gestisce la collezione/tabella AI_SchemaCache per gli embedding.
    ///    Non è esposta su IDbService — è un dettaglio implementativo della libreria.
    /// 
    /// Implementazioni:
    ///   - SqlServerExecutor → Dapper + T-SQL
    ///   - MongoDbExecutor   → MongoDB.Driver + MqlQueryDTO
    /// </summary>
    public interface IDbExecutor
    {
        // =====================================================================
        // SEZIONE 1 — LETTURA (esposta su IDbService)
        // =====================================================================

        /// <summary>
        /// Esegue una query di SOLA LETTURA e restituisce i risultati come dynamic.
        /// I risultati sono compatibili con Syncfusion Grid e Charts.
        /// 
        /// Il parametro "query" dipende dal provider:
        ///   - SQL Server → stringa T-SQL    (es. "SELECT * FROM Studenti")
        ///   - MongoDB    → MqlQueryDTO      (es. { Collection="Studenti", Operation="find" })
        /// 
        /// NOTA: il tipo object è un tradeoff consapevole per supportare entrambi i provider
        /// con una sola interfaccia. La validazione del tipo avviene dentro ogni executor.
        /// </summary>
        Task<IEnumerable<dynamic>> ExecuteQueryAsync(object query, object? parameters = null, CancellationToken ct = default);

        /// <summary>
        /// Estrae lo schema del database come JSON strutturato da mandare all'AI come contesto.
        /// 
        ///   - SQL Server → interroga sys.tables / sys.columns (schema dichiarativo e preciso)
        ///   - MongoDB    → campiona documenti con $sample per inferire i campi
        ///                  (schema-less: l'inferenza è best-effort, non garantita al 100%)
        /// </summary>
        Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList = null, CancellationToken ct = default);

        // =====================================================================
        // SEZIONE 2 — CACHE INTERNA (NON esposta su IDbService)
        // =====================================================================

        /// <summary>
        /// [USO INTERNO] Salva o aggiorna gli embedding nella cache del database.
        /// Chiamato dalla LibreriaAI, non dall'utente finale.
        /// 
        ///   - SQL Server → MERGE su [dbo].[AI_SchemaCache]
        ///   - MongoDB    → ReplaceOne con upsert su collezione AI_SchemaCache
        /// </summary>
        Task UpsertEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> embeddings, CancellationToken ct = default);

        /// <summary>
        /// [USO INTERNO] Recupera tutti gli embedding dalla cache.
        /// </summary>
        Task<IEnumerable<TableEmbeddingDTO>> GetAllEmbeddingsAsync(CancellationToken ct = default);

        /// <summary>
        /// [USO INTERNO] Conta gli embedding in cache.
        /// Usato per decidere se ricalcolare gli embedding o usare quelli in cache.
        /// </summary>
        Task<int> CountEmbeddingsAsync(CancellationToken ct = default);
    }
}
