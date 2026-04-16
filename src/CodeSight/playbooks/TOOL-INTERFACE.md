# Code Intelligence — Agent Tool Interface

How agents interact with CodeSight. The agent sees two tools and doesn't know about playbooks, Jint, or sub-agents.

## Agent-facing tools

### `code_search(js)`
Run a specific query against the code index. For when the agent knows what to look for.

```
code_search('search("delivery engine", {limit: 5})')
code_search('refs("ChecklistTracker", {limit: 10})')
code_search('context("Orchestrator.fs")')
code_search('grep("Result<string, CliError>")')
```

Implementation: `code-sight search '<js>'` or `QueryEngine.eval(engine, js)`.

### `code_intel(question)`
Ask any question about the codebase in natural language.

```
code_intel("What does this codebase do?")
code_intel("Where should I add a new reactor?")
code_intel("What breaks if I change AgentConfig?")
```

Implementation: `code-sight intel '<question>'` or `Intel.run(engine, playbooksDir, question, modulesCache)`.
Dispatches to gpt-5.4-mini with `code_search` + `read_playbook` tools. Mini-agent picks the right playbook and explores adaptively.

## Agent system prompt addition

```
## Code intelligence

code_search(js) — query the code index. Available functions:
  search(query, opts), refs(name, opts), grep(pattern, opts), modules(),
  files(pattern?), context(file), expand(id), neighborhood(id, opts),
  impact(type), imports(file), deps(pattern), similar(id, opts)

code_intel(question) — ask about the codebase in natural language.
  Returns a structured brief with file:line references.
```

## Inside code_intel (transparent to agent)

```
Agent calls: code_intel("Where should I add a new reactor?")
  |
  v
code-sight intel dispatches to gpt-5.4-mini with:
  - System: system-prompt.md (scout role, governance, budget)
  - User: modules() output (auto-injected) + question
  - Tools: code_search(js), read_playbook(name)
  |
  v
Mini-agent:
  1. read_playbook("plan") — picks strategy based on question
  2. code_search('search("reactor", {limit:5})')
  3. code_search('context("AutomationReactor.fs")')
  4. code_search('grep("register.*reactor")')
  5. Synthesizes brief
  |
  v
Agent receives: "Edit AutomationReactor.fs:8. Follow pattern in
  ChecklistReactor.fs:6. Wire in AllPluginsReactorProvider.cs:15."
```

## Wiring (for platform team)

```fsharp
// code_search: shell out to code-sight
let codeSearch = AIFunctionFactory.Create(
    Func<string, string>(fun js ->
        // Option A: shell out
        Process.Start("code-sight", $"search '{js}' --repo {workDir}")
        // Option B: in-process (add CodeSight as project reference)
        QueryEngine.eval engine js),
    "code_search", "Query the code index...")

// code_intel: shell out to code-sight
let codeIntel = AIFunctionFactory.Create(
    Func<string, Task<string>>(fun question -> task {
        // Option A: shell out
        Process.Start("code-sight", $"intel '{question}' --repo {workDir}")
        // Option B: in-process
        return! Intel.run engine playbooksDir question modulesCache }),
    "code_intel", "Ask about the codebase...")
```
