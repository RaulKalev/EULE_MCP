# Standards Lookup

The standards lookup system lets AI agents search company rules, project specifications, and reference documents — fully offline, no cloud or embeddings required.

---

## How It Works

1. You configure **sources** in `%ProgramData%\RKTools\MCP\Config\StandardsSources.json`.
2. You run `standards_index_sources` to build a local text index in `%AppData%\RKTools\RevitMCP\StandardsIndex\`.
3. You search with `standards_search`. Results include a relevance score, heading, snippet, and chunk ID.
4. Use `standards_get_document_chunk` to retrieve a specific chunk plus its context.

Supported file types: `.md`, `.txt`, `.json` (PDF is not supported in the current version).

---

## Config File

Location:

```
%ProgramData%\RKTools\MCP\Config\StandardsSources.json
```

Example:

```json
{
  "sources": [
    {
      "sourceId": "company.eule.rules",
      "name": "EULE ettevõtte reeglid",
      "folderPath": "%ProgramData%\\RKTools\\MCP\\Standards",
      "fileTypes": [".md", ".txt", ".json"],
      "enabled": true
    },
    {
      "sourceId": "project.1626.rules",
      "name": "Projekt 1626 reeglid",
      "folderPath": "C:\\Projects\\1626\\00_Projektijuhtimine\\MCP",
      "fileTypes": [".md", ".txt"],
      "enabled": true
    }
  ]
}
```

If the config file does not exist, run `standards_validate_source_config` — it will create an example file at the correct location.

---

## Tools

### `standards_list_sources`

Lists all configured sources with their enabled/disabled status and whether they have been indexed.

### `standards_index_sources`

Indexes all enabled sources (or a specific source). Only re-indexes files whose modification date has changed since the last run. Use `force=true` to rebuild everything.

```
standards_index_sources                    — index all enabled sources
standards_index_sources sourceId=... force=true   — force re-index one source
```

### `standards_search`

Searches indexed content using tokenized keyword matching. Returns up to `maxResults` results ranked by score.

```
standards_search query="lehtede nimetamise reeglid EL TP" maxResults=10
standards_search query="cable sizing" sourceId=company.eule.rules discipline=electrical
```

Result fields: `sourceId`, `filePath`, `relPath`, `heading`, `snippet`, `score`, `chunkId`.

Use the `chunkId` from results with `standards_get_document_chunk` to read full content.

### `standards_get_document_chunk`

Returns a specific chunk by its ID, plus optional neighbouring chunks for context.

```
standards_get_document_chunk chunkId=... contextBefore=1 contextAfter=2
```

### `standards_validate_source_config`

Validates all configured source paths. Reports missing paths and misconfigured sources. Creates an example config file if none exists.

---

## Scoring

The search engine scores chunks as follows:

- +3 points per matching token found in the **heading**
- +1 point per matching token found in the **body text**
- ×1.5 multiplier if a `discipline` hint matches the source or content

Tokens are normalized (lowercase, trimmed). Common Estonian abbreviations are expanded during tokenization.

---

## Adding Standards Documents

1. Create Markdown, plain text, or JSON files in a folder configured as a source.
2. Use clear headings in Markdown (`#`, `##`) — these become the chunk titles.
3. Run `standards_index_sources` to pick up new or changed files.
4. Search immediately — no restart required.
