# Playbook: Review

Produce an architecture and code quality review.

## Output (exactly this structure)

1. **Architecture** — module structure, dependency direction
2. **Backbone types** — 3-5 most referenced types
3. **Duplication** — repeated patterns that should be extracted
4. **Complexity hotspots** — largest files
5. **Verdict** — one sentence

## Step 1: Structure + backbone (one call)

```js
let m = modules();
let r1 = refs("CodeIndex", {limit:30});
let r2 = refs("CodeChunk", {limit:30});
({
  modules: m.map(x => ({name: x.module, files: x.files, chunks: x.chunks})),
  backbone: {CodeIndex: r1.length, CodeChunk: r2.length}
})
```

Adapt type names to the actual codebase.

## Step 2: Duplication + complexity (one call)

```js
let dup = grep("Array.tryFind.*FilePath.*Name.*StartLine", {limit:10});
let big = files("").sort((a,b) => b.chunks - a.chunks).slice(0,5);
({
  duplicatedPatterns: dup.map(d => ({name: d.name, file: d.file, matchLine: d.matchLine})),
  largestFiles: big.map(f => ({file: f.file, chunks: f.chunks}))
})
```

Adapt the grep pattern to look for repeated boilerplate in the codebase.

## Done

2 calls. Assess and respond.
