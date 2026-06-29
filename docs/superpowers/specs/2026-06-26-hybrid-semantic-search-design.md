# Hybrid Semantic Search Design Spec

## Overview
Currently, CampaignVault relies on wildcard and full-text keyword searches via RavenDB. This means searches fail due to the synonym gap (e.g., searching "innkeeper" when the event states "bartender"). 
This design introduces **Hybrid Search** (combining keyword BM25 with mathematical vector similarity) entirely locally, preserving the core constraint of a free, zero-dependency, and offline-first application.

## Core Decisions
1. **No External APIs:** We will use a local embedding model to avoid imposing subscription costs or API configuration on the user.
2. **Hybrid Search Paradigm:** We will combine RavenDB's native full-text search with vector similarity to capture both exact proper nouns and broad semantic concepts.
3. **No Safety Filters:** The local embedding model operates purely on mathematical proximity and has no refusal logic, guaranteeing compatibility with all genres (including NSFW/violent campaigns).

## Architecture & Components

### 1. The Embedding Service
We will introduce `ILocalEmbeddingService` utilizing the `Microsoft.ML.OnnxRuntime` package.
- **Model:** We will use a lightweight ONNX embedding model (e.g., `all-MiniLM-L6-v2.onnx`, ~22MB).
- **Storage:** The `.onnx` model and its tokenizer configuration will be tracked via **Git LFS** in the repository (e.g., under `src/CampaignVault/Resources/Models/`) and deployed as "Copy to Output Directory".

**Interface:**
```csharp
public interface ILocalEmbeddingService
{
    Task<float[]> GenerateEmbeddingAsync(string text, CancellationToken ct = default);
}
```

### 2. Data Flow: The Write Path
- **Schema Update:** A new property `public float[]? SemanticVector { get; set; }` will be added to searchable narrative entities (`WorldEvent`, `Lore`, `Location`, `Rumor`, `Character`).
- **Generation:** When a narrative-heavy `WorldChange` (like `EventOccurred` or `LoreCreate`) is processed by its respective handler, the summary/text is passed to `ILocalEmbeddingService`. The resulting vector is attached to the entity before saving to RavenDB.

### 3. Data Flow: The Read Path
- **Index Update:** Existing RavenDB static indexes (`Event_Search`, `Lore_Search`, etc.) will be updated to index the `SemanticVector` field using RavenDB's native vector indexing capabilities.
- **Search Execution:** When `search_world` or `recall_history` is invoked:
  1. The query string is passed to `ILocalEmbeddingService` to generate a query vector.
  2. A hybrid query is executed against RavenDB, matching exact keywords (BM25) OR matching the query vector (Cosine similarity).
  3. Results are aggregated, ranked, and returned to the LLM.

## Implementation Steps
1. Configure `.gitattributes` to track `*.onnx` and `*.bin` files via Git LFS.
2. Add the ONNX model files to the repository resources.
3. Install `Microsoft.ML.OnnxRuntime` and implement `ILocalEmbeddingService`.
4. Update Domain Models with `SemanticVector`.
5. Update `ChangeHandlers` to generate vectors at write-time.
6. Update RavenDB `AbstractIndexCreationTask` implementations for vector indexing.
7. Update `SearchHelper.cs` (or equivalent) to construct and execute the hybrid query.
8. Validate search performance and accuracy via integration tests.

## Out of Scope
- Re-embedding existing records dynamically during runtime (a CLI migration tool can be added later if needed for backwards compatibility).
- Multi-lingual embedding models (focus on English first, via MiniLM).
