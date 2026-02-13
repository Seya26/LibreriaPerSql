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

        // Lazy loading dello script SQL embedded.
        private static readonly Lazy<string> _scriptSchemaSql = new(() => LoadEmbeddedSql("LibreriaPerSql.Resources.ScriptSQL.ScriptGetDatabaseSchema.sql"));

        public DbService(IOptions<DbConfig> config, ILogger<DbService> logger)
        {
            _config = config.Value;
            _logger = logger;
        }

        /// <summary>
        /// Carica una risorsa embedded (script SQL) in modo statico e thread-safe.
        /// </summary>
        private static string LoadEmbeddedSql(string fileName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(str => str.EndsWith(fileName));

            if (resourceName == null)
                throw new FileNotFoundException($"Risorsa embedded '{fileName}' non trovata.");

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Risorsa '{resourceName}' trovata ma lo stream è nullo.");

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private SqlConnection CreateConnection() => new SqlConnection(_config.ConnectionString);

            public async Task<IEnumerable<T>> ExecuteQueryAsync<T>(string sql, object? parameters = null, CancellationToken ct = default)
            {
                ArgumentException.ThrowIfNullOrEmpty(sql);

                using var connection = CreateConnection();
                var command = new CommandDefinition(sql, parameters, cancellationToken: ct);

                try
                {
                    return (await connection.QueryAsync<T>(command: command)).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore esecuzione query. SQL: {Sql}", sql);
                    throw; 
                }
            }

        public async Task<int> ExecuteCommandAsync(string sql, object? parameters = null, IDbTransaction? transaction = null, CancellationToken ct = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(sql);

            var command = new CommandDefinition(sql, parameters, transaction: transaction, cancellationToken: ct);

            try
            {
                if (transaction != null)
                {
                    return await transaction.Connection!.ExecuteAsync(command: command);
                }

                using var connection = CreateConnection();
                return await connection.ExecuteAsync(command: command);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Errore esecuzione comando. SQL: {sql}");
                throw;
            }
        }

        public async Task<IEnumerable<TableEmbeddingDTO>> GetAllTableEmbeddingsAsync()
        {
            const string sql = "SELECT TableName, Description, JsonSchema, VectorData FROM [dbo].[AI_SchemaCache]";
            return await ExecuteQueryAsync<TableEmbeddingDTO>(sql, ct: CancellationToken.None);
        }

        public async Task<int> CountTableEmbeddingsAsync()
        {
            const string sql = "SELECT COUNT(*) FROM [dbo].[AI_SchemaCache]";
            return await ExecuteCommandAsync(sql, ct: CancellationToken.None);
        }

        public async Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList, CancellationToken ct = default)
        {
            var schemaQuery = new StringBuilder(_scriptSchemaSql.Value);
            var parameters = new DynamicParameters();

            if (blackList != null && blackList.Any())
            {
                schemaQuery.AppendLine("\nWHERE (QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)) NOT IN @Tables");
                parameters.Add("Tables", blackList);
            }

            schemaQuery.AppendLine("\nORDER BY t.name, c.column_id;");

            IEnumerable<RawSchemaDTO> rawSchema;
            try
            {
                rawSchema = await ExecuteQueryAsync<RawSchemaDTO>(schemaQuery.ToString(), parameters, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante il recupero dello schema raw.");
                throw;
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

        public async Task UpsertTableEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> tables, CancellationToken ct = default)
        {
            if (tables == null || !tables.Any()) return;

            using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();

            try
            {
                await EnsureCacheTableExistsAsync(connection, transaction, ct);

                var dataToUpsert = tables.Select(t => new
                {
                    t.TableName,
                    t.Description,
                    t.JsonSchema,
                    t.VectorData,
                    SchemaHash = ComputeHash(t.Description + t.JsonSchema)
                }).ToList();

                const string mergeSql = @"
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

                var cmdMerge = new CommandDefinition(mergeSql, dataToUpsert, transaction, cancellationToken: ct);
                await connection.ExecuteAsync(cmdMerge);

                var activeTableNames = dataToUpsert.Select(t => t.TableName).ToList();
                const string deleteSql = "DELETE FROM [dbo].[AI_SchemaCache] WHERE TableName NOT IN @ActiveTables";

                var cmdDelete = new CommandDefinition(deleteSql, new { ActiveTables = activeTableNames }, transaction, cancellationToken: ct);
                await connection.ExecuteAsync(cmdDelete);

                await transaction.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore durante l'upsert delle embedding (Rollback in corso).");
                try { await transaction.RollbackAsync(ct); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Metodo per assicurarsi che la tabella di cache esista prima di tentare qualsiasi operazione di upsert. 
        /// </summary>
        /// <param name="connection">Connessione al database (<see cref="IDbConnection"/>) </param>
        /// <param name="transaction">Comando per la transazione sicura di prova (<see cref="IDbTransaction"/>)</param>
        /// <param name="ct">Cancellation Token</param>
        /// <returns>Task per il completamento del metodo</returns>
        private async Task EnsureCacheTableExistsAsync(IDbConnection connection, IDbTransaction transaction, CancellationToken ct)
        {
            const string createTableSql = @"
                IF OBJECT_ID('[dbo].[AI_SchemaCache]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[AI_SchemaCache] (
                        [TableName] NVARCHAR(255) PRIMARY KEY,
                        [Description] NVARCHAR(MAX),
                        [JsonSchema] NVARCHAR(MAX),
                        [VectorData] VARBINARY(MAX),
                        [SchemaHash] NVARCHAR(64),
                        [LastUpdated] DATETIME DEFAULT GETDATE()
                    );
                END";

            var command = new CommandDefinition(createTableSql, transaction: transaction, cancellationToken: ct);
            await connection.ExecuteAsync(command);
        }


        /// <summary>
        /// Metodo per calcolare un hash (SHA256) basato sulla descrizione e lo schema JSON di una tabella.
        /// </summary>
        /// <param name="input">stringa da calcolare (<see cref="string"/>)</param>
        /// <returns>stringa calcolata su base SHA256 (<see cref="string"/>) </returns>
        private static string ComputeHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;

            byte[] inputBytes = Encoding.UTF8.GetBytes(input);
            byte[] hashBytes = SHA256.HashData(inputBytes);

            return Convert.ToHexString(hashBytes);
        }
    }
}