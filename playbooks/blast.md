# Playbook: Blast

Produce an impact assessment for changing a type, function, or module.

## Output (exactly this structure)

1. **Blast radius** — N files, M direct references
2. **How it's used** — group by usage pattern, quote matchLines
3. **Risk zones** — most fragile references
4. **Safe changes** — what won't break callers
5. **Related types** — anything similar that might need the same change

## Step 1: Find definition + all references (one call)

```js
let def = search("ChecklistTracker", {limit:1});
let r = refs("ChecklistTracker", {limit:30});
let byFile = {};
r.forEach(x => { if (!byFile[x.file]) byFile[x.file] = []; byFile[x.file].push(x.matchLine); });
({
  definedIn: def[0] ? def[0].file + ":" + def[0].line : "?",
  totalRefs: r.length,
  fileCount: Object.keys(byFile).length,
  byFile: byFile
})
```

Replace "ChecklistTracker" with the actual name from the user's question.

## Step 2: Module-level impact (one call)

```js
let d = deps("ChecklistTracker");
let imp = impact("ChecklistTracker");
({
  moduleDependers: d,
  typeImpactFiles: imp.map(i => i.file)
})
```

## Done

2 calls. The matchLines from Step 1 tell you HOW it's used — categorize them into constructor calls, type annotations, method calls. Synthesize.

If a specific reference looks risky, ONE expand() call to see full context.
