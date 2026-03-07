using LibreriaPerSql.DTO;

namespace LibreriaPerSql.Services
{
    /// <summary>
    /// Interfaccia pubblica per l'accesso al database in SOLA LETTURA.
    /// 
    /// L'utente può SOLO visualizzare e analizzare dati.
    /// Le operazioni di scrittura (INSERT/UPDATE/DELETE) non sono esposte:
    /// l'unica scrittura che avviene è quella interna al caching degli embedding,
    /// gestita direttamente dagli executor senza passare da questa interfaccia.
    /// 
    /// Funziona sia con SQL Server che con MongoDB — il provider è trasparente
    /// al chiamante e si configura tramite DbConfig.ProviderSQL in appsettings.json.
    /// </summary>
    public interface IDbService
    {
        /// <summary>
        /// Esegue una query di SOLA LETTURA generata dall'AI e restituisce
        /// i risultati come lista di oggetti dynamic, compatibili con
        /// Syncfusion Grid e Charts.
        /// 
        /// Il parametro "query" dipende dal provider configurato:
        ///   - SQL Server → stringa T-SQL
        ///                  es: "SELECT nome, cognome FROM Studenti WHERE anno = 2024"
        ///   - MongoDB    → MqlQueryDTO
        ///                  es: new MqlQueryDTO { Collection="Studenti", Operation="find",
        ///                                        Filter = new { anno = 2024 } }
        /// </summary>
        Task<IEnumerable<dynamic>> ExecuteQueryAsync(object query, object? parameters = null, CancellationToken ct = default);

        /// <summary>
        /// Estrae lo schema del database come JSON strutturato.
        /// Questo JSON viene mandato all'AI come contesto per generare le query.
        /// 
        ///   - SQL Server → schema preciso da sys.tables / sys.columns
        ///   - MongoDB    → schema inferito campionando i documenti esistenti
        /// </summary>
        Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList = null, CancellationToken ct = default);

        /// <summary>
        /// [USO INTERNO — LibreriaAI]
        /// Salva o aggiorna gli embedding nella cache del database.
        /// Non rappresenta una modifica ai dati utente.
        /// </summary>
        Task UpsertEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> embeddings, CancellationToken ct = default);

        /// <summary>
        /// [USO INTERNO — LibreriaAI]
        /// Recupera tutti gli embedding dalla cache.
        /// </summary>
        Task<IEnumerable<TableEmbeddingDTO>> GetAllEmbeddingsAsync(CancellationToken ct = default);

        /// <summary>
        /// [USO INTERNO — LibreriaAI]
        /// Conta gli embedding in cache. Usato per decidere se ricalcolarli.
        /// </summary>
        Task<int> CountEmbeddingsAsync(CancellationToken ct = default);
    }
}
