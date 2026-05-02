open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.RegularExpressions
open AITeam.Sight.Core
open AITeam.CodeSight

let printUsage () =
    printfn "AITeam.CodeSight — code intelligence for any codebase"
    printfn ""
    printfn "Usage:"
    printfn "  code-sight index [--no-embed] [--repo <path>]       Build/update index"
    printfn "  code-sight index --files <f1> <f2> ... [--no-embed] [--repo <path>] Re-index specific files"
    printfn "  code-sight preflight [--files <f1> <f2> ...] [--repo <path>] Inspect index scope/scale without mutating artifacts"
    printfn "  code-sight history [--repo <path>]                  Show recent indexing invocation history"
    printfn "  code-sight compare [--repo <path>]                  Compare the latest invocation with the previous retained run"
    printfn "  code-sight modules [--repo <path>]                   Show project map"
    printfn "  code-sight search <js> [--json] [--repo <path>] [--scope <s>] [--peer <ks-path>] Run a query"
    printfn "  code-sight eval <js> [--json] [--repo <path>]                 Alias for search"
    printfn "  code-sight eval - [--json] [--repo <path>]                    Read expression from stdin"
    printfn "  code-sight intel <question> [--repo <path>]          Ask about the codebase"
    printfn "  code-sight repl [--repo <path>] [--scope <s>]        Interactive mode"
    printfn "  code-sight scopes [--repo <path>]                    List available scopes"
    printfn "  code-sight status [--repo <path>]                    Show latest indexing invocation report"
    printfn "  code-sight fn add <name> <body> [options]            Define a reusable function"
    printfn "  code-sight fn list [--verbose] [--json]              List saved functions"
    printfn "  code-sight fn rm <name> [--repo <path>]              Remove a function"
    printfn "  code-sight --help                                    Show this help"
    printfn ""
    printfn "Functions:"
    printfn "  Save chains of primitives as reusable functions. Example:"
    printfn "    code-sight fn add deepSearch --params \"q\" \"search(q).concat(similar(search(q)[0]))\""
    printfn "    code-sight search \"deepSearch('auth')\""
    printfn ""
    printfn "  Options for 'fn add':"
    printfn "    --params \"a,b\"          Comma-separated parameter names"
    printfn "    --desc \"description\"    Optional description"
    printfn "    --file <path>           Read function body from a file"
    printfn ""
    printfn "  Options for 'index':"
    printfn "    --no-embed              Build a structural-only index (semantic search disabled)"
    printfn ""
    printfn "Primitives (available in search expressions):"
    printfn "  search(query, {limit, kind, file})  Semantic search across indexed chunks"
    printfn "  modules()                           Project map — directories, symbols, imports"
    printfn "  context(file)                       File outline with types, functions, imports"
    printfn "  expand(refId)                       Expand R# ref to full source content"
    printfn "  neighborhood(refId, {before, after}) Surrounding symbols in the same file"
    printfn "  similar(refId, {limit})              Semantically similar chunks"
    printfn "  grep(pattern, {limit, kind, file})   Regex search over source content"
    printfn "  files(pattern)                       List indexed source files"
    printfn "  impact(type)                         Find all references to a type"
    printfn "  imports(file)                        Show what a file imports"
    printfn "  deps(pattern)                        Show dependency graph for a module"
    printfn "  refs(name, {limit})                  Find all references to a symbol"
    printfn "  callers(name, {limit})               Find call sites of a qualified name"
    printfn "  walk(name, {depth, limit})           Walk the dependency graph from a symbol"
    printfn "  changed(gitRef)                      Chunks in files changed since a git ref"
    printfn "  hotspots({by, min})                  Structural metrics per file (chunks/loc/fanIn/fanOut)"
    printfn "  explain(refId)                       Debug: show index metadata for a ref"
    printfn "  saveSession(name)                    Save current ref session as a named snapshot"
    printfn "  loadSession(name)                    Load a previously saved ref session"
    printfn "  sessions()                           List saved sessions"
    printfn "  trace(from, to)                      BFS shortest path between two files"
    printfn "  arch(file)                           Architectural context from arch tool"
    printfn ""
    printfn "Bridge primitives (active when .knowledge-sight/ index exists):"
    printfn "  drift()                              KS sections referencing missing CS symbols"
    printfn "  coverage({minFanIn})                 Important CS files with no KS documentation"
    printfn ""
    printfn "Composition helpers:"
    printfn "  pipe(value, fn1, fn2, ...)           Thread value through functions"
    printfn "  tap(value, fn)                       Run fn for side-effects, return value"
    printfn "  mergeBy(key, arr1, arr2, ...)        Union arrays with dedup by key"
    printfn "  print(value)                         Debug output to stderr"
    printfn ""
    printfn "Note: UDFs are available in search/eval and intel. Intel auto-discovers UDFs."
    printfn ""

let parseArgs (args: string[]) =
    let mutable repo = Environment.CurrentDirectory
    let mutable command = ""
    let mutable query = ""
    let mutable scope = ""
    let mutable fnName = ""
    let mutable fnParams = ""
    let mutable fnDesc = ""
    let mutable fnFile = ""
    let mutable verbose = false
    let mutable jsonOut = false
    let mutable peerPath = ""
    let mutable noEmbed = false
    let files = ResizeArray<string>()
    let mutable i = 0
    while i < args.Length do
        match args.[i] with
        | "--repo" when i + 1 < args.Length ->
            repo <- args.[i + 1]
            i <- i + 2
        | "--peer" when i + 1 < args.Length ->
            peerPath <- args.[i + 1]
            i <- i + 2
        | "--scope" when i + 1 < args.Length ->
            scope <- args.[i + 1]
            i <- i + 2
        | "--help" | "-h" ->
            command <- "help"
            i <- i + 1
        | "--files" ->
            i <- i + 1
            while i < args.Length && not (args.[i].StartsWith("--")) do
                files.Add(args.[i])
                i <- i + 1
        | "--no-embed" ->
            noEmbed <- true
            i <- i + 1
        | "index" | "preflight" | "history" | "compare" | "modules" | "repl" | "scopes" | "status" ->
            command <- args.[i]
            i <- i + 1
        | "fn" when i + 1 < args.Length ->
            match args.[i + 1] with
            | "add" when i + 2 < args.Length ->
                command <- "fn-add"
                fnName <- args.[i + 2]
                i <- i + 3
                while i < args.Length do
                    match args.[i] with
                    | "--params" when i + 1 < args.Length ->
                        fnParams <- args.[i + 1]
                        i <- i + 2
                    | "--desc" when i + 1 < args.Length ->
                        fnDesc <- args.[i + 1]
                        i <- i + 2
                    | "--file" when i + 1 < args.Length ->
                        fnFile <- args.[i + 1]
                        i <- i + 2
                    | "--repo" when i + 1 < args.Length ->
                        repo <- args.[i + 1]
                        i <- i + 2
                    | _ when query = "" ->
                        query <- args.[i]
                        i <- i + 1
                    | _ -> i <- i + 1
            | "list" ->
                command <- "fn-list"
                i <- i + 2
                while i < args.Length do
                    match args.[i] with
                    | "--verbose" | "-v" -> verbose <- true; i <- i + 1
                    | "--json" -> jsonOut <- true; i <- i + 1
                    | "--repo" when i + 1 < args.Length -> repo <- args.[i + 1]; i <- i + 2
                    | _ -> i <- i + 1
            | "rm" when i + 2 < args.Length ->
                command <- "fn-rm"
                fnName <- args.[i + 2]
                i <- i + 3
            | _ ->
                command <- "fn-list"
                i <- i + 2
        | "intel" when i + 1 < args.Length ->
            command <- "intel"
            query <- args.[i + 1]
            i <- i + 2
        | "intel" ->
            command <- "intel"
            i <- i + 1
        | "search" | "eval" when i + 1 < args.Length ->
            command <- "search"
            query <- args.[i + 1]
            i <- i + 2
            while i < args.Length do
                match args.[i] with
                | "--json" -> jsonOut <- true; i <- i + 1
                | "--repo" when i + 1 < args.Length -> repo <- args.[i + 1]; i <- i + 2
                | "--scope" when i + 1 < args.Length -> scope <- args.[i + 1]; i <- i + 2
                | "--peer" when i + 1 < args.Length -> peerPath <- args.[i + 1]; i <- i + 2
                | _ -> i <- i + 1
        | "search" | "eval" ->
            command <- "search"
            i <- i + 1
        | arg when command = "" ->
            command <- "search"
            query <- arg
            i <- i + 1
        | _ -> i <- i + 1
    repo, command, query, scope, files.ToArray(), fnName, fnParams, fnDesc, fnFile, verbose, jsonOut, peerPath, noEmbed

/// Ensure index dir exists and has a .gitignore so repos don't need to add it.
let private ensureIndexDir (dir: string) =
    Directory.CreateDirectory(dir) |> ignore
    let gi = Path.Combine(dir, ".gitignore")
    if not (File.Exists gi) then File.WriteAllText(gi, "# Auto-generated by code-sight — keep index out of source control\n*\n!.gitignore\n")

let private resolveParsersDir (repoRoot: string) =
    let repoOverride = Path.Combine(repoRoot, ".code-intel", "parsers")
    if Directory.Exists repoOverride && File.Exists(Path.Combine(repoOverride, "ts-chunker.js")) then
        repoOverride
    else
        let exeDir = AppDomain.CurrentDomain.BaseDirectory
        let exeParsers = Path.Combine(exeDir, "parsers")
        if Directory.Exists exeParsers then exeParsers else exeDir

let private loadConfig (repoRoot: string) =
    let runtime = { ParsersDir = resolveParsersDir repoRoot }
    Config.load repoRoot runtime

