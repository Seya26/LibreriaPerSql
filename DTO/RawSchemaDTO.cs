namespace LibreriaPerSql.DTO
{
    public class RawSchemaDTO
    {
        public string SchemaName { get; set; } = null!;
        public string TableName { get; set; } = null!;
        public string? TableDescription { get; set; }
        public string ColumnName { get; set; } = null!;
        public string? FullDataType { get; set; }
        public bool IsNullable { get; set; }
        public string? ColumnDescription { get; set; }
    }
}
