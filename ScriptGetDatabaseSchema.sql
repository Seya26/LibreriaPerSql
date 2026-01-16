SELECT 
    SCHEMA_NAME(t.schema_id) AS SchemaName,
    t.name AS TableName,
    td.value AS TableDescription,
    c.name AS ColumnName,
    
    -- Creiamo una stringa leggibile per il tipo di dato (es. NVARCHAR(50))
    UPPER(ty.name) + 
    CASE 
        WHEN ty.name IN ('nvarchar', 'nchar') AND c.max_length != -1 
            THEN '(' + CAST(c.max_length / 2 AS VARCHAR(10)) + ')'
        WHEN ty.name IN ('varchar', 'char', 'varbinary') AND c.max_length != -1 
            THEN '(' + CAST(c.max_length AS VARCHAR(10)) + ')'
        WHEN c.max_length = -1 
            THEN '(MAX)'
        WHEN ty.name IN ('decimal', 'numeric')
            THEN '(' + CAST(c.precision AS VARCHAR(10)) + ',' + CAST(c.scale AS VARCHAR(10)) + ')'
        ELSE ''
    END AS FullDataType,

    c.is_nullable AS IsNullable,
    cd.value AS ColumnDescription

FROM sys.tables t
-- Join Descrizione Tabella
LEFT JOIN sys.extended_properties td 
    ON t.object_id = td.major_id 
    AND td.minor_id = 0 
    AND td.name = 'MS_Description'
INNER JOIN sys.columns c ON t.object_id = c.object_id
INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
-- Join Descrizione Colonna
LEFT JOIN sys.extended_properties cd 
    ON t.object_id = cd.major_id 
    AND c.column_id = cd.minor_id 
    AND cd.name = 'MS_Description'
ORDER BY t.name, c.column_id;