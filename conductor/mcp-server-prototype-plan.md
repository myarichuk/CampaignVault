# D&D Campaign Vault - MCP Server Prototype Implementation Plan

## Objective
Create a minimal, reliable Model Context Protocol (MCP) server that provides a Large Language Model (specifically Grok, acting as a Dungeon Master) with structured, persistent, and queryable access to campaign state for a D&D 5e game. 

## Background & Motivation
Currently, LLMs struggle to maintain long-term, authoritative campaign state across multiple sessions. By building an MCP Server, we can provide the LLM with direct tools to read and write character sheets, lore, and event logs. The prototype focuses on correctness, LLM-friendly tool design, and low resource usage using .NET 10 and LiteDB.

## Scope & Impact
**In Scope:**
- Creation of a .NET 10 Minimal API project.
- Integration of `ModelContextProtocol.AspNetCore`.
- Integration of `LiteDB` for single-file, serverless database storage.
- Strongly-typed POCO data models with support for flexible, free-form data via `BsonExtraElements`.
- Implementation of 5 core MCP tools: `get_character`, `upsert_character`, `update_character`, `query_lore`, `log_event`.
- Basic indexing and error handling.
- Basic Bearer token authentication.

**Out of Scope:**
- Full 5e mechanics engine or combat simulation.
- Complex multi-user concurrent sessions.
- Vector embeddings or semantic search.

## Proposed Solution
We will implement the server using .NET Minimal APIs to keep the boilerplate low. 
Data access will be handled via a singleton `CampaignRepository` that wraps a `LiteDatabase` instance. Data models will be defined as C# records/classes containing explicit core fields (e.g., `Name`, `MaxHp`) and a catch-all dictionary for extra elements, providing safety where it matters and flexibility elsewhere.

### Core Data Models
- **Character:** `Id` (slug), `Name`, `ClassLevel`, `CurrentHp`, `MaxHp`, `Status` (List), `Relationships` (List), `Notes`, `LastUpdated`, plus `ExtraElements`.
- **Lore:** `Id`, `Title`, `Content`, `Tags` (List), `Keywords` (List), `Category`, `LastUpdated`.
- **Event:** `Id`, `Timestamp`, `SessionId`, `Type`, `Summary`, `Details` (Dictionary), `Involved` (List).

### Tool Implementations
1. `get_character`: Fetches a character by ID or partial name.
2. `upsert_character`: Inserts or replaces a full character record.
3. `update_character`: Performs a partial, incremental update on a character's fields.
4. `query_lore`: Searches the Lore collection using basic keyword and tag indexing.
5. `log_event`: Appends an event to the Event collection.

## Alternatives Considered
- **Standard ASP.NET Controllers:** Rejected in favor of Minimal APIs to reduce boilerplate for a prototype.
- **Raw BsonDocument Models:** Rejected in favor of Strongly-typed POCOs + Extra Elements, providing better developer ergonomics, type safety for core fields, while retaining the flexibility required for LLM tool usage.

## Phased Implementation Plan

### Phase 1: Project Scaffolding
1. Initialize a new .NET 10 Web App project (`dotnet new web`).
2. Add necessary NuGet packages (`LiteDB`, `ModelContextProtocol.AspNetCore`).
3. Set up the basic `Program.cs` structure, loading the database path and Bearer token from configuration.

### Phase 2: Data Access Layer
1. Define the POCO classes (`Character`, `Lore`, `Event`) using `BsonId` and `BsonExtraElements` attributes.
2. Create the `CampaignRepository` service.
3. Implement basic CRUD and query operations in the repository.
4. Configure LiteDB indexes on application startup.

### Phase 3: MCP Tools Registration
1. Implement the tool handlers as static methods or minimal API endpoints, decorated or registered as MCP tools.
2. Ensure each tool returns both the raw JSON data and a human-readable string summary.
3. Implement graceful error handling (returning error objects instead of raw exceptions).

### Phase 4: Documentation & Final Polish
1. Add structured logging to console.
2. Write the `README.md` containing run instructions, testing guides, and Grok prompts.

## Verification & Testing
- Run the server locally and connect using an MCP Inspector or a local tool.
- Verify that `LiteDB` successfully creates `campaign.db` and writes data correctly.
- Test each tool with both valid and invalid inputs to ensure robust error handling.

## Migration & Rollback
- Since this is a new prototype, no migration is necessary. The LiteDB `.db` file serves as the persistence mechanism and can be backed up easily by copying the file.