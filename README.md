# Sight — Code & Knowledge Intelligence Tools

Two complementary CLI tools that index and query codebases and documentation, giving LLM agents and developers structured access without reading full files.

| Tool | What it does | Binary |
|------|-------------|--------|
| **[CodeSight](src/CodeSight/)** | Indexes code via tree-sitter AST parsing → JS-scriptable query DSL | `code-sight` |
| **[KnowledgeSight](src/KnowledgeSight/)** | Indexes Markdown documentation → JS-scriptable query DSL | `knowledge-sight` |

Both tools share the same query engine pattern: index once, query with composable JS primitives, chain results with `.map()`, `.filter()`, `.reduce()`.

## Install

**CodeSight:**
```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/micsh/Sight/main/install-code-sight.sh | bash

# Windows (PowerShell)
irm https://raw.githubusercontent.com/micsh/Sight/main/install-code-sight.ps1 | iex
```

**KnowledgeSight:**
```bash
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/micsh/Sight/main/install-knowledge-sight.sh | bash

# Windows (PowerShell)
irm https://raw.githubusercontent.com/micsh/Sight/main/install-knowledge-sight.ps1 | iex
```

Or build from source:
```bash
dotnet publish src/CodeSight/AITeam.CodeSight.fsproj -c Release -o ~/.code-sight
dotnet publish src/KnowledgeSight/AITeam.KnowledgeSight.fsproj -c Release -o ~/.knowledge-sight-cli
```

Requires: .NET 10, Node.js 20+ (for CodeSight tree-sitter parsers), LM Studio embedding server on `localhost:1234`.

## Quick start

```bash
# Index a codebase
code-sight index

# What's in this codebase?
code-sight search 'modules()'

# Index documentation
knowledge-sight index

# Find docs about a topic
knowledge-sight search 'search("authentication")'

# Bridge — cross-index queries (CodeSight only, requires KS index)
code-sight eval 'drift()' --peer ../docs-repo
code-sight eval 'coverage({minFanIn:5})'
```

## Bridge

When both tools' indexes are available, CodeSight exposes bridge primitives that query across indexes:

- **`drift()`** — doc sections referencing code symbols that no longer exist
- **`coverage(opts?)`** — high fan-in code files with no documentation

Use `--peer <path>` to point to a KS index in a different repo.

## Project structure

```
src/
├── CodeSight/          # code-sight CLI (F#, tree-sitter, Jint)
└── KnowledgeSight/     # knowledge-sight CLI (F#, Markdown parser, Jint)
```

See each tool's README for full primitive reference, configuration, and usage details.

## License

MIT