type private RequestedFileResolution = {
    RequestedPath: string
    AbsolutePath: string
    RelativePath: string
    Exists: bool
}

let private toRepoRelativePath (repoRoot: string) (path: string) =
    Path.GetRelativePath(repoRoot, path).Replace("\\", "/")

let private resolveRequestedFiles (repoRoot: string) (files: string[]) =
    files
    |> Array.map (fun filePath ->
        let absolutePath =
            if Path.IsPathRooted filePath then filePath
            else Path.GetFullPath(Path.Combine(repoRoot, filePath))

        {
            RequestedPath = filePath
            AbsolutePath = absolutePath
            RelativePath = toRepoRelativePath repoRoot absolutePath
            Exists = File.Exists absolutePath
        })

let private fullIndexConfig (cfg: CodeSightConfig) =
    let allDirs = cfg.Scopes |> Array.collect (fun scope -> scope.Dirs) |> Array.distinct
    { cfg with SrcDirs = allDirs }

let private topDirectoryBreakdown (relativePaths: string[]) =
    relativePaths
    |> Array.countBy (fun relPath ->
        let segments = relPath.Replace("\\", "/").Split('/', StringSplitOptions.RemoveEmptyEntries)
        if segments.Length <= 1 then "."
        elif segments.Length = 2 then segments.[0]
        else sprintf "%s/%s" segments.[0] segments.[1])
    |> Array.sortByDescending snd
    |> Array.truncate 8

let private printBreakdown heading (items: (string * int)[]) =
    if items.Length > 0 then
        printfn "%s" heading
        for name, count in items do
            printfn "  %-24s %d" name count

let private runPreflight (cfg: CodeSightConfig) (files: string[]) =
    printfn "Preflight mode: read-only (no .code-intel artifacts will be created or modified)"
    printfn "Index directory: %s" cfg.IndexDir
    printfn "Exclude semantics: bare entries match path segments; wildcard entries use glob-style repo-relative matching; custom excludes append to built-in defaults."
    printfn "Discovery-driven index/preflight walks prune excluded subtrees before descending into them."

    if files.Length > 0 then
        let resolutions = resolveRequestedFiles cfg.RepoRoot files
        let resolvedFiles = resolutions |> Array.filter (fun resolution -> resolution.Exists)
        let missingFiles = resolutions |> Array.filter (fun resolution -> not resolution.Exists)

        printfn "Invocation kind: partial"
        printfn "Requested files: %d" resolutions.Length
        printfn "Resolved files: %d" resolvedFiles.Length
        printfn "Missing files: %d" missingFiles.Length

        if resolvedFiles.Length > 0 then
            let resolvedRelativePaths = resolvedFiles |> Array.map (fun resolution -> resolution.RelativePath)
            printBreakdown "Top directories in resolved file set:" (topDirectoryBreakdown resolvedRelativePaths)

            printfn "Resolved file preview:"
            for resolution in resolvedFiles |> Array.truncate 10 do
                printfn "  %s" resolution.RelativePath

            if resolvedFiles.Length > 10 then
                printfn "  ... %d more" (resolvedFiles.Length - 10)

            printfn "Would fail current index run: no"
        else
            printfn "Would fail current index run: yes — `index --files` would exit with `No valid files to index.`"

        if missingFiles.Length > 0 then
            printfn "Missing file preview:"
            for resolution in missingFiles |> Array.truncate 10 do
                printfn "  %s" resolution.RequestedPath

            if missingFiles.Length > 10 then
                printfn "  ... %d more" (missingFiles.Length - 10)

        printfn "Partial preflight uses direct file existence resolution; discovery excludes do not filter explicit `--files` inputs."
        0
    else
        let cfgAll = fullIndexConfig cfg
        let candidateFiles = TreeSitterChunker.findSourceFiles cfgAll
        let relativePaths = candidateFiles |> Array.map (toRepoRelativePath cfg.RepoRoot)

        printfn "Invocation kind: full"
        printfn "Resolved source roots: %s" (cfgAll.SrcDirs |> String.concat ", ")
        printfn "Candidate source files: %d" relativePaths.Length
        printBreakdown "Top directories by candidate file count:" (topDirectoryBreakdown relativePaths)
        0

type private EmbeddingPass = {
    Rows: float32[][]
    FailedBatches: int
    FirstFailure: string option
    TotalBatches: int
    Elapsed: TimeSpan
}

type private SemanticEmbeddingPass = {
    CodeEmbeddings: float32[][]
    SummaryEmbeddings: float32[][]
    EmbeddingDim: int
    CodeBatchCount: int
    CodeFailedBatches: int
    SummaryBatchCount: int
    SummaryFailedBatches: int
    FailedBatches: int
    SemanticState: string
    SemanticMessage: string
    FirstFailure: string option
    SemanticWorkPerformed: bool
    SemanticWorkElapsed: TimeSpan
}

type private LatestInvocationReport = {
    InvocationKind: string
    CompletedAtUtc: DateTime
    InvocationMessage: string
    SemanticWorkPerformed: bool
    SemanticWorkDurationMs: int64
    CodeBatchCount: int
    CodeFailedBatches: int
    SummaryBatchCount: int
    SummaryFailedBatches: int
    SemanticState: string
    SemanticMessage: string
    FirstFailure: string
    ComparisonFacts: InvocationComparisonFactsState
}

and private InvocationComparisonFacts = {
    ChunkCount: int
    ImportCount: int
    SignatureCount: int
}

and private InvocationComparisonFactsState =
    | Available of InvocationComparisonFacts
    | Unavailable
    | Legacy

let private emptyEmbeddings count = Array.init count (fun _ -> [||])

let private firstNonEmptyDim (embeddings: float32[][]) =
    embeddings
    |> Array.tryPick (fun emb -> if emb.Length > 0 then Some emb.Length else None)
    |> Option.defaultValue 0

let private formatElapsed (elapsed: TimeSpan) =
    if elapsed.TotalMilliseconds < 1000.0 then
        sprintf "%.0fms" elapsed.TotalMilliseconds
    elif elapsed.TotalSeconds < 10.0 then
        sprintf "%.1fs" elapsed.TotalSeconds
    else
        sprintf "%.0fs" elapsed.TotalSeconds

let private mergeFirstFailure first second =
    match first with
    | Some _ -> first
    | None -> second

let private embeddingTimeout (cfg: CodeSightConfig) =
    TimeSpan.FromSeconds(float cfg.EmbeddingTimeoutSeconds)

let private buildChunkContentLookup (sourceChunks: CodeChunk[]) =
    sourceChunks
    |> Array.map (fun ch -> (ch.FilePath, ch.Name, ch.StartLine), ch.Content)
    |> dict

let private latestInvocationReportPath (indexDir: string) =
    Path.Combine(indexDir, "latest-invocation.json")

let private invocationHistoryPath (indexDir: string) =
    Path.Combine(indexDir, "invocation-history.json")

let private maxInvocationHistoryEntries = 10

type private InvocationHistoryLoadResult =
    | Missing
    | Loaded of LatestInvocationReport[]
    | Corrupt of string

let private invocationReportPayload (report: LatestInvocationReport) =
    let payload = System.Collections.Generic.Dictionary<string, obj>()
    payload["InvocationKind"] <- box report.InvocationKind
    payload["CompletedAtUtc"] <- box report.CompletedAtUtc
    payload["InvocationMessage"] <- box report.InvocationMessage
    payload["SemanticWorkPerformed"] <- box report.SemanticWorkPerformed
    payload["SemanticWorkDurationMs"] <- box report.SemanticWorkDurationMs
    payload["CodeBatchCount"] <- box report.CodeBatchCount
    payload["CodeFailedBatches"] <- box report.CodeFailedBatches
    payload["SummaryBatchCount"] <- box report.SummaryBatchCount
    payload["SummaryFailedBatches"] <- box report.SummaryFailedBatches
    payload["SemanticState"] <- box report.SemanticState
    payload["SemanticMessage"] <- box report.SemanticMessage
    payload["FirstFailure"] <- box report.FirstFailure
    match report.ComparisonFacts with
    | Available facts ->
        payload["ComparisonFactsStatus"] <- box "available"
        payload["ChunkCount"] <- box facts.ChunkCount
        payload["ImportCount"] <- box facts.ImportCount
        payload["SignatureCount"] <- box facts.SignatureCount
    | Unavailable ->
        payload["ComparisonFactsStatus"] <- box "unavailable"
    | Legacy ->
        ()
    payload

let private tryGetJsonProperty (name: string) (root: JsonElement) =
    match root.TryGetProperty(name) with
    | true, value -> Some value
    | _ -> None

