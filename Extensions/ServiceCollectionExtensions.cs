using LibreriaPerSql.Configurations;
using LibreriaPerSql.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LibreriaPerSql.Extensions
{
    public static class ServiceCollectionExtensions
    {
        public static IServiceCollection AddDb(this IServiceCollection services, IConfiguration configuration)
        {
            var dbSection = configuration.GetSection(DbConfig.SectionName);
            services.Configure<DbConfig>(dbSection);
            var dbConfig = dbSection.Get<DbConfig>();

            if (dbConfig == null) throw new InvalidOperationException("La sezione delle configurazione del DB (DbConfig) risulta mancante.");
            ArgumentNullException.ThrowIfNullOrEmpty(dbConfig.ProviderSQL);
            ArgumentNullException.ThrowIfNullOrEmpty(dbConfig.ConnectionString);

            //switch (dbConfig.ProviderSQL.ToLowerInvariant())
            //{
            //    case "sqlserver":
            //        services.AddScoped<ISqlExecutor, SqlServerExecutor>();
            //        break;
            //    default:
            //        throw new NotSupportedException($"The provider '{dbConfig.ProviderSQL}' is not supported.");
            //}
            services.AddMemoryCache();
            services.AddScoped<IDbService, DbService>();
            return services;
        }
    }
}
