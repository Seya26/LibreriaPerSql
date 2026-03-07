namespace LibreriaPerSql.DTO
{
    /// <summary>
    /// Rappresenta una query MQL generata dall'AI per MongoDB.
    /// Viene passata a ExecuteQueryAsync come parametro "query" (object).
    /// 
    /// Esempio JSON generato dall'AI:
    /// {
    ///   "collection": "Studenti",
    ///   "operation": "find",
    ///   "filter": { "scuola.citta": "Parma" },
    ///   "projection": { "nome": 1, "cognome": 1, "_id": 0 },
    ///   "sort": { "cognome": 1 },
    ///   "limit": 50
    /// }
    /// </summary>
    public class MqlQueryDTO
    {
        /// <summary>
        /// Nome della collezione MongoDB (es. "Studenti", "Attivita").
        /// Equivalente del nome tabella in SQL.
        /// </summary>
        public string Collection { get; set; } = null!;

        /// <summary>
        /// Tipo di operazione: "find" | "aggregate" | "count"
        /// </summary>
        public string Operation { get; set; } = "find";

        /// <summary>
        /// Filtro MQL. Equivalente della clausola WHERE.
        /// Es: { "scuola.citta": "Parma" }
        /// </summary>
        public object? Filter { get; set; }

        /// <summary>
        /// Campi da restituire. Equivalente della SELECT col1, col2.
        /// Es: { "nome": 1, "cognome": 1, "_id": 0 }
        /// </summary>
        public object? Projection { get; set; }

        /// <summary>
        /// Ordinamento. Equivalente di ORDER BY.
        /// Es: { "cognome": 1 } (1=ASC, -1=DESC)
        /// </summary>
        public object? Sort { get; set; }

        /// <summary>
        /// Numero massimo di documenti. Equivalente di TOP / LIMIT.
        /// </summary>
        public int? Limit { get; set; }

        /// <summary>
        /// Pipeline di aggregazione. Usata solo quando Operation = "aggregate".
        /// Equivalente di query complesse con GROUP BY, $lookup (JOIN), ecc.
        /// Es: [{ "$match": { "annoAcc": 20242025 } }, { "$group": { "_id": "$corso" } }]
        /// </summary>
        public IEnumerable<object>? Pipeline { get; set; }
    }
}