let private parseInvocationReportElement (root: JsonElement) =
    let getString (name: string) =
        match tryGetJsonProperty name root with
        | Some value ->
            let text = value.GetString()
            if isNull text then "" else text
        | None -> ""
    let getBool (name: string) =
        tryGetJsonProperty name root
        |> Option.map (fun value -> value.GetBoolean())
        |> Option.defaultValue false
    let getInt64 (name: string) =
        tryGetJsonProperty name root
        |> Option.map (fun value -> value.GetInt64())
        |> Option.defaultValue 0L
    let getInt (name: string) =
        tryGetJsonProperty name root
        |> Option.map (fun value -> value.GetInt32())
        |> Option.defaultValue 0
    let getOptionalInt (name: string) =
        match tryGetJsonProperty name root with
        | Some value when value.ValueKind = JsonValueKind.Number -> Some (value.GetInt32())
        | _ -> None
    let comparisonFacts =
        let chunkCount = getOptionalInt "ChunkCount"
        let importCount = getOptionalInt "ImportCount"
        let signatureCount = getOptionalInt "SignatureCount"
        let status =
            match tryGetJsonProperty "ComparisonFactsStatus" root with
            | Some value when value.ValueKind = JsonValueKind.String ->
                let text = value.GetString()
                if isNull text then "" else text.Trim().ToLowerInvariant()
            | _ -> ""
        match status with
        | "available" ->
            match chunkCount, importCount, signatureCount with
            | Some chunks, Some imports, Some signatures ->
                Available { ChunkCount = chunks; ImportCount = imports; SignatureCount = signatures }
            | _ ->
                Unavailable
        | "unavailable" ->
            Unavailable
        | "legacy" ->
            Legacy
        | _ when chunkCount.IsSome || importCount.IsSome || signatureCount.IsSome ->
            Unavailable
        | _ ->
            Legacy
    let completedAtUtc =
        match tryGetJsonProperty "CompletedAtUtc" root with
        | Some value ->
            let text = value.GetString()
            match DateTime.TryParse(text) with
            | true, parsed -> parsed
            | _ -> DateTime.MinValue
        | None -> DateTime.MinValue
    {
        InvocationKind = getString "InvocationKind"
        CompletedAtUtc = completedAtUtc
        InvocationMessage = getString "InvocationMessage"
        SemanticWorkPerformed = getBool "SemanticWorkPerformed"
        SemanticWorkDurationMs = getInt64 "SemanticWorkDurationMs"
        CodeBatchCount = getInt "CodeBatchCount"
        CodeFailedBatches = getInt "CodeFailedBatches"
        SummaryBatchCount = getInt "SummaryBatchCount"
        SummaryFailedBatches = getInt "SummaryFailedBatches"
        SemanticState = getString "SemanticState"
        SemanticMessage = getString "SemanticMessage"
        FirstFailure = getString "FirstFailure"
        ComparisonFacts = comparisonFacts
    }

let private saveLatestInvocationReport (indexDir: string) (report: LatestInvocationReport) =
    Directory.CreateDirectory(indexDir) |> ignore
    let options = JsonSerializerOptions(WriteIndented = true)
    File.WriteAllText(latestInvocationReportPath indexDir, JsonSerializer.Serialize(invocationReportPayload report, options))

let private loadLatestInvocationReport (indexDir: string) =
    let path = latestInvocationReportPath indexDir
    if not (File.Exists path) then None
    else
        try
            use doc = JsonDocument.Parse(File.ReadAllText(path))
            Some (parseInvocationReportElement doc.RootElement)
        with _ ->
            None

let private loadInvocationHistory (indexDir: string) =
    let path = invocationHistoryPath indexDir
    if not (File.Exists path) then Missing
    else
        try
            use doc = JsonDocument.Parse(File.ReadAllText(path))
            match doc.RootElement.ValueKind with
            | JsonValueKind.Array ->
                doc.RootElement.EnumerateArray()
                |> Seq.map parseInvocationReportElement
                |> Seq.toArray
                |> Loaded
            | _ ->
                Corrupt "Invocation history file is not a JSON array."
        with ex ->
            Corrupt ex.Message

let private saveInvocationHistory (indexDir: string) (reports: LatestInvocationReport[]) =
    Directory.CreateDirectory(indexDir) |> ignore
    let options = JsonSerializerOptions(WriteIndented = true)
    let payload = reports |> Array.map invocationReportPayload
    File.WriteAllText(invocationHistoryPath indexDir, JsonSerializer.Serialize(payload, options))

let private quarantineCorruptInvocationHistory (indexDir: string) =
    let path = invocationHistoryPath indexDir
    if not (File.Exists path) then
        Error "Invocation history file was missing before quarantine."
    else
        try
            let timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")
            let fileName = sprintf "invocation-history.corrupt-%s.json" timestamp
            let quarantinePath = Path.Combine(indexDir, fileName)
            File.Move(path, quarantinePath)
            Ok quarantinePath
        with ex ->
            Error ex.Message

let private appendInvocationHistory (indexDir: string) (report: LatestInvocationReport) =
    match loadInvocationHistory indexDir with
    | Missing ->
        saveInvocationHistory indexDir [| report |]
        None
    | Loaded existingHistory ->
        let boundedHistory =
            Array.append existingHistory [| report |]
            |> Array.rev
            |> Array.truncate maxInvocationHistoryEntries
            |> Array.rev
        saveInvocationHistory indexDir boundedHistory
        None
    | Corrupt reason ->
        match quarantineCorruptInvocationHistory indexDir with
        | Ok quarantinePath ->
            saveInvocationHistory indexDir [| report |]
            Some (sprintf "Invocation history was unreadable and has been preserved at %s before starting a new bounded history. Details: %s" quarantinePath reason)
        | Error quarantineError ->
            Some (sprintf "Invocation history is unreadable and could not be preserved for rewrite (%s). Leaving the existing file untouched. Details: %s" quarantineError reason)

let private latestInvocationReport
    invocationKind
    invocationMessage
    semanticWorkPerformed
    (semanticWorkElapsed: TimeSpan)
    codeBatchCount
    codeFailedBatches
    summaryBatchCount
    summaryFailedBatches
    semanticState
    semanticMessage
    firstFailure
    comparisonFacts =
    {
        InvocationKind = invocationKind
        CompletedAtUtc = DateTime.UtcNow
        InvocationMessage = invocationMessage
        SemanticWorkPerformed = semanticWorkPerformed
        SemanticWorkDurationMs = int64 semanticWorkElapsed.TotalMilliseconds
        CodeBatchCount = codeBatchCount
        CodeFailedBatches = codeFailedBatches
        SummaryBatchCount = summaryBatchCount
        SummaryFailedBatches = summaryFailedBatches
        SemanticState = semanticState
        SemanticMessage = semanticMessage
        FirstFailure = defaultArg firstFailure ""
        ComparisonFacts = comparisonFacts
    }

let private signatureCountFromChunks (chunks: ChunkEntry[]) =
    chunks |> Array.sumBy (fun chunk -> if String.IsNullOrWhiteSpace chunk.Signature then 0 else 1)

let private availableComparisonFacts chunkCount importCount signatureCount =
    Available {
        ChunkCount = chunkCount
        ImportCount = importCount
        SignatureCount = signatureCount
    }

let private currentIndexComparisonFacts (index: CodeIndex) =
    availableComparisonFacts index.Chunks.Length index.Imports.Length (signatureCountFromChunks index.Chunks)

let private completedInvocationComparisonFacts (chunks: ChunkEntry[]) (imports: (string * string)[]) signatureCount =
    availableComparisonFacts chunks.Length imports.Length signatureCount

let private renderComparisonFactsSummary comparisonFacts =
    match comparisonFacts with
    | Available facts ->
        Some (sprintf "Structural output: %d chunks, %d imports, %d signatures" facts.ChunkCount facts.ImportCount facts.SignatureCount)
    | Unavailable ->
        Some "Structural output: unavailable for this invocation."
    | Legacy ->
        Some "Structural output: unavailable on this legacy retained entry (captured before comparison facts existed)."

let private renderInvocationReport (heading: string) (report: LatestInvocationReport) =
    let lines = ResizeArray<string>()
    lines.Add(sprintf "%s: %s" heading report.InvocationKind)
    if report.CompletedAtUtc <> DateTime.MinValue then
        lines.Add(sprintf "Completed at (UTC): %s" (report.CompletedAtUtc.ToUniversalTime().ToString("O")))
    lines.Add(sprintf "Semantic work: %s" (if report.SemanticWorkPerformed then "performed" else "not performed"))
    if report.InvocationMessage <> "" then
        lines.Add(sprintf "Invocation note: %s" report.InvocationMessage)
    if report.SemanticWorkPerformed then
        lines.Add(sprintf "Semantic work duration: %s" (formatElapsed (TimeSpan.FromMilliseconds(float report.SemanticWorkDurationMs))))
        lines.Add(sprintf "Code embedding batches: %d total, %d failed" report.CodeBatchCount report.CodeFailedBatches)
        lines.Add(sprintf "Summary embedding batches: %d total, %d failed" report.SummaryBatchCount report.SummaryFailedBatches)
    lines.Add(sprintf "Semantic state: %s" report.SemanticState)
    if report.SemanticMessage <> "" then
        lines.Add(sprintf "Semantic message: %s" report.SemanticMessage)
    if report.FirstFailure <> "" then
        lines.Add(sprintf "First failure: %s" report.FirstFailure)
    match renderComparisonFactsSummary report.ComparisonFacts with
    | Some summary -> lines.Add(summary)
    | None -> ()
    String.concat Environment.NewLine lines

let private renderLatestInvocationReport (report: LatestInvocationReport) =
    renderInvocationReport "Latest invocation" report

let private renderInvocationHistory (reports: LatestInvocationReport[]) =
    if reports.Length = 0 then
        "No indexing invocation history found. Run 'code-sight index' first."
    else
        reports
        |> Array.rev
        |> Array.mapi (fun index report -> renderInvocationReport (sprintf "%d. Invocation" (index + 1)) report)
        |> String.concat (Environment.NewLine + Environment.NewLine)

let private sameInvocationReport (left: LatestInvocationReport) (right: LatestInvocationReport) =
    left.InvocationKind = right.InvocationKind
    && left.CompletedAtUtc = right.CompletedAtUtc
    && left.InvocationMessage = right.InvocationMessage
    && left.SemanticState = right.SemanticState
    && left.FirstFailure = right.FirstFailure

