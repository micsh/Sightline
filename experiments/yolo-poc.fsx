// ════════════════════════════════════════════════════════════════════
// YOLO: You Only Look Once — expand/collapse proof of concept
// ════════════════════════════════════════════════════════════════════
//
// Tests: can a model answer a cross-cutting question about 5 files
// while only ever seeing ONE file at a time?
//
// Protocol:
//   1. Model receives a question that requires understanding all 5 files
//   2. Model has an expand(file) tool — injects file content into context
//   3. After each expand, model takes notes, then we collapse (remove file content)
//   4. Model can re-expand any file if needed
//   5. Model synthesizes final answer from its notes alone
//
// The rule: only one file expanded at a time. All reasoning/notes persist.
//

open System
open System.IO
open System.Net.Http
open System.Text
open System.Text.Json

let llmUrl = "http://127.0.0.1:8090/v1/chat/completions"
let llmModel = "google/gemma-4-26b-a4b"

let repo = @"C:\Users\moshalal\source\repos\AITeam.Platform\src"
let fileMap = dict [
    "Orchestrator", @"AITeam.Orchestration\Orchestrator.fs"
    "MessageRouter", @"AITeam.Orchestration\MessageRouter.fs"
    "DeliveryEngine", @"AITeam.Orchestration\Delivery\DeliveryEngine.fs"
    "BoardSearch", @"AITeam.Search\BoardSearch.fs"
    "AppOrchestrator", @"AITeam.App\Services\AppOrchestrator.cs"
]

// ── Conversation state ──

let mutable messages: (string * string) list = []  // (role, content)
let mutable currentlyExpanded: string option = None

let addMsg role content = messages <- messages @ [(role, content)]

let collapseCurrentFile () =
    match currentlyExpanded with
    | Some name ->
        let marker = sprintf "═══ FILE: %s ═══" name
        messages <- messages |> List.map (fun (role, content) ->
            if content.Contains(marker) then
                (role, sprintf "[File %s was here — now collapsed. Refer to your notes above.]" name)
            else (role, content))
        currentlyExpanded <- None
        eprintfn "  [collapsed %s]" name
    | None -> ()

let expandFile (name: string) =
    collapseCurrentFile()  // collapse any currently expanded file first
    let path = Path.Combine(repo, fileMap.[name])
    let content = File.ReadAllText(path)
    let truncated = if content.Length > 4000 then content.Substring(0, 4000) + "\n// ... truncated" else content
    let wrapped = sprintf "═══ FILE: %s ═══\n%s\n═══ END FILE ═══" name truncated
    addMsg "tool" wrapped
    currentlyExpanded <- Some name
    eprintfn "  [expanded %s — %d chars]" name truncated.Length

// ── LLM ──

let client = new HttpClient(Timeout = TimeSpan.FromSeconds(600.0))

let chat () =
    let reqMsgs = messages |> List.map (fun (role, content) ->
        let d = Collections.Generic.Dictionary<string, obj>()
        d.["role"] <- role
        d.["content"] <- content
        d)

    let tools = [|
        dict [
            "type", box "function"
            "function", box (dict [
                "name", box "expand"
                "description", box (sprintf "Expand a file to read its source code. Available files: %s. Only one file can be expanded at a time — the previous one will be collapsed." (fileMap.Keys |> Seq.toArray |> String.concat ", "))
                "parameters", box (dict [
                    "type", box "object"
                    "properties", box (dict [
                        "file", box (dict [ "type", box "string"; "description", box "File name to expand" ])
                    ])
                    "required", box [| "file" |]
                ])
            ])
        ]
        dict [
            "type", box "function"
            "function", box (dict [
                "name", box "done"
                "description", box "Call this when you have enough information to answer the question. Include your final answer in the notes parameter."
                "parameters", box (dict [
                    "type", box "object"
                    "properties", box (dict [
                        "answer", box (dict [ "type", box "string"; "description", box "Your final answer to the question" ])
                    ])
                    "required", box [| "answer" |]
                ])
            ])
        ]
    |]

    let body = JsonSerializer.Serialize({|
        model = llmModel
        messages = reqMsgs
        tools = tools
        max_tokens = 1500
        temperature = 0.1
    |})

    let content = new StringContent(body, Encoding.UTF8, "application/json")
    let sw = Diagnostics.Stopwatch.StartNew()
    let response = client.PostAsync(llmUrl, content).Result
    let responseBody = response.Content.ReadAsStringAsync().Result
    sw.Stop()
    let doc = JsonDocument.Parse(responseBody)
    let choice = doc.RootElement.GetProperty("choices").[0]
    let msg = choice.GetProperty("message")

    let contentText = match msg.TryGetProperty("content") with true, v when v.ValueKind <> JsonValueKind.Null -> v.GetString() | _ -> ""
    let reasoning = match msg.TryGetProperty("reasoning_content") with true, v when v.ValueKind <> JsonValueKind.Null -> v.GetString() | _ -> ""

    let toolCalls =
        match msg.TryGetProperty("tool_calls") with
        | true, tc when tc.GetArrayLength() > 0 ->
            tc.EnumerateArray() |> Seq.map (fun t ->
                let fn = t.GetProperty("function")
                fn.GetProperty("name").GetString(),
                fn.GetProperty("arguments").GetString()
            ) |> Seq.toList
        | _ -> []

    let finish = choice.GetProperty("finish_reason").GetString()
    sw.Elapsed.TotalSeconds, contentText, reasoning, toolCalls, finish

