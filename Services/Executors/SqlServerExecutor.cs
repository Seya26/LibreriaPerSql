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

namespace LibreriaPerSql.Executors
{
    /// <summary>
    /// Executor per SQL Server. Usa Dapper per l'esecuzione delle query.
    /// Riceve le query come stringhe T-SQL generate dall'AI.
    /// Solo lettura verso i dati utente — scrittura solo sulla cache interna.
    /// </summary>
    public class SqlServerExecutor : IDbExecutor
    {
        private readonly DbConfig _config;
        private readonly ILogger<SqlServerExecutor> _logger;

        private static readonly Lazy<string> _scriptSchemaSql = new(() =>
            LoadEmbeddedSql("LibreriaPerSql.Resources.ScriptSQL.ScriptGetDatabaseSchema.sql"));

        public SqlServerExecutor(IOptions<DbConfig> config, ILogger<SqlServerExecutor> logger)
        {
            _config = config.Value;
            _logger = logger;
        }

        // -------------------------------------------------------------------------
        // HELPERS PRIVATI
        // -------------------------------------------------------------------------

        private SqlConnection CreateConnection() => new(_config.ConnectionString);

        private static string LoadEmbeddedSql(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fullName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceName))
                ?? throw new FileNotFoundException($"Risorsa embedded '{resourceName}' non trovata.");

