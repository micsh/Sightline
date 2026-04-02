# Playbook: Review

Produce an architecture and code quality review for the codebase.

## Output (exactly this structure)

1. **Architecture** — module structure, dependency direction, any circular deps
2. **Backbone types** — 3-5 most referenced types and their role
3. **Duplication** — repeated patterns that should be extracted
4. **Complexity hotspots** — largest files/modules, deeply nested code
5. **Verdict** — one sentence: is the architecture sound?

## Approach

1. `modules()` — get the project-level map
2. For 3-4 key files: `imports(file)` — check dependency direction flows one way
3. `refs(typeName)` on 2-3 types that look central — count breadth
4. `grep` for repeated patterns (e.g., common boilerplate, duplicated lookups)
5. `files("").sort((a,b) => b.chunks - a.chunks).slice(0,5)` — find the largest files

**You're done when you can fill all 5 sections.** Focus on structural issues, not style.