let private tryFindPreviousInvocation (latest: LatestInvocationReport) (history: LatestInvocationReport[]) =
    let newestFirst = history |> Array.rev
    match newestFirst with
    | [||] -> None
    | _ when sameInvocationReport newestFirst.[0] latest ->
        newestFirst |> Array.tryItem 1
    | _ ->
        newestFirst |> Array.tryItem 0

let private formatSignedInt value =
    if value >= 0 then sprintf "+%d" value else string value

let private formatSignedDurationMs (milliseconds: int64) =
    let sign = if milliseconds >= 0L then "+" else "-"
    let absolute = TimeSpan.FromMilliseconds(float (abs milliseconds))
    sprintf "%s%s" sign (formatElapsed absolute)

let private comparisonFactsAvailabilityDescription comparisonFacts =
    match comparisonFacts with
    | Available _ -> None
    | Unavailable -> Some "unavailable for this invocation"
    | Legacy -> Some "unavailable on a legacy retained entry"

let private renderFailedBatchComparison (latest: LatestInvocationReport) (previous: LatestInvocationReport) =
    let currentFailed = latest.CodeFailedBatches + latest.SummaryFailedBatches
    let previousFailed = previous.CodeFailedBatches + previous.SummaryFailedBatches
    sprintf "Failed embedding batches: %d (previous: %d, delta: %s)" currentFailed previousFailed (formatSignedInt (currentFailed - previousFailed))

let private renderSemanticWorkDurationComparison (latest: LatestInvocationReport) (previous: LatestInvocationReport) =
    match latest.SemanticWorkPerformed, previous.SemanticWorkPerformed with
    | true, true ->
        sprintf
            "Semantic work duration: %s (previous: %s, delta: %s)"
            (formatElapsed (TimeSpan.FromMilliseconds(float latest.SemanticWorkDurationMs)))
            (formatElapsed (TimeSpan.FromMilliseconds(float previous.SemanticWorkDurationMs)))
            (formatSignedDurationMs (latest.SemanticWorkDurationMs - previous.SemanticWorkDurationMs))
    | true, false ->
        sprintf
            "Semantic work duration: %s (previous: unavailable — semantic work was not performed)"
            (formatElapsed (TimeSpan.FromMilliseconds(float latest.SemanticWorkDurationMs)))
    | false, true ->
        sprintf
            "Semantic work duration: unavailable — semantic work was not performed (previous: %s)"
            (formatElapsed (TimeSpan.FromMilliseconds(float previous.SemanticWorkDurationMs)))
    | false, false ->
        "Semantic work duration: unavailable — semantic work was not performed on either invocation."

let private renderStructuralComparison metricName selector (latest: LatestInvocationReport) (previous: LatestInvocationReport) =
    match latest.ComparisonFacts, previous.ComparisonFacts with
    | Available latestFacts, Available previousFacts ->
        let currentValue = selector latestFacts
        let previousValue = selector previousFacts
        sprintf "%s: %d (previous: %d, delta: %s)" metricName currentValue previousValue (formatSignedInt (currentValue - previousValue))
    | Available latestFacts, previousFacts ->
        sprintf "%s: %d (previous: %s)" metricName (selector latestFacts) (defaultArg (comparisonFactsAvailabilityDescription previousFacts) "unavailable")
    | latestFacts, Available previousFacts ->
        sprintf "%s: %s (previous: %d)" metricName (defaultArg (comparisonFactsAvailabilityDescription latestFacts) "unavailable") (selector previousFacts)
    | latestFacts, previousFacts ->
        sprintf
            "%s: %s (previous: %s)"
            metricName
            (defaultArg (comparisonFactsAvailabilityDescription latestFacts) "unavailable")
            (defaultArg (comparisonFactsAvailabilityDescription previousFacts) "unavailable")

let private renderInvocationComparison (latest: LatestInvocationReport) (previous: LatestInvocationReport) =
    let lines = ResizeArray<string>()
    lines.Add("Invocation comparison: latest vs previous retained run")
    lines.Add(sprintf "Latest invocation: %s" latest.InvocationKind)
    if latest.CompletedAtUtc <> DateTime.MinValue then
        lines.Add(sprintf "Latest completed at (UTC): %s" (latest.CompletedAtUtc.ToUniversalTime().ToString("O")))
    lines.Add(sprintf "Previous retained invocation: %s" previous.InvocationKind)
    if previous.CompletedAtUtc <> DateTime.MinValue then
        lines.Add(sprintf "Previous completed at (UTC): %s" (previous.CompletedAtUtc.ToUniversalTime().ToString("O")))
    lines.Add(sprintf "Semantic state: %s (previous: %s)" latest.SemanticState previous.SemanticState)
    lines.Add(renderFailedBatchComparison latest previous)
    lines.Add(renderSemanticWorkDurationComparison latest previous)
    lines.Add(renderStructuralComparison "Chunks" (fun facts -> facts.ChunkCount) latest previous)
    lines.Add(renderStructuralComparison "Imports" (fun facts -> facts.ImportCount) latest previous)
    lines.Add(renderStructuralComparison "Signatures" (fun facts -> facts.SignatureCount) latest previous)
    if latest.ComparisonFacts = Legacy || previous.ComparisonFacts = Legacy then
        lines.Add("Legacy note: retained entries captured before comparison facts existed render new structural comparison fields as unavailable.")
    String.concat Environment.NewLine lines

let private persistIndexingInvocationReport (indexDir: string) (report: LatestInvocationReport) =
    saveLatestInvocationReport indexDir report
    match appendInvocationHistory indexDir report with
    | Some warning -> eprintfn "  Warning: %s" warning
    | None -> ()

let private currentSemanticStateReport invocationKind invocationMessage (index: CodeIndex) =
    latestInvocationReport
        invocationKind
        invocationMessage
        false
        TimeSpan.Zero
        0
        0
        0
        0
        index.SemanticState
        index.SemanticMessage
        None
        (currentIndexComparisonFacts index)

let private failedInvocationReport invocationKind invocationMessage firstFailure (existingIndex: CodeIndex option) =
    match existingIndex with
    | Some index ->
        latestInvocationReport
            invocationKind
            invocationMessage
            false
            TimeSpan.Zero
            0
            0
            0
            0
            index.SemanticState
            index.SemanticMessage
            (Some firstFailure)
            Unavailable
    | None ->
        latestInvocationReport
            invocationKind
            invocationMessage
            false
            TimeSpan.Zero
            0
            0
            0
            0
            "none"
            "No persisted index is available from a successful invocation."
            (Some firstFailure)
            Unavailable

let private tryLoadExistingIndex indexDir =
    try
        IndexStore.load indexDir
    with _ ->
        None

let private failedIndexReportDirs (repoRoot: string) =
    let dirs = ResizeArray<string>()
    let addDir (dir: string) =
        let candidate =
            if Path.IsPathRooted dir then dir
            else dir
        let candidateFullPath = Path.GetFullPath candidate
        if not (dirs |> Seq.exists (fun existing -> Path.GetFullPath(existing) = candidateFullPath)) then
            dirs.Add candidate

    let configPath = Path.Combine(repoRoot, "code-intel.json")
    let mutable recoveredConfiguredIndexDir = false
    if File.Exists configPath then
        try
            let rawConfig = File.ReadAllText configPath
            let match' = Regex.Match(rawConfig, "\"indexDir\"\\s*:\\s*\"([^\"]+)\"")
            if match'.Success then
                addDir match'.Groups.[1].Value
                recoveredConfiguredIndexDir <- true
        with _ ->
            ()

    if not recoveredConfiguredIndexDir then
        addDir (Path.Combine(repoRoot, ".code-intel"))

    dirs.ToArray()

let private saveFailedIndexingInvocationReports repoRoot invocationKind invocationMessage firstFailure =
    for indexDir in failedIndexReportDirs repoRoot do
        failedInvocationReport invocationKind invocationMessage firstFailure (tryLoadExistingIndex indexDir)
        |> persistIndexingInvocationReport indexDir

let private chunkText (contentLookup: System.Collections.Generic.IDictionary<string * string * int, string>) (chunk: ChunkEntry) =
    let context =
        if chunk.Module <> "" then sprintf "%s\n%s:%s" chunk.Module chunk.Kind chunk.Name
        else sprintf "%s:%s" chunk.Kind chunk.Name
    let content =
        match contentLookup.TryGetValue((chunk.FilePath, chunk.Name, chunk.StartLine)) with
        | true, value -> value
        | _ -> chunk.Name
    sprintf "%s\n%s" context content

let private summaryText (chunk: ChunkEntry) =
    if chunk.Summary <> "" then chunk.Summary else sprintf "%s %s" chunk.Kind chunk.Name

let private runEmbeddingPass (cfg: CodeSightConfig) (skipEmbeddings: bool) (label: string) (texts: string[]) =
    if texts.Length = 0 then
        { Rows = [||]; FailedBatches = 0; FirstFailure = None; TotalBatches = 0; Elapsed = TimeSpan.Zero }
    elif skipEmbeddings then
        { Rows = emptyEmbeddings texts.Length; FailedBatches = 0; FirstFailure = None; TotalBatches = 0; Elapsed = TimeSpan.Zero }
    else
        let batches = texts |> Array.chunkBySize cfg.EmbeddingBatchSize
        let results = ResizeArray<float32[]>()
        let mutable failedBatches = 0
        let mutable firstFailure = None
        let stopwatch = Stopwatch.StartNew()

        for batchIndex, batch in batches |> Array.indexed do
            match EmbeddingService.embedWithTimeout (embeddingTimeout cfg) cfg.EmbeddingUrl batch |> Async.AwaitTask |> Async.RunSynchronously with
            | Ok embs ->
                results.AddRange embs
            | Error msg ->
                failedBatches <- failedBatches + 1
                if firstFailure.IsNone then firstFailure <- Some msg
                eprintfn "  Warning: %s embeddings failed — %s. Storing empty rows for this batch." label msg
                results.AddRange(emptyEmbeddings batch.Length)
            eprintfn "  %s embeddings: batch %d/%d complete (%s elapsed)" label (batchIndex + 1) batches.Length (formatElapsed stopwatch.Elapsed)

        eprintfn "  %s embeddings: completed %d/%d batches in %s" label batches.Length batches.Length (formatElapsed stopwatch.Elapsed)

        { Rows = results.ToArray(); FailedBatches = failedBatches; FirstFailure = firstFailure; TotalBatches = batches.Length; Elapsed = stopwatch.Elapsed }

