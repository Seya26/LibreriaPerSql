using Dapper;
using LibreriaPerSql.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json;

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

        public async Task<string> GetSchemaJsonAsync()
        {
            var schemaQuery = GetScriptGetSchema();
            using var connection = CreateConnection();
            IEnumerable<dynamic> rawSchema;
            try
            {
                rawSchema = await connection.QueryAsync(schemaQuery);
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
                    Description = (string?)g.First().TableDescription,
                    Columns = g.Select(c => new
                    {
                        Name = c.ColumnName,
                        Type = FormatDataType((string)c.DataType, (int) (c.MaxLength ?? 0),(int) (c.Precision ?? 0),(int) (c.Scale ?? 0)),
                        Description = (string?)c.ColumnDescription
                    }).ToList()
                });

            // Serializzazione che rispetta i null (se Description manca, il campo viene omesso o messo a null)
            return JsonSerializer.Serialize(schemaStructured, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }

        private string GetScriptGetSchema()
        {
            var assembly = Assembly.GetExecutingAssembly();

            // Cerca la risorsa che finisce con il nome file (ignora il namespace)
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(str => str.EndsWith(_EmbeddedSqlName));

            if (resourceName == null)
                throw new FileNotFoundException($"Impossibile trovare la risorsa incorporata: '{_EmbeddedSqlName}'.");

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if(stream is null)
            {
                throw new InvalidOperationException($"Trovato il nome '{resourceName}', ma lo stream è null. Qualcosa non va nel caricamento della risorsa.");
            }
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }

        // Metodo helper per formattare il tipo SQL (VARCHAR(50), DECIMAL(10,2), ecc)
        private string FormatDataType(string type, int length, int precision, int scale)
        {
            if (string.IsNullOrEmpty(type)) return "UNKNOWN";
            type = type.ToUpper();
            if (type == "VARCHAR" || type == "NVARCHAR" || type == "CHAR" || type == "NCHAR")
            {
                // Se è -1 è MAX, altrimenti è la lunghezza. Per nvarchar la lunghezza è doppia in byte, quindi /2 se necessario
                var lenStr = length == -1 ? "MAX" : (type.StartsWith('N') ? length / 2 : length).ToString();
                return $"{type}({lenStr})";
            }
            if (type == "DECIMAL" || type == "NUMERIC")
            {
                return $"{type}({precision},{scale})";
            }
            return type;
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