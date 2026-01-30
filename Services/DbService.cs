using Dapper;
using LibreriaPerSql.Configurations;
using LibreriaPerSql.DTO;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LibreriaPerSql.Services
{
	public class DbService : IDbService
	{
		private readonly DbConfig _config;
		private readonly IMemoryCache _cache;

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
			if (resourceName == null) throw new FileNotFoundException($"Embedded file {embeddedSqlName} not founded");

			// Apre lo stream della risorsa incorporata 
			using Stream? stream = assembly.GetManifestResourceStream(resourceName);
			if (stream == null) throw new InvalidOperationException($"Found the name '{resourceName}', but the stream is null. Something is wrong in loading the resource.");

			// Reader per leggere e ritornare il contenuto della risorsa come stringa 
			using StreamReader reader = new(stream);
			return reader.ReadToEnd();
		});

		public DbService(IOptions<DbConfig> config, IMemoryCache cache)
		{
			_config = config.Value;
			_cache = cache;
		}

		private SqlConnection CreateConnection()
		{
			return new SqlConnection(_config.ConnectionString);
		}

		public async Task<string> GetSchemaJsonAsync(IEnumerable<string>? tablesToInclude)
		{
			//Chiave per la cache che identifica se la whitelist è cambiata e quindi di riscaricare lo schema o di utilizzare quello presente nella cache
			// Se ce una whitelist, gli crea un nome (es.. schema_Students_Courses) per permettere di controllare se esiste nella cache lo stesso schema (esso viene poi orderBy alfabeticamente per evitare problemi di ordine (! schema_Courses_Students...sono la stessa cosa)
			// se invece whitelist nulla, allora etichetta con "Schema_All_Tables" cosi prende dalla cache la variabile con la giusta etichetta
			string cacheKey = tablesToInclude != null && tablesToInclude.Any() 
				? $"Schema_{string.Join("_", tablesToInclude.OrderBy(x => x))}" 
				: "Schema_All_Tables";

			//Restituiamo subito lo schema se gia presente con l'etichetta che gli abbiamo dato prima
			//Se ce, la restituisce 'out' in cachedJson
			if (_cache.TryGetValue(cacheKey, out string? cachedJson))
			{
				return cachedJson!;
			}

			var schemaQuery = _scriptSchemaSql.Value;

			string queryFilter = "";
			if (tablesToInclude != null && tablesToInclude.Any())
			{
				queryFilter += "\nWHERE (QUOTENAME(SCHEMA_NAME(t.schema_id)) + '.' + QUOTENAME(t.name)) IN @Tables";
			}
			queryFilter += "\nORDER BY t.name, c.column_id;";
			//Aggiungo il filtro per le tabelle
			schemaQuery += queryFilter;

			IEnumerable<RawSchemaDTO> rawSchema;
			try
			{
				using var connection = CreateConnection();
				rawSchema = await connection.QueryAsync<RawSchemaDTO>(schemaQuery, new { Tables = tablesToInclude });
			}
			catch (SqlException ex)
			{
				throw new Exception($"Error during DB reading: {ex.Message}", ex);
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
			var JsonResult = JsonSerializer.Serialize(schemaStructured, new JsonSerializerOptions
			{
				WriteIndented = false,
				DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
			});

			var cacheOptions = new MemoryCacheEntryOptions()
				.SetAbsoluteExpiration(TimeSpan.FromHours(1))
				.SetPriority(CacheItemPriority.High);
			_cache.Set(cacheKey, JsonResult, cacheOptions);

			return JsonResult;
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