let private canReuseSemanticRows (skipEmbeddings: bool) (existingIdx: CodeIndex option) (oldChunkCount: int) =
    not skipEmbeddings &&
    match existingIdx with
    | Some idx when idx.SemanticState = "full"
                     && idx.CodeEmbeddings.Length = oldChunkCount
                     && idx.SummaryEmbeddings.Length = oldChunkCount -> true
    | _ -> false

let private runSemanticEmbeddingCoordinator
    (cfg: CodeSightConfig)
    (skipEmbeddings: bool)
    (existingIdx: CodeIndex option)
    (oldChunkCount: int)
    (finalChunks: ChunkEntry[])
    (contentLookup: System.Collections.Generic.IDictionary<string * string * int, string>) =

    let reuseSemanticRows = canReuseSemanticRows skipEmbeddings existingIdx oldChunkCount
    let embeddingTargets =
        if reuseSemanticRows then finalChunks.[oldChunkCount..] else finalChunks

    let embedLabel =
        if reuseSemanticRows then sprintf "%d new chunk%s" embeddingTargets.Length (if embeddingTargets.Length = 1 then "" else "s")
        else sprintf "%d chunk%s" embeddingTargets.Length (if embeddingTargets.Length = 1 then "" else "s")
    eprintfn "▶ Computing embeddings for %s..." embedLabel

    let totalStopwatch = Stopwatch.StartNew()
    let codePass =
        embeddingTargets
        |> Array.map (chunkText contentLookup)
        |> runEmbeddingPass cfg skipEmbeddings "code"
    let summaryPass =
        embeddingTargets
        |> Array.map summaryText
        |> runEmbeddingPass cfg skipEmbeddings "summary"

    let codeEmbeddings =
        if skipEmbeddings then
            emptyEmbeddings finalChunks.Length
        elif reuseSemanticRows then
            match existingIdx with
            | Some idx -> Array.append idx.CodeEmbeddings codePass.Rows
            | None -> codePass.Rows
        else
            codePass.Rows

    let summaryEmbeddings =
        if skipEmbeddings then
            emptyEmbeddings finalChunks.Length
        elif reuseSemanticRows then
            match existingIdx with
            | Some idx -> Array.append idx.SummaryEmbeddings summaryPass.Rows
            | None -> summaryPass.Rows
        else
            summaryPass.Rows

    if not skipEmbeddings then
        eprintfn "  Semantic embedding work completed in %s" (formatElapsed totalStopwatch.Elapsed)

    let dim = firstNonEmptyDim codeEmbeddings
    if dim > 0 then
        eprintfn "  %d embeddings (%d dimensions)" codeEmbeddings.Length dim
    else
        eprintfn "  Embeddings not available (no server or empty responses)"

    let failedBatches = codePass.FailedBatches + summaryPass.FailedBatches
    let firstFailure = mergeFirstFailure codePass.FirstFailure summaryPass.FirstFailure
    let semanticState, semanticMessage =
        if skipEmbeddings then
            "no-embed", "Semantic search is disabled for this index (--no-embed)."
        elif failedBatches > 0 then
            "degraded", defaultArg firstFailure "One or more embedding batches failed."
        else
            "full", ""

    {
        CodeEmbeddings = codeEmbeddings
        SummaryEmbeddings = summaryEmbeddings
        EmbeddingDim = dim
        CodeBatchCount = codePass.TotalBatches
        CodeFailedBatches = codePass.FailedBatches
        SummaryBatchCount = summaryPass.TotalBatches
        SummaryFailedBatches = summaryPass.FailedBatches
        FailedBatches = failedBatches
        SemanticState = semanticState
        SemanticMessage = semanticMessage
        FirstFailure = firstFailure
        SemanticWorkPerformed = not skipEmbeddings
        SemanticWorkElapsed = if skipEmbeddings then TimeSpan.Zero else totalStopwatch.Elapsed
    }

let private semanticInvocationReport invocationKind invocationMessage comparisonFacts (semanticPass: SemanticEmbeddingPass) =
    latestInvocationReport
        invocationKind
        invocationMessage
        semanticPass.SemanticWorkPerformed
        semanticPass.SemanticWorkElapsed
        semanticPass.CodeBatchCount
        semanticPass.CodeFailedBatches
        semanticPass.SummaryBatchCount
        semanticPass.SummaryFailedBatches
        semanticPass.SemanticState
        semanticPass.SemanticMessage
        semanticPass.FirstFailure
        comparisonFacts

let private describeSemanticState semanticState semanticMessage failedBatches =
    match semanticState with
    | "full" ->
        eprintfn "  Semantic status: full"
    | "no-embed" ->
        eprintfn "  Semantic status: no-embed — semantic search/similarity disabled for this index."
    | "degraded" ->
        let failureSuffix =
            if failedBatches > 0 then sprintf " (%d failed embedding batch%s)" failedBatches (if failedBatches = 1 then "" else "es")
            else ""
        eprintfn "  Semantic status: degraded — %s%s" semanticMessage failureSuffix
        if semanticMessage <> "" && failedBatches > 0 then
            eprintfn "  Embedding failure summary: %d failed batch%s; first failure: %s" failedBatches (if failedBatches = 1 then "" else "es") semanticMessage
    | other ->
        eprintfn "  Semantic status: %s%s" other (if semanticMessage <> "" then sprintf " — %s" semanticMessage else "")

let private printIndexCompletionSummary operation chunkCount importCount signatureCount (semanticPass: SemanticEmbeddingPass) =
    let counts = sprintf "%d chunks, %d imports, %d signatures" chunkCount importCount signatureCount
    match semanticPass.SemanticState with
    | "full" ->
        eprintfn "✓ Index %s: %s (semantic status: full)" operation counts
    | "no-embed" ->
        eprintfn "✓ Structural-only index %s: %s (semantic search disabled: --no-embed)" operation counts
    | "degraded" ->
        let batchLabel = if semanticPass.FailedBatches = 1 then "batch" else "batches"
        let firstFailure =
            semanticPass.FirstFailure
            |> Option.filter (fun failure -> not (String.IsNullOrWhiteSpace failure))
            |> Option.defaultValue semanticPass.SemanticMessage
        if String.IsNullOrWhiteSpace firstFailure then
            eprintfn "⚠ Index %s with degraded semantic health: %s (%d failed embedding %s)" operation counts semanticPass.FailedBatches batchLabel
        else
            eprintfn "⚠ Index %s with degraded semantic health: %s (%d failed embedding %s; first failure: %s)" operation counts semanticPass.FailedBatches batchLabel firstFailure
    | other ->
        let messageSuffix =
            if String.IsNullOrWhiteSpace semanticPass.SemanticMessage then ""
            else sprintf " — %s" semanticPass.SemanticMessage
        eprintfn "✓ Index %s: %s (semantic status: %s%s)" operation counts other messageSuffix

let private failedInvocationKind isPartial skipEmbeddings =
    match isPartial, skipEmbeddings with
    | true, true -> "failed-partial-structural-only"
    | true, false -> "failed-partial"
    | false, true -> "failed-structural-only"
    | false, false -> "failed-full"

let private indexExitCode semanticState =
    if semanticState = "degraded" then 2 else 0

let private distinctFilePaths (chunks: CodeChunk[]) =
    chunks |> Array.map (fun chunk -> chunk.FilePath) |> Array.distinct

let private staleCacheFiles (candidateFiles: string[]) (existingIdx: CodeIndex option) (cacheStatus: IndexStore.SourceChunkCacheStatus) (cachedSourceChunks: CodeChunk[]) (maxChunkChars: int) =
    let candidateSet = Set.ofArray candidateFiles
    let cachedCandidateFiles =
        cachedSourceChunks
        |> Array.filter (fun chunk -> candidateSet.Contains chunk.FilePath)
        |> distinctFilePaths

    let overCapCachedFiles =
        cachedSourceChunks
        |> Array.filter (fun chunk -> candidateSet.Contains chunk.FilePath && chunk.Content.Length > maxChunkChars)
        |> distinctFilePaths

    match cacheStatus with
    | IndexStore.Fresh -> overCapCachedFiles
    | IndexStore.Missing when Option.isSome existingIdx -> candidateFiles
    | IndexStore.Stale _ when cachedCandidateFiles.Length > 0 -> cachedCandidateFiles
    | IndexStore.Stale _ -> candidateFiles
    | _ -> [||]

