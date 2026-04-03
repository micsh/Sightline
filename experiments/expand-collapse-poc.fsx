// ════════════════════════════════════════════════════════════════════
// Expand/Collapse proof of concept — minimal context management
// ════════════════════════════════════════════════════════════════════
//
// Reads 5 source files sequentially. For each file:
//   1. EXPAND: inject file content into conversation
//   2. LLM summarizes the file in ~50 words
//   3. COLLAPSE: replace file content with the summary
//   4. Move to next file
//
// At the end, LLM has seen all 5 files but context only holds summaries.
// Budget: never exceed 4000 tokens despite processing ~12K total.
//

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json

let llmUrl = "http://127.0.0.1:8090/v1/chat/completions"
let llmModel = "google/gemma-4-26b-a4b"
let budgetLimit = 4000

// ── Files to process ──

let repo = @"C:\Users\moshalal\source\repos\AITeam.Platform\src"
let files = [|
    @"AITeam.Orchestration\Orchestrator.fs"
    @"AITeam.Orchestration\MessageRouter.fs"
    @"AITeam.Orchestration\Delivery\DeliveryEngine.fs"
    @"AITeam.Search\BoardSearch.fs"
    @"AITeam.App\Services\AppOrchestrator.cs"
|]

// ── Context management ──

type Msg = { Role: string; Content: string; mutable Tokens: int }

let estimateTokens (s: string) = max 1 (s.Length / 4)

let mutable messages: Msg list = []
let mutable totalTokens = 0
let mutable peakTokens = 0

let addMsg role content =
    let t = estimateTokens content
    messages <- messages @ [{ Role = role; Content = content; Tokens = t }]
    totalTokens <- totalTokens + t
    peakTokens <- max peakTokens totalTokens

let replaceLastUserMsg newContent =
    // Replace the most recent user message (the file content) with collapsed version
    let newT = estimateTokens newContent
    match messages |> List.tryFindIndexBack (fun m -> m.Role = "user") with
    | Some idx ->
        let old = messages.[idx]
        let saved = old.Tokens - newT
        messages <- messages |> List.mapi (fun i m ->
            if i = idx then { m with Content = newContent; Tokens = newT }
            else m)
        totalTokens <- totalTokens - saved
        saved
    | None -> 0

// ── LLM call ──

let client = new HttpClient(Timeout = TimeSpan.FromSeconds(300.0))

let chat () =
    let reqMsgs = messages |> List.map (fun m ->
        let d = Collections.Generic.Dictionary<string, obj>()
        d.["role"] <- m.Role
        d.["content"] <- m.Content
        d)
    let body = JsonSerializer.Serialize({|
        model = llmModel
        messages = reqMsgs
        max_tokens = 1000
        temperature = 0.1
    |})
    let content = new StringContent(body, Encoding.UTF8, "application/json")
    let response = client.PostAsync(llmUrl, content).Result
    let responseBody = response.Content.ReadAsStringAsync().Result
    let doc = JsonDocument.Parse(responseBody)
    let choice = doc.RootElement.GetProperty("choices").[0]
    let msg = choice.GetProperty("message")
    let text = match msg.TryGetProperty("content") with true, v when v.ValueKind <> JsonValueKind.Null -> v.GetString() | _ -> ""
    let reasoning = match msg.TryGetProperty("reasoning_content") with true, v when v.ValueKind <> JsonValueKind.Null -> v.GetString() | _ -> ""
    // If content is empty but reasoning has the answer, extract it
    let finalText =
        if text <> "" then text
        elif reasoning.Length > 50 then
            // Take the last paragraph of reasoning as the likely answer
            let paragraphs = reasoning.Split("\n\n") |> Array.filter (fun p -> p.Trim().Length > 20)
            if paragraphs.Length > 0 then paragraphs.[paragraphs.Length - 1].Trim()
            else reasoning.Substring(max 0 (reasoning.Length - 300)).Trim()
        else "(no response)"
    let usage = doc.RootElement.GetProperty("usage")
    let promptTokens = usage.GetProperty("prompt_tokens").GetInt32()
    let completionTokens = usage.GetProperty("completion_tokens").GetInt32()
    finalText, reasoning, promptTokens, completionTokens

// ── System prompt ──

addMsg "system" "You are a code analyst. When given a source file, summarize it in exactly 2 sentences: what it does and what its key types/functions are. Be specific with names."

// ── Process files ──

printfn "Expand/Collapse PoC — processing %d files with %d token budget" files.Length budgetLimit
printfn ""

for i in 0..files.Length-1 do
    let filePath = Path.Combine(repo, files.[i])
    let fileName = Path.GetFileName(filePath)
    let content = File.ReadAllText(filePath)
    let fileTokens = estimateTokens content

    // Truncate if single file is too large
    let truncated = if content.Length > 8000 then content.Substring(0, 8000) + "\n// ... truncated" else content
    let truncTokens = estimateTokens truncated

    printfn "── File %d/%d: %s (%d tokens) ──" (i+1) files.Length fileName truncTokens

    // EXPAND: inject file into conversation
    addMsg "user" (sprintf "Summarize this file:\n\n```\n%s\n```" truncated)
    printfn "  EXPAND: context now %d tokens (budget: %d)" totalTokens budgetLimit

    // Get summary from LLM
    let sw = Diagnostics.Stopwatch.StartNew()
    let summary, reasoning, promptTok, completionTok = chat()
    sw.Stop()
    printfn "  LLM: %.1fs (%d prompt, %d completion tokens)" sw.Elapsed.TotalSeconds promptTok completionTok
    if reasoning.Length > 0 then
        printfn "  [thinking: %s...]" (reasoning.Replace("\n", " ").Substring(0, min 60 reasoning.Length))

    addMsg "assistant" summary
    printfn "  Summary: %s" (if summary.Length > 120 then summary.Substring(0, 120) + "..." else summary)

    // COLLAPSE: replace file content with summary
    let saved = replaceLastUserMsg (sprintf "[File: %s] %s" fileName summary)
    printfn "  COLLAPSE: saved %d tokens, context now %d tokens" saved totalTokens
    printfn ""

    if totalTokens > budgetLimit then
        printfn "  ⚠ OVER BUDGET: %d > %d" totalTokens budgetLimit

// ── Final: ask LLM what it learned ──

printfn "── Final: asking LLM to synthesize all 5 files ──"
addMsg "user" "Based on all the files you've summarized, what is this system about? How do the 5 files relate to each other? Answer in 3-4 sentences."
printfn "  Context: %d tokens" totalTokens

let sw = Diagnostics.Stopwatch.StartNew()
let synthesis, _, promptTok, completionTok = chat()
sw.Stop()
printfn "  LLM: %.1fs" sw.Elapsed.TotalSeconds
printfn ""
printfn "  %s" synthesis
printfn ""

printfn "═══ Results ═══"
printfn "  Files processed: %d" files.Length
printfn "  Total file tokens: %d" (files |> Array.sumBy (fun f -> estimateTokens (File.ReadAllText(Path.Combine(repo, f)))))
printfn "  Peak context: %d tokens" peakTokens
printfn "  Final context: %d tokens" totalTokens
printfn "  Budget limit: %d tokens" budgetLimit
printfn "  Under budget: %b" (peakTokens <= budgetLimit)
