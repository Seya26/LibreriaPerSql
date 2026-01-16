namespace LibreriaPerSql.Services
{
    public interface IDbService
    {
        Task<IEnumerable<T>> ExecuteQueryAsync<T>(string sql, object? parameters = null);

        Task<string> GetSchemaJsonAsync();
    }
}