let runIndex (cfg: CodeSightConfig) (skipEmbeddings: bool) =
    let hashesPath = Path.Combine(cfg.IndexDir, "hashes.json")
    ensureIndexDir cfg.IndexDir

    // Index ALL scope dirs (union) so every scope can query
    let cfgAll = fullIndexConfig cfg
    let allDirs = cfgAll.SrcDirs

    let allFilesAbs = TreeSitterChunker.findSourceFiles cfgAll
    let toRel (f: string) = Path.GetRelativePath(cfg.RepoRoot, f).Replace("\\", "/")
    let allFilesRel = allFilesAbs |> Array.map toRel
    eprintfn "Found %d source files in %A (scopes: %s)" allFilesAbs.Length allDirs (cfg.Scopes |> Array.map (fun s -> s.Name) |> String.concat ", ")

    // Compute current hashes (relative path → hash)
    let currentHashes = Array.zip allFilesRel allFilesAbs |> Array.map (fun (rel, abs) -> rel, FileHashing.hashFile abs) |> Map.ofArray
    let oldHashes = FileHashing.loadHashes hashesPath

    let hashChanged = currentHashes |> Map.toArray |> Array.filter (fun (f, h) -> match Map.tryFind f oldHashes with Some old -> old <> h | None -> true) |> Array.map fst
    let removed = oldHashes |> Map.toArray |> Array.filter (fun (f, _) -> not (currentHashes.ContainsKey f)) |> Array.map fst

    let existingIdx = IndexStore.load cfg.IndexDir
    let cacheStatus = IndexStore.getSourceChunkCacheStatus cfg.IndexDir cfg.MaxChunkChars
    let loadedSourceChunks = IndexStore.loadSourceChunks cfg.IndexDir |> Option.defaultValue [||]
    let staleFiles =
        staleCacheFiles allFilesRel existingIdx cacheStatus loadedSourceChunks cfg.MaxChunkChars
    let changed = Array.append hashChanged staleFiles |> Array.distinct
    let unchanged = currentHashes |> Map.toArray |> Array.filter (fun (f, h) -> not (Array.contains f changed) && match Map.tryFind f oldHashes with Some old -> old = h | None -> false) |> Array.map fst
    let rebuildSemanticState =
        existingIdx
        |> Option.exists (fun idx -> idx.SemanticState <> "full")
    let rebuildRequired = changed.Length > 0 || removed.Length > 0 || Option.isNone existingIdx || skipEmbeddings || rebuildSemanticState
    let canReuseSourceCache =
        match cacheStatus with
        | IndexStore.Fresh -> true
        | _ -> false
    let needUnchangedSourceChunks = not skipEmbeddings && (rebuildSemanticState || Option.isNone existingIdx || not canReuseSourceCache)

    // Map relative back to absolute for chunking
    let relToAbs = Array.zip allFilesRel allFilesAbs |> Map.ofArray
    let absOf rel = Map.find rel relToAbs

    if not rebuildRequired then
        eprintfn "Index is up to date (%d files, no changes)" allFilesAbs.Length
        match existingIdx with
        | Some index ->
            currentSemanticStateReport "up-to-date" "Index was already up to date; semantic work did not run." index
            |> persistIndexingInvocationReport cfg.IndexDir
        | None -> ()
        0
    else
        eprintfn "  Changed: %d, Unchanged: %d, Removed: %d" changed.Length unchanged.Length removed.Length
        if staleFiles.Length > 0 then
            match cacheStatus with
            | IndexStore.Fresh ->
                eprintfn "  Re-chunking %d file(s) because cached source chunks exceed maxChunkChars=%d" staleFiles.Length cfg.MaxChunkChars
            | IndexStore.Missing ->
                eprintfn "  Re-chunking %d file(s) because source chunk cache is missing and existing indexes must be upgraded to the new maxChunkChars contract" staleFiles.Length
            | IndexStore.Stale reason ->
                eprintfn "  Re-chunking %d file(s) because %s" staleFiles.Length reason

        let changedAbs = changed |> Array.map absOf
        let unchangedSet = Set.ofArray unchanged

        // Chunk changed files
        eprintfn "▶ Chunking %d changed files..." changed.Length
        let newChunks = TreeSitterChunker.chunkFiles cfgAll changedAbs
        eprintfn "  %d chunks from changed files" newChunks.Length

        // Load cached source chunks for unchanged files
        let cachedSourceChunks =
            if canReuseSourceCache then
                loadedSourceChunks |> Array.filter (fun c -> unchangedSet.Contains c.FilePath)
            elif needUnchangedSourceChunks && unchanged.Length > 0 then
                let unchangedAbs = unchanged |> Array.map absOf
                eprintfn "  Source cache unavailable for reuse — re-chunking %d unchanged files..." unchangedAbs.Length
                TreeSitterChunker.chunkFiles cfgAll unchangedAbs
            else
                [||]

        // Merge all source chunks (cached unchanged + new)
        let allSourceChunks = Array.append cachedSourceChunks newChunks

        let oldChunks =
            match existingIdx with
            | Some idx -> idx.Chunks |> Array.filter (fun c -> unchangedSet.Contains c.FilePath)
            | None -> [||]
        let allChunkEntries =
            let newEntries = newChunks |> Array.map (fun c ->
                { FilePath = c.FilePath; Module = c.Module; Name = c.Name; Kind = c.Kind
                  StartLine = c.StartLine; EndLine = c.EndLine; Summary = ""; Signature = ""; Extra = Map.empty })
            Array.append oldChunks newEntries

        // Extract imports and signatures (full — fast)
        eprintfn "▶ Extracting imports..."
        let imports = TreeSitterChunker.extractImports cfgAll allFilesAbs |> Array.map (fun i -> i.FilePath, i.Module)
        eprintfn "  %d import edges" imports.Length

        eprintfn "▶ Extracting signatures..."
        let signatures = TreeSitterChunker.extractSignatures cfgAll allFilesAbs
        eprintfn "  %d signatures" signatures.Length

        eprintfn "▶ Extracting type refs..."
        let typeRefs = TreeSitterChunker.extractTypeRefs cfgAll allFilesAbs |> Array.map (fun r -> r.FilePath, r.TypeRefs)
        eprintfn "  %d files with type refs" typeRefs.Length

        // Match signatures to chunks
        let sigLookup = signatures |> Array.map (fun s -> (s.FilePath, s.Name, s.StartLine), s.Signature) |> dict
        let finalChunks =
            allChunkEntries |> Array.map (fun c ->
                let sig' =
                    match sigLookup.TryGetValue((c.FilePath, c.Name, c.StartLine)) with
                    | true, v -> v
                    | _ ->
                        let shortName = c.Name.Split('.') |> Array.last
                        match sigLookup.TryGetValue((c.FilePath, shortName, c.StartLine)) with
                        | true, v -> v | _ -> c.Signature
                if sig' <> "" then { c with Signature = sig' } else c)

        let contentLookup = buildChunkContentLookup allSourceChunks
        let semanticPass =
            runSemanticEmbeddingCoordinator cfg skipEmbeddings existingIdx oldChunks.Length finalChunks contentLookup

        let index : CodeIndex = {
            Chunks = finalChunks
            CodeEmbeddings = semanticPass.CodeEmbeddings
            SummaryEmbeddings = semanticPass.SummaryEmbeddings
            Imports = imports
            TypeRefs = typeRefs
            EmbeddingDim = semanticPass.EmbeddingDim
            SemanticState = semanticPass.SemanticState
            SemanticMessage = semanticPass.SemanticMessage
            FailedEmbeddingBatches = semanticPass.FailedBatches
        }
        IndexStore.save cfg.IndexDir index
        IndexStore.saveSourceChunks cfg.IndexDir cfg.MaxChunkChars allSourceChunks
        FileHashing.saveHashes hashesPath currentHashes
        semanticInvocationReport
            (if skipEmbeddings then "structural-only" else "full")
            (if skipEmbeddings then "Semantic work was skipped because this invocation used --no-embed." else "Latest full index invocation completed.")
            (completedInvocationComparisonFacts finalChunks imports signatures.Length)
            semanticPass
        |> persistIndexingInvocationReport cfg.IndexDir
        describeSemanticState semanticPass.SemanticState semanticPass.SemanticMessage semanticPass.FailedBatches
        printIndexCompletionSummary "built" finalChunks.Length imports.Length signatures.Length semanticPass
        indexExitCode semanticPass.SemanticState

