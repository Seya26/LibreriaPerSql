namespace LibreriaPerSql.Configurations
{
    /// <summary>
    /// Configurazione del database. Supporta sia SQL Server che MongoDB.
    /// 
    /// Esempio appsettings.json per SQL Server:
    /// "DbConfig": {
    ///   "ProviderSQL": "sqlserver",
    ///   "ConnectionString": "Server=...;Database=...;Trusted_Connection=True;"
    /// }
    /// 
    /// Esempio appsettings.json per MongoDB:
    /// "DbConfig": {
    ///   "ProviderSQL": "mongodb",
    ///   "ConnectionString": "mongodb+srv://user:pass@cluster.mongodb.net/",
    ///   "DatabaseName": "pcto"
    /// }
    /// </summary>
    public class DbConfig
    {
        public const string SectionName = "DbConfig";

        /// <summary>
        /// Provider del database. Valori supportati: "sqlserver" | "mongodb"
        /// </summary>
        public string ProviderSQL { get; set; } = "sqlserver";

        /// <summary>
        /// Connection string del database (valida per entrambi i provider)
        /// </summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>
        /// Nome del database. Obbligatorio solo per MongoDB.
        /// Per SQL Server è già incluso nella ConnectionString.
        /// </summary>
        public string? DatabaseName { get; set; }
    }
}