            using var stream = assembly.GetManifestResourceStream(fullName)
                ?? throw new InvalidOperationException($"Stream nullo per '{fullName}'.");

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        private static string ComputeHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        }

        // -------------------------------------------------------------------------
        // LETTURA
        // -------------------------------------------------------------------------

        public async Task<IEnumerable<dynamic>> ExecuteQueryAsync(object query, object? parameters = null, CancellationToken ct = default)
        {
            if (query is not string sql)
                throw new ArgumentException(
                    $"SqlServerExecutor richiede una stringa T-SQL. Ricevuto: {query.GetType().Name}. " +
                    $"Verificare che il provider configurato sia 'sqlserver'.");

            ArgumentException.ThrowIfNullOrEmpty(sql);

            // Sicurezza: blocca query di modifica anche se generate erroneamente dall'AI
            ValidateReadOnlyQuery(sql);

            var dynamicParams = parameters == null ? null : new DynamicParameters(parameters);
            using var connection = CreateConnection();

            try
            {
                var results = await connection.QueryAsync(
                    new CommandDefinition(sql, dynamicParams, cancellationToken: ct));
                return results.Cast<dynamic>().ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore ExecuteQueryAsync SQL Server. SQL: {Sql}", sql);
                throw;
            }
        }

        /// <summary>
        /// Verifica che la query sia di sola lettura.
        /// Blocca istruzioni di modifica anche se generate erroneamente dall'AI.
        /// </summary>
        private static void ValidateReadOnlyQuery(string sql)
        {
            var upper = sql.TrimStart().ToUpperInvariant();
            string[] forbidden = ["INSERT", "UPDATE", "DELETE", "DROP", "TRUNCATE", "ALTER", "CREATE", "MERGE", "EXEC", "EXECUTE"];

            var found = forbidden.FirstOrDefault(kw => upper.StartsWith(kw) || upper.Contains($" {kw} "));
            if (found != null)
                throw new InvalidOperationException(
                    $"Operazione '{found}' non consentita. Questo servizio supporta solo query di lettura (SELECT).");
        }

        public async Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList = null, CancellationToken ct = default)
        {
            var schemaQuery = new StringBuilder(_scriptSchemaSql.Value);
            var parameters = new DynamicParameters();

            if (blackList != null && blackList.Any())
            {
                schemaQuery.AppendLine("\nWHERE (QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)) NOT IN @Tables");
                parameters.Add("Tables", blackList);
            }

            schemaQuery.AppendLine("\nORDER BY t.name, c.column_id;");

            try
            {
                using var connection = CreateConnection();
                var rawSchema = (await connection.QueryAsync<RawSchemaDTO>(
                    new CommandDefinition(schemaQuery.ToString(), parameters, cancellationToken: ct))).ToList();

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
                            IsNullable = c.IsNullable,
                            Description = c.ColumnDescription
                        }).ToList()
                    });

                return JsonSerializer.Serialize(schemaStructured, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore GetSchemaJsonAsync SQL Server.");
                throw;
            }
        }

        // -------------------------------------------------------------------------
        // CACHE EMBEDDING (solo uso interno)
        // -------------------------------------------------------------------------

        public async Task UpsertEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> embeddings, CancellationToken ct = default)
        {
            if (embeddings == null || !embeddings.Any()) return;

            using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            using var transaction = connection.BeginTransaction();

            try
            {
                await EnsureCacheTableExistsAsync(connection, transaction, ct);

                var data = embeddings.Select(t => new
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
                        UPDATE SET Description = source.Description, JsonSchema = source.JsonSchema,
                                   VectorData = source.VectorData, SchemaHash = source.SchemaHash,
                                   LastUpdated = GETDATE()
                    WHEN NOT MATCHED THEN
                        INSERT (TableName, Description, JsonSchema, VectorData, SchemaHash, LastUpdated)
                        VALUES (source.TableName, source.Description, source.JsonSchema,
                                source.VectorData, source.SchemaHash, GETDATE());";

                await connection.ExecuteAsync(new CommandDefinition(mergeSql, data, transaction, cancellationToken: ct));

                var activeNames = data.Select(t => t.TableName).ToList();
                await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM [dbo].[AI_SchemaCache] WHERE TableName NOT IN @ActiveTables",
                    new { ActiveTables = activeNames }, transaction, cancellationToken: ct));

                await transaction.CommitAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Errore UpsertEmbeddingsAsync SQL Server. Rollback in corso.");
                try { await transaction.RollbackAsync(ct); } catch { /* ignored */ }
                throw;
            }
        }

        public async Task<IEnumerable<TableEmbeddingDTO>> GetAllEmbeddingsAsync(CancellationToken ct = default)
        {
            await EnsureCacheTableExistsNoTransactionAsync(ct);
            using var connection = CreateConnection();
            return (await connection.QueryAsync<TableEmbeddingDTO>(
                new CommandDefinition(
                    "SELECT TableName, Description, JsonSchema, VectorData FROM [dbo].[AI_SchemaCache]",
                    cancellationToken: ct))).ToList();
        }

        public async Task<int> CountEmbeddingsAsync(CancellationToken ct = default)
        {
            await EnsureCacheTableExistsNoTransactionAsync(ct);
            using var connection = CreateConnection();
            return await connection.ExecuteScalarAsync<int>(
                new CommandDefinition("SELECT COUNT(*) FROM [dbo].[AI_SchemaCache]", cancellationToken: ct));
        }

        private static async Task EnsureCacheTableExistsAsync(SqlConnection connection, IDbTransaction transaction, CancellationToken ct)
        {
            const string sql = @"
                IF OBJECT_ID('[dbo].[AI_SchemaCache]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[AI_SchemaCache] (
                        [TableName]   NVARCHAR(255) PRIMARY KEY,
                        [Description] NVARCHAR(MAX),
                        [JsonSchema]  NVARCHAR(MAX),
                        [VectorData]  VARBINARY(MAX),
                        [SchemaHash]  NVARCHAR(64),
                        [LastUpdated] DATETIME DEFAULT GETDATE()
                    );
                END";
            await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: ct));
        }

        private async Task EnsureCacheTableExistsNoTransactionAsync(CancellationToken ct = default)
        {
            const string sql = @"
                IF OBJECT_ID('[dbo].[AI_SchemaCache]', 'U') IS NULL
                BEGIN
                    CREATE TABLE [dbo].[AI_SchemaCache] (
                        [TableName]   NVARCHAR(255) PRIMARY KEY,
                        [Description] NVARCHAR(MAX),
                        [JsonSchema]  NVARCHAR(MAX),
                        [VectorData]  VARBINARY(MAX),
                        [SchemaHash]  NVARCHAR(64),
                        [LastUpdated] DATETIME DEFAULT GETDATE()
                    );
                END";
            using var connection = CreateConnection();
            await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
        }
    }
}