// ── System prompt ──

let systemPrompt = sprintf """You are analyzing a codebase to answer a specific question. You have 5 source files available but can only look at ONE at a time.

Available files: %s

Your tools:
- expand(file) — opens a file so you can read it. The previously open file gets collapsed.
- done(answer) — call this with your final answer when ready.

IMPORTANT WORKFLOW:
1. Expand a file
2. Read it carefully
3. Write your key observations as notes in your response (these persist after collapse)
4. The file content will be collapsed but your notes remain
5. Expand the next file you need
6. When you have enough information, call done(answer)

Your notes are your memory. Write them clearly — you won't see the file again unless you re-expand it.""" (fileMap.Keys |> Seq.toArray |> String.concat ", ")

addMsg "system" systemPrompt

// ── The question ──

let question = "Trace the complete flow: when a user posts a message, how does it get from the UI entry point through routing and delivery to the target agent? Name the specific functions and types involved at each step."

addMsg "user" question
eprintfn "YOLO Experiment — You Only Look Once"
eprintfn "Question: %s" question
eprintfn "Files: %s" (fileMap.Keys |> Seq.toArray |> String.concat ", ")
eprintfn ""

// ── Main loop ──

let mutable running = true
let mutable turns = 0
let mutable expandCount = 0

while running && turns < 20 do
    turns <- turns + 1
    let elapsed, contentText, reasoning, toolCalls, finish = chat()

    // Show reasoning summary
    if reasoning.Length > 20 then
        let preview = reasoning.Replace("\n", " ")
        eprintfn "Turn %d (%.1fs): [thinking: %s...]" turns elapsed (preview.Substring(0, min 100 preview.Length))

    // Show any text content (notes)
    if contentText <> "" then
        addMsg "assistant" contentText
        printfn ""
        printfn "NOTES (turn %d):" turns
        printfn "%s" contentText
        printfn ""

    // Handle tool calls
    if toolCalls.Length > 0 then
        if contentText = "" then addMsg "assistant" "(tool call)"
        for (name, argsJson) in toolCalls do
            let args = JsonDocument.Parse(argsJson)
            match name with
            | "expand" ->
                let file = args.RootElement.GetProperty("file").GetString()
                if fileMap.ContainsKey(file) then
                    expandCount <- expandCount + 1
                    eprintfn "Turn %d: expand(%s) [#%d]" turns file expandCount
                    expandFile file
                else
                    addMsg "tool" (sprintf "File '%s' not found. Available: %s" file (fileMap.Keys |> Seq.toArray |> String.concat ", "))
                    eprintfn "Turn %d: expand(%s) — NOT FOUND" turns file
            | "done" ->
                let answer = args.RootElement.GetProperty("answer").GetString()
                printfn ""
                printfn "════════════════════════════════════════"
                printfn "FINAL ANSWER:"
                printfn "════════════════════════════════════════"
                printfn "%s" answer
                printfn "════════════════════════════════════════"
                running <- false
            | _ ->
                addMsg "tool" (sprintf "Unknown tool: %s" name)
    elif finish = "stop" && contentText = "" then
        // Model stopped without tool call or content — might need prompting
        addMsg "user" "Continue. Expand the next file you need, or call done(answer) if you have enough."

    if finish = "length" then
        eprintfn "  [hit token limit — continuing]"
        addMsg "user" "Continue from where you left off."

// Collapse any remaining file
collapseCurrentFile()

eprintfn ""
eprintfn "═══ Stats ═══"
eprintfn "  Turns: %d" turns
eprintfn "  Expands: %d" expandCount
eprintfn "  Files available: %d" fileMap.Count
eprintfn "  Messages in history: %d" messages.Length
let totalChars = messages |> List.sumBy (fun (_, c) -> c.Length)
eprintfn "  Final context: ~%d tokens" (totalChars / 4)