/// Re-index only the specified files, merging with cached index for everything else.
let runIndexFiles (cfg: CodeSightConfig) (files: string[]) (skipEmbeddings: bool) =
    let hashesPath = Path.Combine(cfg.IndexDir, "hashes.json")
    ensureIndexDir cfg.IndexDir

    let cfgAll = fullIndexConfig cfg

    // Resolve files to absolute + relative paths
    let fileResolutions = resolveRequestedFiles cfg.RepoRoot files
    for resolution in fileResolutions |> Array.filter (fun resolution -> not resolution.Exists) do
        eprintfn "  Warning: file not found, skipping: %s" resolution.RequestedPath

    let resolvedFiles =
        fileResolutions
        |> Array.choose (fun resolution ->
            if resolution.Exists then Some (resolution.RelativePath, resolution.AbsolutePath)
            else None)

    if resolvedFiles.Length = 0 then
        eprintfn "No valid files to index."
        failedInvocationReport
            (failedInvocationKind true skipEmbeddings)
            "Index invocation failed before semantic work because no valid files were resolved."
            "No valid files to index."
            (IndexStore.load cfg.IndexDir)
        |> persistIndexingInvocationReport cfg.IndexDir
        1
    else
        let requestedChangedRel = resolvedFiles |> Array.map fst
        let requestedChangedSet = Set.ofArray requestedChangedRel

        eprintfn "▶ Re-indexing %d files..." resolvedFiles.Length
        for rel, _ in resolvedFiles do eprintfn "  %s" rel

        // Everything NOT in the changed set is unchanged
        let oldHashes = FileHashing.loadHashes hashesPath
        let unchangedCandidates =
            oldHashes |> Map.toArray
            |> Array.filter (fun (f, _) -> not (requestedChangedSet.Contains f))
            |> Array.map fst |> Set.ofArray
        let existingIdx = IndexStore.load cfg.IndexDir
        let cacheStatus = IndexStore.getSourceChunkCacheStatus cfg.IndexDir cfg.MaxChunkChars
        let loadedSourceChunks = IndexStore.loadSourceChunks cfg.IndexDir |> Option.defaultValue [||]
        let staleReuseFiles =
            staleCacheFiles (unchangedCandidates |> Set.toArray) existingIdx cacheStatus loadedSourceChunks cfg.MaxChunkChars
            |> Array.filter (fun rel -> File.Exists(Path.Combine(cfg.RepoRoot, rel)))
        let effectiveChangedRel = Array.append requestedChangedRel staleReuseFiles |> Array.distinct
        let effectiveChangedSet = Set.ofArray effectiveChangedRel
        let unchangedSet =
            oldHashes |> Map.toArray
            |> Array.filter (fun (f, _) -> not (effectiveChangedSet.Contains f))
            |> Array.map fst |> Set.ofArray
        let canReuseSourceCache =
            match cacheStatus with
            | IndexStore.Fresh -> true
            | _ -> false
        let needUnchangedSourceChunks =
            not skipEmbeddings
            && (Option.isNone existingIdx
                || existingIdx |> Option.exists (fun idx -> idx.SemanticState <> "full")
                || not canReuseSourceCache)
        let relToAbs (rel: string) = Path.GetFullPath(Path.Combine(cfg.RepoRoot, rel))

        // Chunk changed files
        if staleReuseFiles.Length > 0 then
            match cacheStatus with
            | IndexStore.Fresh ->
                eprintfn "  Re-chunking %d unchanged file(s) because cached source chunks exceed maxChunkChars=%d" staleReuseFiles.Length cfg.MaxChunkChars
            | IndexStore.Missing ->
                eprintfn "  Re-chunking %d unchanged file(s) because source chunk cache is missing and existing indexes must be upgraded to the new maxChunkChars contract" staleReuseFiles.Length
            | IndexStore.Stale reason ->
                eprintfn "  Re-chunking %d unchanged file(s) because %s" staleReuseFiles.Length reason
        let effectiveChangedAbs =
            effectiveChangedRel
            |> Array.map (fun rel ->
                match resolvedFiles |> Array.tryFind (fun (requestedRel, _) -> requestedRel = rel) with
                | Some (_, abs) -> abs
                | None -> relToAbs rel)
        eprintfn "▶ Chunking %d files..." effectiveChangedAbs.Length
        let newChunks = TreeSitterChunker.chunkFiles cfgAll effectiveChangedAbs
        eprintfn "  %d chunks from changed files" newChunks.Length

        // Load cached source chunks for unchanged files
        let cachedSourceChunks =
            if canReuseSourceCache then
                loadedSourceChunks |> Array.filter (fun c -> unchangedSet.Contains c.FilePath)
            elif needUnchangedSourceChunks && unchangedSet.Count > 0 then
                let unchangedAbs =
                    unchangedSet
                    |> Set.toArray
                    |> Array.map relToAbs
                eprintfn "  Source cache missing — re-chunking %d unchanged files..." unchangedAbs.Length
                TreeSitterChunker.chunkFiles cfgAll unchangedAbs
            else
                [||]
        let allSourceChunks = Array.append cachedSourceChunks newChunks

        let oldChunks =
            match existingIdx with
            | Some idx -> idx.Chunks |> Array.filter (fun c -> unchangedSet.Contains c.FilePath)
            | None -> [||]
        let allChunkEntries =
            let newEntries = newChunks |> Array.map (fun c ->
                { FilePath = c.FilePath; Module = c.Module; Name = c.Name; Kind = c.Kind
                  StartLine = c.StartLine; EndLine = c.EndLine; Summary = ""; Signature = ""; Extra = Map.empty })
            Array.append oldChunks newEntries

        // Imports and signatures (only re-extract for changed files, keep cached for rest)
        let allFilesAbs =
            let cachedFiles = unchangedSet |> Set.toArray |> Array.map relToAbs
            Array.append cachedFiles effectiveChangedAbs

        eprintfn "▶ Extracting imports..."
        let imports = TreeSitterChunker.extractImports cfgAll allFilesAbs |> Array.map (fun i -> i.FilePath, i.Module)
        eprintfn "  %d import edges" imports.Length

        eprintfn "▶ Extracting signatures..."
        let signatures = TreeSitterChunker.extractSignatures cfgAll allFilesAbs
        eprintfn "  %d signatures" signatures.Length

        eprintfn "▶ Extracting type refs..."
        let typeRefs = TreeSitterChunker.extractTypeRefs cfgAll allFilesAbs |> Array.map (fun r -> r.FilePath, r.TypeRefs)
        eprintfn "  %d files with type refs" typeRefs.Length

        // Match signatures to chunks
        let sigLookup = signatures |> Array.map (fun s -> (s.FilePath, s.Name, s.StartLine), s.Signature) |> dict
        let finalChunks =
            allChunkEntries |> Array.map (fun c ->
                let sig' =
                    match sigLookup.TryGetValue((c.FilePath, c.Name, c.StartLine)) with
                    | true, v -> v
                    | _ ->
                        let shortName = c.Name.Split('.') |> Array.last
                        match sigLookup.TryGetValue((c.FilePath, shortName, c.StartLine)) with
                        | true, v -> v | _ -> c.Signature
                if sig' <> "" then { c with Signature = sig' } else c)

        let contentLookup = buildChunkContentLookup allSourceChunks
        let semanticPass =
            runSemanticEmbeddingCoordinator cfg skipEmbeddings existingIdx oldChunks.Length finalChunks contentLookup

        let index : CodeIndex = {
            Chunks = finalChunks
            CodeEmbeddings = semanticPass.CodeEmbeddings
            SummaryEmbeddings = semanticPass.SummaryEmbeddings
            Imports = imports
            TypeRefs = typeRefs
            EmbeddingDim = semanticPass.EmbeddingDim
            SemanticState = semanticPass.SemanticState
            SemanticMessage = semanticPass.SemanticMessage
            FailedEmbeddingBatches = semanticPass.FailedBatches
        }
        IndexStore.save cfg.IndexDir index
        IndexStore.saveSourceChunks cfg.IndexDir cfg.MaxChunkChars allSourceChunks

        // Update hashes only for the changed files
        let updatedHashes =
            oldHashes
            |> Map.toArray |> Array.filter (fun (f, _) -> not (requestedChangedSet.Contains f))
            |> Array.append (resolvedFiles |> Array.map (fun (rel, abs) -> rel, FileHashing.hashFile abs))
            |> Map.ofArray
        FileHashing.saveHashes hashesPath updatedHashes
        semanticInvocationReport
            (if skipEmbeddings then "structural-only" else "partial")
            (if skipEmbeddings then "Semantic work was skipped because this invocation used --no-embed." else "Latest partial index invocation completed.")
            (completedInvocationComparisonFacts finalChunks imports signatures.Length)
            semanticPass
        |> persistIndexingInvocationReport cfg.IndexDir
        describeSemanticState semanticPass.SemanticState semanticPass.SemanticMessage semanticPass.FailedBatches
        printIndexCompletionSummary "updated" finalChunks.Length imports.Length signatures.Length semanticPass
        indexExitCode semanticPass.SemanticState

