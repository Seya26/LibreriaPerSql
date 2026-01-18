# LibreriaPerSql: SQL Schema Extractor & Dapper Wrapper

![.NET 8](https://img.shields.io/badge/.NET-8.0-blue)
![Dapper](https://img.shields.io/badge/ORM-Dapper-green)
![License](https://img.shields.io/badge/License-MIT-blue)

Una libreria .NET 8 leggera e performante progettata per interagire con Microsoft SQL Server. Offre funzionalità ottimizzate per l'estrazione dei metadati del database (Schema Reflection) e l'esecuzione di query rapide tramite **Dapper**.

> **Use Case Ideale:** Questa libreria è perfetta per fornire il "contesto" agli agenti AI (RAG o Text-to-SQL). Il metodo di estrazione dello schema genera un JSON pulito e dettagliato che aiuta gli LLM a capire la struttura del database.

## Caratteristiche Principali

* **Schema Reflection Intelligente:** Estrae automaticamente tabelle, colonne, tipi di dato (es. `NVARCHAR(50)`, `DECIMAL(10,2)`) e descrizioni (Extended Properties) in un formato JSON strutturato.
* **Embedded Scripts:** Gli script SQL di sistema sono incorporati nella DLL come risorse, rendendo la libreria portabile senza dipendenze da file esterni.
* **Dapper Integration:** Wrapper leggero su Dapper per l'esecuzione asincrona di query ad alte prestazioni.
* **Configuration Friendly:** Supporta il pattern `IOptions` per una configurazione sicura tramite Dependency Injection.

## Per Iniziare

### Prerequisiti
* .NET 8 SDK
* Un database SQL Server accessibile

### Configurazione

Aggiungi la stringa di connessione nel file `appsettings.json` dell'applicazione ospite (o nei User Secrets per lo sviluppo locale):

```json
{
  "DbConfig": {
    "ProviderSQL": "SQLServer",
    "ConnectionString": "Server=myServerAddress;Database=myDataBase;Trusted_Connection=True;TrustServerCertificate=True;"
  }
}
