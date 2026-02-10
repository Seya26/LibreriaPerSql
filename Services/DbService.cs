using Dapper;
using LibreriaPerSql.Configurations;
using LibreriaPerSql.DTO;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibreriaPerSql.Services
{
    public class DbService : IDbService
    {
        private readonly DbConfig _config;
        private readonly ILogger<DbService> _logger;

        // Variabile che tramite lambda function estrae lo script sql incorporato per estrapolare lo schema di un db
        // Lazy<T> permette di caricare la risorsa solo alla prima chiamata quando è realmente necessaria, senno restituisce (le chiamate successive) il valore memorizzato (caching)
        // Static perchè lo script è lo stesso per tutte le istanze di DbService (non cambia mai)
        private static readonly Lazy<string> _scriptSchemaSql = new Lazy<string>(() =>
        {
            const string embeddedSqlName = "ScriptGetDatabaseSchema.sql";
            // Determina l'assembly che contiene la risorsa incorporata
            var assembly = Assembly.GetExecutingAssembly();

            // Prende tutte le risorse incorporate e cerca quella che finisce con il nome del file (ignora il namespace)
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(str => str.EndsWith(embeddedSqlName));
            if (resourceName == null) throw new FileNotFoundException($"File embedded {embeddedSqlName} non trovato");

            // Apre lo stream della risorsa incorporata 
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) throw new InvalidOperationException($"Risorsa trovata '{resourceName}', ma lo stream è nullo.");

            // Reader per leggere e ritornare il contenuto della risorsa come stringa 
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        });

        public DbService(IOptions<DbConfig> config, ILogger<DbService> logger)
        {
            _config = config.Value;
            _logger = logger;
        }

        private SqlConnection CreateConnection()
        {
            return new SqlConnection(_config.ConnectionString);
        }

        public async Task<IEnumerable<T>> ExecuteQueryAsync<T>(string sql, object? parameters = null)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(sql);
            using var connection = CreateConnection();
            try
            {
                return await connection.QueryAsync<T>(sql, parameters);
            }
            catch (SqlException ex)
            {
                _logger.LogError(ex, $"Errore in fase di esecuzione query: {ex.Message}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Errore in fase di esecuzione query: {ex.Message}");
                throw;
            }
        }

        public async Task<int> ExecuteCommandAsync(string sql, object? parameters = null, IDbTransaction? transaction = null)
        {
            ArgumentNullException.ThrowIfNullOrEmpty(sql);

            if (transaction != null)
            {
                try
                {
                    return await transaction.Connection!.ExecuteAsync(sql, parameters, transaction);
                }
                catch (SqlException ex)
                {
                    _logger.LogError(ex, $"Errore in fase di esecuzione del comando sql (transazione): {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    throw new Exception(ex.Message, ex);
                }
            }

            using var connection = CreateConnection();
            try
            {
                return await connection.ExecuteAsync(sql, parameters);
            }
            catch (SqlException ex)
            {
                throw new Exception($"Errore nell'esecuzione del comando sql: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception(ex.Message, ex);
            }
        }

        public async Task<IEnumerable<TableEmbeddingDTO>> GetAllTableEmbeddingsAsync()
        {
            string sql = "SELECT TableName, Description, JsonSchema, VectorData FROM [dbo].[AI_SchemaCache]";
            return await ExecuteQueryAsync<TableEmbeddingDTO>(sql);
        }

        public async Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList)
        {
            var schemaQuery = _scriptSchemaSql.Value;

            if (blackList != null && blackList.Any())
            {
                schemaQuery += "\nWHERE (QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)) NOT IN @Tables";
            }
            schemaQuery += "\nORDER BY t.name, c.column_id;";

            IEnumerable<RawSchemaDTO> rawSchema;
            try
            {
                rawSchema = await ExecuteQueryAsync<RawSchemaDTO>(schemaQuery, new { Tables = blackList });
            }
            catch (SqlException ex)
            {
                throw new Exception($"Errore durante la creazione dello schema: {ex.Message}", ex);
            }

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

            return JsonSerializer.Serialize(schemaStructured, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }

        public async Task UpsertTableEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> tables)
        {
            if (tables == null || !tables.Any()) return;

            using var connection = CreateConnection();
            await connection.OpenAsync();

            using var transaction = connection.BeginTransaction();

            try
            {
                string createTableSql = @"
                    IF OBJECT_ID('[dbo].[AI_SchemaCache]', 'U') IS NULL
                    BEGIN
                        CREATE TABLE [dbo].[AI_SchemaCache] (
                            [TableName] NVARCHAR(255) PRIMARY KEY,
                            [Description] NVARCHAR(MAX),
                            [JsonSchema] NVARCHAR(MAX),       -- Cache del JSON per il prompt
                            [VectorData] VARBINARY(MAX),
                            [SchemaHash] NVARCHAR(64),        -- Hash per verificare cambiamenti
                            [LastUpdated] DATETIME DEFAULT GETDATE()
                        );
                    END";

                await ExecuteCommandAsync(createTableSql, transaction: transaction);

                string mergeSql = @"
                    MERGE INTO [dbo].[AI_SchemaCache] AS target
                    USING (VALUES (@TableName, @Description, @JsonSchema, @VectorData, @SchemaHash)) 
                           AS source (TableName, Description, JsonSchema, VectorData, SchemaHash)
                    ON target.TableName = source.TableName
                    
                    WHEN MATCHED AND (target.SchemaHash <> source.SchemaHash OR target.SchemaHash IS NULL) THEN
                        UPDATE SET 
                            Description = source.Description,
                            JsonSchema = source.JsonSchema,
                            VectorData = source.VectorData,
                            SchemaHash = source.SchemaHash,
                            LastUpdated = GETDATE()

                    WHEN NOT MATCHED THEN
                        INSERT (TableName, Description, JsonSchema, VectorData, SchemaHash, LastUpdated)
                        VALUES (source.TableName, source.Description, source.JsonSchema, source.VectorData, source.SchemaHash, GETDATE());";

                var dataToUpsert = tables.Select(t => new
                {
                    t.TableName,
                    t.Description,
                    t.JsonSchema,
                    t.VectorData,
                    SchemaHash = ComputeHash(t.Description + t.JsonSchema) // Hash combinato per sicurezza
                });

                await ExecuteCommandAsync(mergeSql, dataToUpsert, transaction: transaction);

                if (dataToUpsert.Any())
                {
                    var activeTableNames = dataToUpsert.Select(t => t.TableName).ToList();

                    string deleteSql = "DELETE FROM [dbo].[AI_SchemaCache] WHERE TableName NOT IN @ActiveTables";

                    await ExecuteCommandAsync(deleteSql, new { ActiveTables = activeTableNames }, transaction: transaction);
                }
                else
                {
                    await ExecuteCommandAsync("TRUNCATE TABLE [dbo].[AI_SchemaCache]", transaction: transaction);
                }

                await transaction.CommitAsync();
            }
            catch (Exception ex)
            {
                // In caso di errore, annulla tutte le operazioni
                try
                {
                    await transaction.RollbackAsync();
                }
                catch { }
                _logger.LogError(ex, $"Errore durante l'upsert delle embedding: {ex.Message}");
                throw;
            }
        }

        private string ComputeHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = SHA256.HashData(inputBytes);

            return Convert.ToHexString(hashBytes);
        }
    }
}