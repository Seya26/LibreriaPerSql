using Dapper;
using LibreriaPerSql.Configurations;
using LibreriaPerSql.DTO;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibreriaPerSql.Services
{
    public class DbService : IDbService
    {
        private readonly DbConfig _config;
        private const string _EmbeddedSqlName = "ScriptGetDatabaseSchema.sql";
        public DbService(IOptions<DbConfig> config)
        {
            _config = config.Value;
        }

        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_config.ConnectionString);
        }

        public async Task<string> GetSchemaJsonAsync(IEnumerable<string>? tablesToInclude)
        {
            var schemaQuery = GetScriptGetSchema();

            string queryFilter = "";  
            if(tablesToInclude is not null && tablesToInclude.Any())
            {
                queryFilter += "\nWHERE (QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)) IN @Tables";
            }
            queryFilter += "\nORDER BY t.name, c.column_id;";
            //Aggiungo il filtro per le tabelle
            schemaQuery += queryFilter;

            using var connection = CreateConnection();
            IEnumerable<RawSchemaDTO> rawSchema;
            try
            {
                rawSchema = await connection.QueryAsync<RawSchemaDTO>(schemaQuery, new {Tables = tablesToInclude});
            }catch(SqlException ex)
            {
                throw new Exception($"Errore durante la lettura dello schema DB: {ex.Message}", ex);
            }
            // Trasformazione in memoria per creare la gerarchia Tabella -> Colonne
            var schemaStructured = rawSchema
                .GroupBy(r => new { r.SchemaName, r.TableName })
                .Select(g => new
                {
                    TableName = $"[{g.Key.SchemaName}].[{g.Key.TableName}]",
                    Description = g.First().TableDescription,
                    Columns = g.Select(c => new
                    {
                        Name = c.ColumnName,
                        Type = c.FullDataType,
                        Description = c.ColumnDescription
                    }).ToList()
                });

            // Serializzazione che rispetta i null (se Description manca, il campo viene omesso o messo a null)
            return JsonSerializer.Serialize(schemaStructured, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        private string GetScriptGetSchema()
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Cerca la risorsa che finisce con il nome file (ignora il namespace)
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(str => str.EndsWith(_EmbeddedSqlName));

            if (resourceName is null)
                throw new FileNotFoundException($"Impossibile trovare la risorsa incorporata: '{_EmbeddedSqlName}'.");

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if(stream is null)
            {
                throw new InvalidOperationException($"Trovato il nome '{resourceName}', ma lo stream è null. Qualcosa non va nel caricamento della risorsa.");
            }
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        public async Task<IEnumerable<T>> ExecuteQueryAsync<T>(string sql, object? parameters = null)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(sql);
            using var connection = CreateConnection();
            try
            {
                return await connection.QueryAsync<T>(sql, parameters);
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
        }
    }
}