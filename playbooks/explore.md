# Playbook: Explore

Answer any question about the codebase. No fixed structure.

## Quick reference — pick the right queries

| Question | One-call solution |
|----------|-------------------|
| "What does X do?" | `search("X", {limit:3})` — read the summaries |
| "Where is X?" | `grep("X", {limit:5})` |
| "Who uses X?" | `refs("X", {limit:15})` — matchLines show how |
| "What depends on X?" | `({deps: deps("X"), impact: impact("X")})` |
| "Show me the code" | `expand("R1")` after a search |
| "How do I add X?" | Use the Plan playbook instead |

## Combining queries

```js
// Get everything about a type in one call
let r = refs("MyType", {limit:20});
let d = deps("MyType");
({
  refs: r.length,
  usedIn: [...new Set(r.map(x => x.file))],
  dependers: d,
  topUsages: r.slice(0,5).map(x => x.file + ": " + x.matchLine)
})
```

Keep it to 3-5 calls max. Synthesize and respond.
