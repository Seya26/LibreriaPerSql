namespace LibreriaPerSql.DTO
{
    public class TableEmbeddingDTO
    {
        public string TableName { get; set; } = null!;
        public string Description { get; set; } = null!;
        public string JsonSchema { get; set; } = null!;
        public byte[] VectorData { get; set; } = null!;
    }
}