[<EntryPoint>]
let main args =
    let repo, command, query, scope, files, fnName, fnParams, fnDesc, fnFile, verbose, jsonOut, peerPath, noEmbed = parseArgs args

    match command with
    | "help" ->
        printUsage()
        0
    | "fn-add" ->
        let body =
            if fnFile <> "" then
                let path = if File.Exists fnFile then fnFile else Path.Combine(repo, fnFile)
                if File.Exists path then File.ReadAllText(path)
                else eprintfn "File not found: %s" fnFile; ""
            else query
        if body = "" then
            eprintfn "No function body provided. Pass it as an argument or use --file <path>."
            1
        else
            let ps = if fnParams = "" then [||] else fnParams.Split(',') |> Array.map (fun s -> s.Trim())
            let fn = { Name = fnName; Params = ps; Body = body; Description = fnDesc }
            match FunctionStore.add repo fn with
            | Ok msg -> printfn "%s" msg; 0
            | Error msg -> eprintfn "Error: %s" msg; 1
    | "fn-list" ->
        match FunctionStore.load repo with
        | Error msg ->
            eprintfn "Error: %s" msg
            1
        | Ok fns ->
            if fns.Length = 0 then
                if jsonOut then printfn "[]"
                else printfn "No functions defined. Use 'code-sight fn add <name> <body>' to create one."
            elif jsonOut then
                let options = System.Text.Json.JsonSerializerOptions(WriteIndented = true)
                let arr = fns |> Array.map (fun f ->
                    dict [ "name", box f.Name; "params", box f.Params; "body", box f.Body; "description", box f.Description ])
                printfn "%s" (System.Text.Json.JsonSerializer.Serialize(arr, options))
            elif verbose then
                printfn "%d function(s) in %s:" fns.Length repo
                printfn ""
                for f in fns do
                    let joined = f.Params |> String.concat ", "
                    let ps = if f.Params.Length = 0 then "()" else sprintf "(%s)" joined
                    printfn "  %s%s" f.Name ps
                    if f.Description <> "" then printfn "    %s" f.Description
                    printfn "    body: %s" f.Body
                    printfn ""
            else
                printfn "%d function(s):" fns.Length
                for f in fns do
                    let joined = f.Params |> String.concat ", "
                    let ps = if f.Params.Length = 0 then "()" else sprintf "(%s)" joined
                    let desc = if f.Description <> "" then sprintf " — %s" f.Description else ""
                    printfn "  %s%s%s" f.Name ps desc
            0
    | "fn-rm" ->
        match FunctionStore.remove repo fnName with
        | Ok msg -> printfn "%s" msg; 0
        | Error msg -> eprintfn "Error: %s" msg; 1
    | "index" ->
        let isPartial = files.Length > 0
        try
            let cfg = loadConfig repo
            try
                if isPartial then runIndexFiles cfg files noEmbed
                else runIndex cfg noEmbed
            with ex ->
                failedInvocationReport
                    (failedInvocationKind isPartial noEmbed)
                    "Index invocation failed before completion."
                    ex.Message
                    (tryLoadExistingIndex cfg.IndexDir)
                |> persistIndexingInvocationReport cfg.IndexDir
                eprintfn "Error: %s" ex.Message
                1
        with ex ->
            saveFailedIndexingInvocationReports
                repo
                (failedInvocationKind isPartial noEmbed)
                "Index invocation failed before configuration loaded."
                ex.Message
            eprintfn "Error: %s" ex.Message
            1
    | "preflight" ->
        let cfg = loadConfig repo
        runPreflight cfg files
    | "history" ->
        let cfg = loadConfig repo
        match loadInvocationHistory cfg.IndexDir with
        | Missing ->
            eprintfn "No indexing invocation history found. Run 'code-sight index' first."
            1
        | Loaded history when history.Length = 0 ->
            eprintfn "No indexing invocation history found. Run 'code-sight index' first."
            1
        | Loaded history ->
            printfn "%s" (renderInvocationHistory history)
            0
        | Corrupt reason ->
            eprintfn "Indexing invocation history is unreadable. Run 'code-sight index' to preserve it explicitly and start a new bounded history. Details: %s" reason
            1
    | "compare" ->
        let cfg = loadConfig repo
        match loadLatestInvocationReport cfg.IndexDir, loadInvocationHistory cfg.IndexDir with
        | None, _ ->
            eprintfn "No latest indexing invocation report found. Run 'code-sight index' at least twice before comparing invocations."
            1
        | _, Corrupt reason ->
            eprintfn "Indexing invocation history is unreadable. Run 'code-sight index' to preserve it explicitly and start a new bounded history. Details: %s" reason
            1
        | Some _, Missing ->
            eprintfn "No indexing invocation history found. Run 'code-sight index' at least twice before comparing invocations."
            1
        | Some latest, Loaded history when history.Length = 0 ->
            eprintfn "No indexing invocation history found. Run 'code-sight index' at least twice before comparing invocations."
            1
        | Some latest, Loaded history ->
            match tryFindPreviousInvocation latest history with
            | Some previous ->
                printfn "%s" (renderInvocationComparison latest previous)
                0
            | None ->
                eprintfn "Need at least two retained indexing invocations to compare. Run 'code-sight index' again, then retry."
                1
    | "status" ->
        let cfg = loadConfig repo
        match loadLatestInvocationReport cfg.IndexDir with
        | Some report ->
            printfn "%s" (renderLatestInvocationReport report)
            0
        | None ->
            eprintfn "No latest indexing invocation report found. Run 'code-sight index' first."
            1
    | "scopes" ->
        let cfg = loadConfig repo
        for s in cfg.Scopes do
            eprintfn "  %-12s → %s" s.Name (s.Dirs |> String.concat ", ")
        0
    | "modules" | "search" | "repl" | "intel" ->
        let cfg = loadConfig repo
        match IndexStore.load cfg.IndexDir with
        | None ->
            eprintfn "No index found. Run 'code-sight index' first."
            1
        | Some fullIndex ->
            // Filter index by scope if specified
            let index =
                if scope = "" then fullIndex
                else
                    let scopeDirs = Config.scopeDirs cfg scope
                    let inScope (filePath: string) =
                        scopeDirs |> Array.exists (fun d -> filePath.Replace("\\", "/").StartsWith(d + "/") || filePath.Replace("\\", "/").StartsWith(d + "\\"))
                    { fullIndex with
                        Chunks = fullIndex.Chunks |> Array.filter (fun c -> inScope c.FilePath)
                        Imports = fullIndex.Imports |> Array.filter (fun (f, _) -> inScope f)
                        TypeRefs = fullIndex.TypeRefs |> Array.filter (fun (f, _) -> inScope f)
                        // Embeddings need re-indexing to match — for now keep all (search will return some out-of-scope)
                        CodeEmbeddings = fullIndex.CodeEmbeddings
                        SummaryEmbeddings = fullIndex.SummaryEmbeddings }

            // Lazy-load source chunks — try cache first, fall back to re-chunking
            let chunksRef = lazy (
                match IndexStore.loadSourceChunks cfg.IndexDir with
                | Some cached ->
                    eprintfn "  [loaded %d cached source chunks]" cached.Length
                    Some cached
                | None ->
                    eprintfn "  [no cache — re-chunking source files...]"
                    let scopeCfg = if scope = "" then cfg else { cfg with SrcDirs = Config.scopeDirs cfg scope }
                    let allFiles = TreeSitterChunker.findSourceFiles scopeCfg
                    if allFiles.Length > 0 then Some (TreeSitterChunker.chunkFiles scopeCfg allFiles)
                    else None)
            // For modules/files/context/impact/imports/deps — no chunks needed
            // Pass None initially; primitives that need chunks will force the lazy
            let mutable engine = QueryEngine.create index None cfg.EmbeddingUrl cfg.EmbeddingTimeoutSeconds cfg.IndexDir cfg.RepoRoot cfg.SrcDirs (if peerPath <> "" then Some peerPath else None)
            let needsChunks = [| "expand"; "grep"; "refs"; "neighborhood"; "similar"; "walk"; "callers"; "explain" |]
            let ensureChunks (js: string) =
                if needsChunks |> Array.exists (fun p -> js.Contains(p)) then
                    if not chunksRef.IsValueCreated then
                        engine <- QueryEngine.create index chunksRef.Value cfg.EmbeddingUrl cfg.EmbeddingTimeoutSeconds cfg.IndexDir cfg.RepoRoot cfg.SrcDirs (if peerPath <> "" then Some peerPath else None)

            match command with
            | "modules" ->
                printfn "%s" (QueryEngine.eval engine "modules()")
                0
            | "search" ->
                let actualQuery =
                    if query = "-" then
                        use reader = new StreamReader(Console.OpenStandardInput())
                        reader.ReadToEnd().Trim()
                    else query
                if actualQuery = "" then
                    eprintfn "Usage: code-sight search '<js query>'"
                    1
                else
                    ensureChunks actualQuery
                    let result =
                        if jsonOut then QueryEngine.evalJson engine actualQuery
                        else QueryEngine.eval engine actualQuery
                    printfn "%s" result
                    0
            | "repl" ->
                eprintfn "code-sight REPL. Type JS queries, 'quit' to exit."
                eprintfn "  search(q,opts), refs(name,opts), grep(pattern,opts), modules(),"
                eprintfn "  files(p?), context(file), expand(id), neighborhood(id,opts),"
                eprintfn "  impact(type), imports(file), deps(pattern), similar(id,opts)"
                eprintfn ""
                engine <- QueryEngine.create index chunksRef.Value cfg.EmbeddingUrl cfg.EmbeddingTimeoutSeconds cfg.IndexDir cfg.RepoRoot cfg.SrcDirs (if peerPath <> "" then Some peerPath else None)
                let mutable running = true
                while running do
                    eprintf "> "
                    let line = System.Console.ReadLine()
                    if line = null || line.Trim() = "quit" || line.Trim() = "exit" then
                        running <- false
                    elif line.Trim() <> "" then
                        printfn "%s" (QueryEngine.eval engine (line.Trim()))
                        printfn ""
                0
            | "intel" ->
                if query = "" then
                    eprintfn "Usage: code-sight intel '<question>'"
                    1
                else
                    // Ensure chunks loaded for the mini-agent's code_search calls
                    engine <- QueryEngine.create index chunksRef.Value cfg.EmbeddingUrl cfg.EmbeddingTimeoutSeconds cfg.IndexDir cfg.RepoRoot cfg.SrcDirs (if peerPath <> "" then Some peerPath else None)
                    let modulesCache= QueryEngine.eval engine "modules()"
                    let playbooksDir =
                        // 1. Per-repo override
                        let repoPlaybooks = Path.Combine(repo, ".code-intel", "playbooks")
                        if Directory.Exists repoPlaybooks then repoPlaybooks
                        else
                        // 2. Alongside exe
                        let exePlaybooks = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "playbooks")
                        if Directory.Exists exePlaybooks then exePlaybooks
                        else
                        // 3. Source tree (dev mode)
                        Path.Combine(repo, "playbooks")
                    eprintfn "Dispatching to mini-model..."
                    // Build UDF signatures for the AI tool description
                    let udfSigs =
                        match FunctionStore.load repo with
                        | Error msg ->
                            eprintfn "Warning: %s" msg
                            ""
                        | Ok userFns ->
                            if userFns.Length = 0 then ""
                            else
                                userFns
                                |> Array.truncate 15
                                |> Array.map (fun f ->
                                    let ps = if f.Params.Length = 0 then "()" else sprintf "(%s)" (f.Params |> String.concat ",")
                                    let desc = if f.Description <> "" then sprintf " — %s" f.Description else ""
                                    sprintf "%s%s%s" f.Name ps desc)
                                |> String.concat "; "
                    let result = Intel.run engine playbooksDir query modulesCache udfSigs |> Async.AwaitTask |> Async.RunSynchronously
                    printfn "%s" result
                    0
            | _ -> 1
    | "" ->
        printUsage()
        0
    | other ->
        eprintfn "Unknown command: %s" other
        printUsage()
        1



