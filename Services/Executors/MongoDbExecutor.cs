using LibreriaPerSql.Configurations;
using LibreriaPerSql.DTO;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LibreriaPerSql.Executors
{
    /// <summary>
    /// Executor per MongoDB. Usa il MongoDB.Driver ufficiale.
    /// Riceve le query come MqlQueryDTO generati dall'AI.
    /// Solo lettura verso i dati utente — scrittura solo sulla cache interna.
    /// 
    /// DIFFERENZE CHIAVE rispetto a SqlServerExecutor:
    /// - Niente T-SQL: si usa MqlQueryDTO con filtri BSON
    /// - Lo schema è INFERITO campionando documenti (MongoDB è schema-less)
    /// - I dati embedded (es. scuola.citta) vengono navigati con dot-notation
    /// - Il campo _class (artefatto Java/Spring) viene sempre rimosso dai risultati
    /// </summary>
    public class MongoDbExecutor : IDbExecutor
    {
        private readonly IMongoDatabase _database;
        private readonly ILogger<MongoDbExecutor> _logger;

        private const string CacheCollectionName = "AI_SchemaCache";

        // Campi interni da escludere sempre dai risultati e dallo schema
        private static readonly HashSet<string> InternalFields = ["_class"];

        public MongoDbExecutor(IOptions<DbConfig> config, ILogger<MongoDbExecutor> logger)
        {
            _logger = logger;
            var cfg = config.Value;

            ArgumentException.ThrowIfNullOrEmpty(cfg.DatabaseName,
                $"DatabaseName è obbligatorio per il provider 'mongodb'. " +
                $"Aggiungerlo nella sezione '{DbConfig.SectionName}' di appsettings.json.");

            var client = new MongoClient(cfg.ConnectionString);
            _database = client.GetDatabase(cfg.DatabaseName);
        }

        // -------------------------------------------------------------------------
        // HELPERS PRIVATI
        // -------------------------------------------------------------------------

        private IMongoCollection<BsonDocument> GetBsonCollection(string name)
            => _database.GetCollection<BsonDocument>(name);

        /// <summary>
        /// Converte un object .NET in BsonDocument.
        /// Accetta sia stringhe JSON che oggetti serializzabili.
        /// </summary>
        private static BsonDocument ToBson(object? obj)
        {
            if (obj == null) return new BsonDocument();
            var json = obj is string s ? s : JsonSerializer.Serialize(obj);
            return BsonDocument.Parse(json);
        }

        private static string ComputeHash(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        }

        // -------------------------------------------------------------------------
        // VALIDAZIONE MqlQueryDTO
        // -------------------------------------------------------------------------

        /// <summary>
        /// Valida il MqlQueryDTO prima di eseguire la query.
        /// Blocca operazioni di scrittura e verifica i campi obbligatori.
        /// </summary>
        private static void ValidateMqlQuery(MqlQueryDTO mql)
        {
            ArgumentException.ThrowIfNullOrEmpty(mql.Collection,
                "MqlQueryDTO.Collection è obbligatorio.");

            var op = mql.Operation?.ToLowerInvariant();

            // Blocca operazioni di scrittura anche se generate erroneamente dall'AI
            string[] forbidden = ["insert", "update", "delete", "drop", "replace"];
            if (forbidden.Contains(op))
                throw new InvalidOperationException(
                    $"Operazione '{mql.Operation}' non consentita. " +
                    $"Questo servizio supporta solo operazioni di lettura: find | aggregate | count.");

            if (op == "aggregate" && (mql.Pipeline == null || !mql.Pipeline.Any()))
                throw new ArgumentException(
                    "MqlQueryDTO.Pipeline è obbligatorio per operation='aggregate'.");
        }

        // -------------------------------------------------------------------------
        // LETTURA
        // -------------------------------------------------------------------------

        public async Task<IEnumerable<dynamic>> ExecuteQueryAsync(object query, object? parameters = null, CancellationToken ct = default)
        {
            if (query is not MqlQueryDTO mql)
                throw new ArgumentException(
                    $"MongoDbExecutor richiede un MqlQueryDTO. Ricevuto: {query.GetType().Name}. " +
                    $"Verificare che il provider configurato sia 'mongodb'.");

            ValidateMqlQuery(mql);

            // Sostituisce i parametri di contesto nel filtro MQL.
            // Con SQL Server Dapper fa questo automaticamente (@NegozioId → valore).
            // Con MongoDB dobbiamo farlo manualmente: il filtro JSON è una stringa,
            // cerchiamo pattern "@NomeParametro" e li sostituiamo col valore reale.
            // Es: filter: {"negozioId": "@NegozioId"} + params: {NegozioId: 5}
            //  → filter: {"negozioId": 5}
            if (parameters != null && mql.Filter != null)
            {
                var filterJson = System.Text.Json.JsonSerializer.Serialize(mql.Filter);
                var paramDict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                    System.Text.Json.JsonSerializer.Serialize(parameters)) ?? new();

                foreach (var (key, value) in paramDict)
                {
                    // Sostituisce la stringa "@NomeParametro" (con le virgolette incluse) col valore tipizzato
                    filterJson = filterJson.Replace($"\"@{key}\"", JsonSerializer.Serialize(value));
                }
                // Ricrea il filtro dal JSON aggiornato
                mql.Filter = System.Text.Json.JsonDocument.Parse(filterJson).RootElement;
            }

            try
            {
                return mql.Operation.ToLowerInvariant() switch
                {
                    "find"      => await ExecuteFindAsync(mql, ct),
                    "aggregate" => await ExecuteAggregateAsync(mql, ct),
                    "count"     => await ExecuteCountAsync(mql, ct),
                    _ => throw new NotSupportedException(
                        $"Operazione '{mql.Operation}' non supportata. Usare: find | aggregate | count.")
                };
            }
            catch (Exception ex) when (ex is not InvalidOperationException and not ArgumentException)
            {
                _logger.LogError(ex, "Errore ExecuteQueryAsync MongoDB. Collection: {Collection}, Operation: {Operation}",
                    mql.Collection, mql.Operation);
                throw;
            }
        }

        /// <summary>
        /// Esegue una find.
        /// Equivalente di: SELECT [projection] FROM collection WHERE [filter] ORDER BY [sort] LIMIT [limit]
        /// </summary>
        private async Task<IEnumerable<dynamic>> ExecuteFindAsync(MqlQueryDTO mql, CancellationToken ct)
        {
            var collection = GetBsonCollection(mql.Collection);

            var filter = mql.Filter != null
                ? new BsonDocumentFilterDefinition<BsonDocument>(ToBson(mql.Filter))
                : Builders<BsonDocument>.Filter.Empty;

            var findFluent = collection.Find(filter);

            if (mql.Projection != null)
                findFluent = findFluent.Project<BsonDocument>(ToBson(mql.Projection));

            if (mql.Sort != null)
                findFluent = findFluent.Sort(ToBson(mql.Sort));

            if (mql.Limit.HasValue)
                findFluent = findFluent.Limit(mql.Limit.Value);

            var results = await findFluent.ToListAsync(ct);
            return results.Select(doc => BsonToExpando(doc, removeInternalFields: true));
        }

        /// <summary>
        /// Esegue una aggregation pipeline.
        /// Equivalente di query complesse con GROUP BY, $lookup (JOIN), HAVING, ecc.
        /// </summary>
        private async Task<IEnumerable<dynamic>> ExecuteAggregateAsync(MqlQueryDTO mql, CancellationToken ct)
        {
            var collection = GetBsonCollection(mql.Collection);

            var stages = mql.Pipeline!
                .Select(stage => (PipelineStageDefinition<BsonDocument, BsonDocument>)
                    new BsonDocumentPipelineStageDefinition<BsonDocument, BsonDocument>(ToBson(stage)))
                .ToList();

            var pipeline = PipelineDefinition<BsonDocument, BsonDocument>.Create(stages);
            var results = await collection.Aggregate(pipeline).ToListAsync(ct);
            return results.Select(doc => BsonToExpando(doc, removeInternalFields: true));
        }

        /// <summary>
        /// Esegue un count.
        /// Equivalente di: SELECT COUNT(*) FROM collection WHERE [filter]
        /// </summary>
        private async Task<IEnumerable<dynamic>> ExecuteCountAsync(MqlQueryDTO mql, CancellationToken ct)
        {
            var collection = GetBsonCollection(mql.Collection);

            var filter = mql.Filter != null
                ? new BsonDocumentFilterDefinition<BsonDocument>(ToBson(mql.Filter))
                : Builders<BsonDocument>.Filter.Empty;

            var count = await collection.CountDocumentsAsync(filter, cancellationToken: ct);

            var result = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
            result["count"] = count;
            result["collection"] = mql.Collection;
            return [result];
        }

        // -------------------------------------------------------------------------
        // SCHEMA INFERENCE
        // -------------------------------------------------------------------------

        public async Task<string> GetSchemaJsonAsync(IEnumerable<string>? blackList = null, CancellationToken ct = default)
        {
            var excluded = new HashSet<string> { CacheCollectionName };
            if (blackList != null)
                foreach (var b in blackList) excluded.Add(b);

            var cursor = await _database.ListCollectionNamesAsync(cancellationToken: ct);
            var allCollections = await cursor.ToListAsync(ct);
            var toProcess = allCollections.Where(n => !excluded.Contains(n)).ToList();

            var schema = new List<object>();

            foreach (var name in toProcess)
            {
                var collection = GetBsonCollection(name);

                // Usa $sample per campionare documenti casuali invece dei primi N
                // Questo copre meglio la variabilità dello schema in collezioni grandi
                var samplePipeline = new BsonDocument[]
                {
                    new("$sample", new BsonDocument("size", 10))
                };

                List<BsonDocument> samples;
                try
                {
                    samples = await collection.Aggregate<BsonDocument>(samplePipeline).ToListAsync(ct);
                }
                catch
                {
                    // Fallback se $sample non è supportato (es. standalone non replicato)
                    samples = await collection.Find(Builders<BsonDocument>.Filter.Empty)
                        .Limit(10).ToListAsync(ct);
                }

                // Raccoglie tutti i campi dai campioni, escludendo i campi interni
                var fields = samples
                    .SelectMany(doc => doc.Elements.Select(e => e.Name))
                    .Distinct()
                    .Where(f => !InternalFields.Contains(f))
                    .ToList();

                var count = await collection.CountDocumentsAsync(
                    Builders<BsonDocument>.Filter.Empty, cancellationToken: ct);

                // IMPORTANTE: struttura identica all'output di SQL Server (TableName, Description, Columns)
                // così EmbeddingService e AiAgentService deserializzano in List<TableSchema>
                // senza sapere nulla del provider sottostante.
                schema.Add(new
                {
                    TableName = name,
                    Description = $"MongoDB collection with {count} documents.",
                    Columns = fields.Select(f => new
                    {
                        Name = f,
                        Type = InferFieldType(samples, f),
                        // Codifica info extra nella Description della colonna:
                        // l'AI sa usare dot-notation e gestire campi embedded/opzionali
                        Description = BuildColumnDescription(samples, f)
                    }).ToList()
                });
            }

            // PascalCase per coerenza con SQL Server e con TableSchema
            return JsonSerializer.Serialize(schema, new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }

        /// <summary>
        /// Costruisce una descrizione testuale per una colonna MongoDB.
        /// Viene inclusa nel JSON schema per dare contesto all'AI (dot-notation, campi opzionali).
        /// </summary>
        private static string? BuildColumnDescription(List<BsonDocument> samples, string field)
        {
            var parts = new List<string>();
            if (IsEmbeddedField(samples, field))
                parts.Add("embedded document — use dot-notation (e.g. field.subfield)");
            if (samples.Any(doc => !doc.Contains(field)))
                parts.Add("optional field");
            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        private static string InferFieldType(List<BsonDocument> samples, string field)
        {
            var types = samples
                .Where(doc => doc.Contains(field))
                .Select(doc => doc[field].BsonType.ToString())
                .GroupBy(t => t)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .ToList();

            return types.Count switch
            {
                0 => "Unknown",
                1 => types[0],
                _ => string.Join("|", types) // es. "String|Null" se alcuni doc hanno il campo null
            };
        }

        private static bool IsEmbeddedField(List<BsonDocument> samples, string field)
            => samples.Where(doc => doc.Contains(field))
                      .Any(doc => doc[field].BsonType is BsonType.Document or BsonType.Array);

        // -------------------------------------------------------------------------
        // CACHE EMBEDDING (solo uso interno)
        // -------------------------------------------------------------------------

        public async Task UpsertEmbeddingsAsync(IEnumerable<TableEmbeddingDTO> embeddings, CancellationToken ct = default)
        {
            if (embeddings == null || !embeddings.Any()) return;

            var cacheCollection = GetBsonCollection(CacheCollectionName);
            await EnsureCacheIndexAsync(cacheCollection, ct);

            var activeNames = new List<string>();

            foreach (var item in embeddings)
            {
                var hash = ComputeHash(item.Description + item.JsonSchema);
                activeNames.Add(item.TableName);

                var doc = new BsonDocument
                {
                    ["_id"]         = item.TableName,
                    ["tableName"]   = item.TableName,
                    ["description"] = item.Description ?? string.Empty,
                    ["jsonSchema"]  = item.JsonSchema ?? string.Empty,
                    ["vectorData"]  = item.VectorData != null
                                        ? new BsonBinaryData(item.VectorData)
                                        : BsonNull.Value,
                    ["schemaHash"]  = hash,
                    ["lastUpdated"] = DateTime.UtcNow
                };

                // Upsert solo se l'hash è cambiato — equivalente del MERGE SQL Server
                var filter = Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq("_id", item.TableName),
                    Builders<BsonDocument>.Filter.Ne("schemaHash", hash)
                );

                try
                {
                    await cacheCollection.ReplaceOneAsync(filter, doc, new ReplaceOptions { IsUpsert = true }, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Errore upsert embedding per {TableName}", item.TableName);
                    throw;
                }
            }

            // Rimuove dalla cache le collezioni che non esistono più nel DB
            var deleteFilter = Builders<BsonDocument>.Filter.Nin("_id", activeNames);
            await cacheCollection.DeleteManyAsync(deleteFilter, ct);
        }

        public async Task<IEnumerable<TableEmbeddingDTO>> GetAllEmbeddingsAsync(CancellationToken ct = default)
        {
            var cacheCollection = GetBsonCollection(CacheCollectionName);
            var docs = await cacheCollection.Find(Builders<BsonDocument>.Filter.Empty).ToListAsync(ct);

            return docs.Select(doc => new TableEmbeddingDTO
            {
                TableName   = doc["tableName"].AsString,
                Description = doc.GetValue("description", BsonNull.Value) != BsonNull.Value ? doc["description"].AsString : string.Empty,
                JsonSchema  = doc.GetValue("jsonSchema", BsonNull.Value) != BsonNull.Value ? doc["jsonSchema"].AsString : string.Empty,
                VectorData  = doc.GetValue("vectorData", BsonNull.Value) != BsonNull.Value ? doc["vectorData"].AsByteArray : Array.Empty<byte>()
            }).ToList();
        }

        public async Task<int> CountEmbeddingsAsync(CancellationToken ct = default)
        {
            var cacheCollection = GetBsonCollection(CacheCollectionName);
            return (int)await cacheCollection.CountDocumentsAsync(Builders<BsonDocument>.Filter.Empty, cancellationToken: ct);
        }

        private static async Task EnsureCacheIndexAsync(IMongoCollection<BsonDocument> collection, CancellationToken ct)
        {
            var model = new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending("tableName"),
                new CreateIndexOptions { Unique = true, Background = true });
            try
            {
                await collection.Indexes.CreateOneAsync(model, cancellationToken: ct);
            }
            catch (MongoCommandException ex) when (ex.CodeName == "IndexOptionsConflict") { /* già esiste */ }
        }

        // -------------------------------------------------------------------------
        // CONVERSIONE BsonDocument → dynamic (per Syncfusion Grid/Charts)
        // -------------------------------------------------------------------------

        private static dynamic BsonToExpando(BsonDocument doc, bool removeInternalFields = false)
        {
            var expando = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;

            foreach (var element in doc.Elements)
            {
                if (removeInternalFields && InternalFields.Contains(element.Name))
                    continue;

                expando[element.Name] = BsonValueToObject(element.Value);
            }

            return expando;
        }

        private static object? BsonValueToObject(BsonValue value) => value.BsonType switch
        {
            BsonType.Document => BsonToExpando(value.AsBsonDocument, removeInternalFields: true),
            BsonType.Array    => value.AsBsonArray.Select(BsonValueToObject).ToList(),
            // ObjectId convertito in stringa — Syncfusion non sa cos'è un ObjectId
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.DateTime => value.ToUniversalTime(),
            BsonType.Boolean  => value.AsBoolean,
            BsonType.Int32    => value.AsInt32,
            BsonType.Int64    => value.AsInt64,
            BsonType.Double   => value.AsDouble,
            BsonType.Decimal128 => value.AsDecimal,
            BsonType.String   => value.AsString,
            BsonType.Null     => null,
            _                 => value.ToString()
        };
    }
}
