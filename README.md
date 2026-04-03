# AITeam.CodeSight

Code intelligence tool for any codebase. Indexes source code using tree-sitter AST parsing, then provides 12 query primitives via CLI. Agents and developers can search semantically, trace references, analyze impact, and explore code structure — all without reading full files.

## Setup

```
cd parsers && npm install --legacy-peer-deps
```

F# support requires `tree-sitter-fsharp` — copy it into `parsers/node_modules/` from a local build or the AITeam.Platform repo.

## Usage

```bash
# Index a codebase (incremental — only re-chunks changed files)
code-sight index --repo /path/to/repo

# Project map — what modules exist, how many files, key types
code-sight modules

# Direct queries — 12 primitives via JavaScript
code-sight search 'refs("MyType", {limit:10})'
code-sight search 'context("Orchestrator.fs")'
code-sight search 'grep("Result<string, CliError>")'
code-sight search 'modules()'

# Natural language exploration — dispatches to gpt-mini scout
code-sight intel "What does this codebase do?"
code-sight intel "Where should I add a new reactor?"
code-sight intel "What breaks if I change AgentConfig?"

# Scoped queries — filter to src, tests, or everything
code-sight search 'modules()' --scope src
code-sight search 'files("Test")' --scope tests

# Interactive REPL
code-sight repl

# List available scopes
code-sight scopes
```

If `--repo` is omitted, uses the current working directory.

## Primitives (12 functions)

| Function | Returns | Use for |
|----------|---------|---------|
| `search(query, {limit, kind, file})` | scored results with summary + signature + preview | semantic search |
| `refs(name, {limit})` | references with matchLine showing HOW it's used | tracing usage |
| `grep(pattern, {limit, kind, file})` | matches with matched line | exact pattern search |
| `modules()` | project-level map (files, chunks, types per module) | orientation |
| `files(pattern?)` | file list with chunk counts | file discovery |
| `context(file)` | all chunks + imports + dependents for a file | file overview |
| `impact(type)` | files that reference a type | type-level impact |
| `imports(file)` | what a file imports | dependency check |
| `deps(pattern)` | who imports from a module | reverse dependency |
| `expand(id)` | full source code + imports + dependents | read code |
| `neighborhood(id, {before, after})` | target code + surrounding chunks | code in context |
| `similar(id, {limit})` | structurally similar chunks | find patterns |

Results carry ref IDs (R1, R2...) — pass them to `expand()` or `neighborhood()` to drill deeper.

Compose with JavaScript: `.filter()`, `.map()`, `let` variables. Each query is isolated.

## Configuration

Place `code-intel.json` at the repo root. All fields are optional — defaults are auto-detected.

```json
{
    "srcDirs": ["src", "lib"],
    "extensions": [".fs", ".cs", ".ts", ".py"],
    "exclude": ["node_modules", "bin", "obj"],
    "scopes": {
        "src": ["src"],
        "tests": ["tests"],
        "all": ["src", "tests"]
    },
    "embeddingUrl": "http://localhost:1234/v1/embeddings",
    "chunkerScript": "path/to/ts-chunker.js"
}
```

## Overrides per repo

Place custom files in `.code-intel/` at the repo root:
- `.code-intel/parsers/` — custom tree-sitter parsers (overrides built-in)
- `.code-intel/playbooks/` — custom playbooks for the intel command

## Architecture

```
code-sight.exe
  ├── src/Types/         Core types (CodeChunk, ChunkEntry, CodeIndex)
  ├── src/Config/        code-intel.json loader, scope resolution
  ├── src/Services/      Embedding HTTP client, file hashing (SHA256)
  ├── src/Parsing/       Tree-sitter Node.js process wrapper
  ├── src/Index/         TSV + binary persistence, in-memory queries
  ├── src/Query/         12 primitives, Jint engine, formatting, intel
  ├── parsers/           JS files: ts-chunker.js, chunker-core.js, languages/*
  └── playbooks/         Strategy guides: orient, plan, blast, explore, review
```
