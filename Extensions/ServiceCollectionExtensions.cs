using LibreriaPerSql.Configurations;
using LibreriaPerSql.Executors;
using LibreriaPerSql.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LibreriaPerSql.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Registra IDbService nel container DI, scegliendo automaticamente
        /// l'executor corretto in base a DbConfig.ProviderSQL.
        /// 
        /// Configurazione in appsettings.json:
        /// 
        /// Per SQL Server:
        /// "DbConfig": {
        ///   "ProviderSQL": "sqlserver",
        ///   "ConnectionString": "Server=...;Database=...;Trusted_Connection=True;"
        /// }
        /// 
        /// Per MongoDB:
        /// "DbConfig": {
        ///   "ProviderSQL": "mongodb",
        ///   "ConnectionString": "mongodb+srv://user:pass@cluster.mongodb.net/",
        ///   "DatabaseName": "pcto"
        /// }
        /// </summary>
        public static IServiceCollection AddDb(this IServiceCollection services, IConfiguration configuration)
        {
            var dbSection = configuration.GetSection(DbConfig.SectionName);
            services.Configure<DbConfig>(dbSection);

            var dbConfig = dbSection.Get<DbConfig>()
                ?? throw new InvalidOperationException($"Sezione '{DbConfig.SectionName}' mancante in appsettings.json.");

            ArgumentNullException.ThrowIfNullOrEmpty(dbConfig.ProviderSQL);
            ArgumentNullException.ThrowIfNullOrEmpty(dbConfig.ConnectionString);

            // Switch sul provider: inietta l'executor corretto
            // Per aggiungere un nuovo provider (es. PostgreSQL) basta aggiungere un case qui
            switch (dbConfig.ProviderSQL.ToLowerInvariant())
            {
                case "sqlserver":
                    services.AddScoped<IDbExecutor, SqlServerExecutor>();
                    break;

                case "mongodb":
                    ArgumentNullException.ThrowIfNullOrEmpty(dbConfig.DatabaseName,
                        $"DatabaseName è obbligatorio per il provider 'mongodb'.");
                    services.AddScoped<IDbExecutor, MongoDbExecutor>();
                    break;

                default:
                    throw new NotSupportedException(
                        $"Provider '{dbConfig.ProviderSQL}' non supportato. " +
                        $"Valori validi: 'sqlserver', 'mongodb'.");
            }

            services.AddMemoryCache();
            services.AddScoped<IDbService, DbService>();
            return services;
        }
    }
}
