namespace LibreriaPerSql.Configurations
{
    public class DbConfig
    {
        public const string SectionName = "DbConfig";
        public string ProviderSQL { get; set; } = "SQLServer";
        public string ConnectionString { get; set; } = string.Empty;

    }
}
