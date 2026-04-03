# Playbook: Orient

Produce a codebase orientation brief.

## Output (exactly this structure)

1. **What it is** — one sentence
2. **Entry points** — 2-3 files where execution starts, with line refs
3. **Key modules** — 4-6 modules, each with a one-line role description
4. **Backbone types** — 3-5 types that are referenced widely
5. **Dependency flow** — one sentence: what depends on what

## Step 1: Get the landscape (one call)

```js
let m = modules();
let topModule = m.sort((a,b) => b.chunks - a.chunks)[0];
({
  moduleCount: m.length,
  modules: m.map(x => ({name: x.module, files: x.files, chunks: x.chunks, types: x.topTypes})),
  largestModule: topModule.module
})
```

This gives you modules, file counts, and top types. You can already fill "Key modules" from this.

## Step 2: Find entry points (one call)

Pick the module that looks like the app host (usually has "App" or "Program" in it):

```js
let p = context("Program.cs");
let a = context("AppOrchestrator.cs");
({
  program: {chunks: p.chunks.length, imports: p.imports, first: p.chunks.slice(0,3).map(c => c.name + " L" + c.line)},
  appOrch: {chunks: a.chunks.length, imports: a.imports, first: a.chunks.slice(0,3).map(c => c.name + " L" + c.line)}
})
```

If those files don't exist, adapt: search for the entry point with `search("main entry point startup", {limit:3})`.

## Step 3: Confirm backbone types (one call)

Pick 2-3 types that appeared in the modules data and check their reach:

```js
let r1 = refs("Orchestrator", {limit:20});
let r2 = refs("BoardRepository", {limit:20});
({
  orchestrator: {refs: r1.length, files: [...new Set(r1.map(r => r.file))]},
  boardRepo: {refs: r2.length, files: [...new Set(r2.map(r => r.file))]}
})
```

Adapt the type names based on what you found in Step 1.

## Done

You should have enough to fill all 5 sections in 3 calls. Synthesize and respond.

If a module looks unfamiliar, you may run ONE more call: `context("filename.fs")` on the most interesting file. But don't explore more than 4 calls total.
