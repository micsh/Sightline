namespace AITeam.KnowledgeSight

open System
open System.Collections.Generic
open System.IO
open System.Text.RegularExpressions
open AITeam.Sight.Core

/// Ref tracking for expand/neighborhood across queries.
type QuerySession(indexDir: string) =
    let refsPath = Path.Combine(indexDir, "refs.json")
    let refs = Dictionary<string, int>()
    let mutable counter = 0

    do
        if File.Exists refsPath then
            try
                let json = File.ReadAllText(refsPath)
                let doc = System.Text.Json.JsonDocument.Parse(json)
                for prop in doc.RootElement.EnumerateObject() do
                    refs.[prop.Name] <- prop.Value.GetInt32()
                    let num = prop.Name.Substring(1) |> int
                    if num > counter then counter <- num
            with _ -> ()

    member _.NextRef(chunkIdx) =
        counter <- counter + 1
        let id = sprintf "R%d" counter
        refs.[id] <- chunkIdx
        try
            let dict = Dictionary<string, int>()
            for kv in refs do dict.[kv.Key] <- kv.Value
            let json = System.Text.Json.JsonSerializer.Serialize(dict)
            File.WriteAllText(refsPath, json)
        with _ -> ()
        id

    member _.GetRef(id: string) =
        match refs.TryGetValue(id) with true, v -> Some v | _ -> None

    member _.SaveSession(name: string) =
        let sessDir = Path.Combine(indexDir, "sessions")
        Directory.CreateDirectory(sessDir) |> ignore
        let dict = Dictionary<string, int>()
        for kv in refs do dict.[kv.Key] <- kv.Value
        let json = System.Text.Json.JsonSerializer.Serialize(dict)
        File.WriteAllText(Path.Combine(sessDir, name + ".json"), json)

    member _.LoadSession(name: string) =
        let sessPath = Path.Combine(indexDir, "sessions", name + ".json")
        if File.Exists sessPath then
            let json = File.ReadAllText(sessPath)
            let doc = System.Text.Json.JsonDocument.Parse(json)
            refs.Clear()
            counter <- 0
            for prop in doc.RootElement.EnumerateObject() do
                refs.[prop.Name] <- prop.Value.GetInt32()
                let num = prop.Name.Substring(1) |> int
                if num > counter then counter <- num
            true
        else false

    member _.ListSessions() =
        let sessDir = Path.Combine(indexDir, "sessions")
        if Directory.Exists sessDir then
            Directory.GetFiles(sessDir, "*.json")
            |> Array.map (fun f -> Path.GetFileNameWithoutExtension(f))
        else [||]

    member _.RefCount = refs.Count

/// All query primitives for knowledge/doc operations.
module Primitives =

    let private embedQuery (url: string) (query: string) =
        EmbeddingService.embed url [| sprintf "search_query: %s" query |]
        |> Async.AwaitTask |> Async.RunSynchronously
        |> Result.toOption
        |> Option.map (fun e -> e.[0])

    /// Normalize path separators for cross-platform comparison.
    let private normPath (p: string) = p.Replace('\\', '/')

    /// Find source chunk matching an index entry. Uses stable chunk ID as primary key.
    let private findSource (chunks: DocChunk[] option) (c: ChunkEntry) =
        chunks |> Option.bind (fun chs ->
            let targetCid = IndexStore.chunkId c.FilePath c.Heading c.StartLine
            match chs |> Array.tryFind (fun ch -> IndexStore.chunkId ch.FilePath ch.Heading ch.StartLine = targetCid) with
            | Some _ as hit -> hit
            | None ->
                chs |> Array.tryFind (fun ch ->
                    normPath ch.FilePath = normPath c.FilePath && ch.Heading = c.Heading && ch.StartLine = c.StartLine))

    // ── catalog (like modules in code-sight) ──

    let catalog (index: DocIndex) =
        index.Chunks |> Array.groupBy (fun c ->
            let parts = c.FilePath.Replace("\\", "/").Split('/')
            // Group by first directory under repo (e.g., "knowledge", "design", "pocs")
            let dotsIdx = parts |> Array.tryFindIndex (fun p -> p.StartsWith("."))
            match dotsIdx with
            | Some i when i + 1 < parts.Length -> parts.[i] + "/" + parts.[i + 1]
            | Some i -> parts.[i]
            | None ->
                if parts.Length >= 2 then parts.[parts.Length - 2]
                else "root")
        |> Array.sortBy fst
        |> Array.map (fun (dir, chunks) ->
            let fileNames = chunks |> Array.map (fun c -> Path.GetFileName c.FilePath) |> Array.distinct |> Array.sort
            let allTags = chunks |> Array.collect (fun c -> c.Tags.Split(',') |> Array.filter ((<>) "")) |> Array.distinct |> Array.truncate 8
            let topTitles = chunks |> Array.filter (fun c -> c.Level <= 1) |> Array.truncate 3 |> Array.map (fun c -> c.Heading)
            mdict [ "directory", box dir; "docs", box fileNames.Length; "sections", box chunks.Length
                    "fileList", box (fileNames |> String.concat ", ")
                    "topTags", box (allTags |> String.concat ", ")
                    "titles", box (topTitles |> String.concat "; ") ])

    // ── search ──

    let search (index: DocIndex) (session: QuerySession) (chunks: DocChunk[] option) (embeddingUrl: string)
               (query: string) (limit: int) (tag: string) (filePattern: string) =
        match embedQuery embeddingUrl query with
        | None -> [| mdict [ "error", box "embedding server not available — search requires embeddings" ] |]
        | Some qEmb ->
            IndexStore.search index qEmb (limit * 3)
            |> Array.filter (fun (i, _) ->
                let c = index.Chunks.[i]
                (String.IsNullOrEmpty(tag) || c.Tags.Contains(tag, StringComparison.OrdinalIgnoreCase)) &&
                (String.IsNullOrEmpty(filePattern) || c.FilePath.Contains(filePattern, StringComparison.OrdinalIgnoreCase)))
            |> Array.truncate limit
            |> Array.map (fun (i, sim) ->
                let c = index.Chunks.[i]
                let id = session.NextRef(i)
                mdict [ "id", box id; "score", box (Math.Round(float sim, 3))
                        "heading", box c.Heading; "headingPath", box c.HeadingPath
                        "file", box (Path.GetFileName c.FilePath); "path", box c.FilePath
                        "line", box c.StartLine; "summary", box c.Summary
                        "tags", box c.Tags; "links", box c.LinkCount; "words", box c.WordCount ])

    // ── context ──

    let context (index: DocIndex) (session: QuerySession) (fileName: string) =
        // Detect ambiguous filename matches
        let matchingFiles =
            index.Chunks |> Array.map (fun c -> c.FilePath.Replace("\\", "/"))
            |> Array.distinct
            |> Array.filter (fun fp -> IndexStore.matchFile fp fileName)
        if matchingFiles.Length > 1 then
            let listing = matchingFiles |> Array.map (fun f -> sprintf "  %s" f) |> String.concat "\n"
            mdict [ "error", box (sprintf "'%s' is ambiguous (%d matches):\n%s\nUse a more specific path, e.g. context('%s')" fileName matchingFiles.Length listing matchingFiles.[0]) ]
        else
        let fileChunks = IndexStore.fileChunks index fileName
        let backlinks = IndexStore.backlinks index fileName
        let outlinks = IndexStore.outlinks index fileName
        let fm = index.Frontmatters |> Map.tryFind fileName
                 |> Option.orElseWith (fun () ->
                    index.Frontmatters |> Map.toSeq |> Seq.tryFind (fun (k, _) -> IndexStore.matchFile k fileName) |> Option.map snd)
        mdict [
            "file", box fileName
            "title", box (fm |> Option.map (fun f -> f.Title) |> Option.defaultValue "")
            "status", box (fm |> Option.map (fun f -> f.Status) |> Option.defaultValue "")
            "tags", box (fm |> Option.map (fun f -> f.Tags |> String.concat ", ") |> Option.defaultValue "")
            "related", box (fm |> Option.map (fun f -> f.Related |> String.concat ", ") |> Option.defaultValue "")
            "sections", box (fileChunks |> Array.map (fun (i, c) ->
                let id = session.NextRef(i)
                mdict [ "id", box id; "heading", box c.Heading; "level", box c.Level
                        "line", box c.StartLine; "summary", box c.Summary
                        "words", box c.WordCount; "links", box c.LinkCount ]))
            "backlinks", box (backlinks |> Array.map (fun l ->
                mdict [ "from", box (Path.GetFileName l.SourceFile); "section", box l.SourceHeading; "text", box l.LinkText ]))
            "outlinks", box (outlinks |> Array.map (fun l ->
                let resolved = if l.TargetResolved <> "" then Path.GetFileName l.TargetResolved else sprintf "⚠ %s" l.TargetPath
                mdict [ "to", box resolved; "text", box l.LinkText; "section", box l.SourceHeading ]))
        ]

    // ── expand ──

    let expand (index: DocIndex) (session: QuerySession) (chunks: DocChunk[] option) (refId: string) =
        match session.GetRef(refId) with
        | None -> mdict [ "error", box (sprintf "ref %s not found" refId) ]
        | Some chunkIdx ->
            let c = index.Chunks.[chunkIdx]
            let content = findSource chunks c |> Option.map (fun ch -> ch.Content) |> Option.defaultValue "(source not loaded)"
            let backlinks = IndexStore.backlinks index (Path.GetFileName c.FilePath)
            mdict [ "id", box refId; "heading", box c.Heading; "headingPath", box c.HeadingPath
                    "file", box (Path.GetFileName c.FilePath); "line", box c.StartLine
                    "endLine", box c.EndLine; "summary", box c.Summary
                    "tags", box c.Tags; "content", box content
                    "backlinks", box (backlinks |> Array.map (fun l -> sprintf "%s (%s)" (Path.GetFileName l.SourceFile) l.LinkText)) ]

    // ── neighborhood ──

    let neighborhood (index: DocIndex) (session: QuerySession) (chunks: DocChunk[] option) (refId: string) (beforeCount: int) (afterCount: int) =
        match session.GetRef(refId) with
        | None -> mdict [ "error", box (sprintf "ref %s not found" refId) ]
        | Some chunkIdx ->
            let target = index.Chunks.[chunkIdx]
            let fileChunks = index.Chunks |> Array.indexed |> Array.filter (fun (_, c) -> c.FilePath = target.FilePath) |> Array.sortBy (fun (_, c) -> c.StartLine)
            let targetPos = fileChunks |> Array.tryFindIndex (fun (i, _) -> i = chunkIdx) |> Option.defaultValue 0
            let mkCompact (i, c: ChunkEntry) =
                let id = session.NextRef(i)
                mdict [ "id", box id; "heading", box c.Heading; "level", box c.Level
                        "line", box c.StartLine; "summary", box c.Summary; "words", box c.WordCount ]
            let beforeChunks = fileChunks.[max 0 (targetPos - beforeCount) .. max 0 (targetPos - 1)] |> Array.map mkCompact
            let afterChunks = fileChunks.[min (fileChunks.Length - 1) (targetPos + 1) .. min (fileChunks.Length - 1) (targetPos + afterCount)] |> Array.filter (fun (i, _) -> i <> chunkIdx) |> Array.map mkCompact
            let targetContent = findSource chunks target |> Option.map (fun ch -> ch.Content) |> Option.defaultValue "(source not loaded)"
            mdict [ "file", box (Path.GetFileName target.FilePath)
                    "before", box beforeChunks
                    "target", box (mdict [ "id", box refId; "heading", box target.Heading; "level", box target.Level
                                           "line", box target.StartLine; "summary", box target.Summary; "content", box targetContent ])
                    "after", box afterChunks ]

    // ── similar ──

    let similar (index: DocIndex) (session: QuerySession) (refId: string) (limit: int) =
        match session.GetRef(refId) with
        | None -> [| mdict [ "error", box (sprintf "ref %s not found" refId) ] |]
        | Some chunkIdx ->
            IndexStore.similar index chunkIdx limit
            |> Array.map (fun (i, sim) ->
                let c = index.Chunks.[i]
                let id = session.NextRef(i)
                mdict [ "id", box id; "score", box (Math.Round(float sim, 3))
                        "heading", box c.Heading; "file", box (Path.GetFileName c.FilePath)
                        "line", box c.StartLine; "summary", box c.Summary; "tags", box c.Tags ])

    // ── grep ──

    let grep (index: DocIndex) (session: QuerySession) (chunks: DocChunk[] option) (pattern: string) (limit: int) (filePattern: string) =
        match chunks with
        | None -> [| mdict [ "error", box "source chunks not loaded — run 'knowledge-sight index' first" ] |]
        | Some allChunks ->
            let regex = try Regex(pattern, RegexOptions.IgnoreCase ||| RegexOptions.Compiled) with _ -> Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
            let results = ResizeArray()
            for i in 0..index.Chunks.Length-1 do
                if results.Count < limit then
                    let c = index.Chunks.[i]
                    if String.IsNullOrEmpty(filePattern) || c.FilePath.Contains(filePattern, StringComparison.OrdinalIgnoreCase) then
                        match findSource (Some allChunks) c with
                        | Some ch when regex.IsMatch(ch.Content) ->
                            let matchLine = ch.Content.Split('\n') |> Array.tryFind (fun l -> regex.IsMatch(l)) |> Option.map (fun l -> l.Trim()) |> Option.defaultValue ""
                            let id = session.NextRef(i)
                            results.Add(mdict [ "id", box id; "heading", box c.Heading; "file", box (Path.GetFileName c.FilePath)
                                                "path", box c.FilePath; "line", box c.StartLine; "matchLine", box matchLine
                                                "summary", box c.Summary; "tags", box c.Tags ])
                        | _ -> ()
            results.ToArray()

    // ── mentions (like refs in code-sight) ──

    let mentions (index: DocIndex) (session: QuerySession) (chunks: DocChunk[] option) (term: string) (limit: int) =
        match chunks with
        | None -> [| mdict [ "error", box "source chunks not loaded — run 'knowledge-sight index' first" ] |]
        | Some allChunks ->
            let regex = Regex(sprintf @"\b%s\b" (Regex.Escape term), RegexOptions.IgnoreCase ||| RegexOptions.Compiled)
            let results = ResizeArray()
            for i in 0..index.Chunks.Length-1 do
                if results.Count < limit then
                    let c = index.Chunks.[i]
                    match findSource (Some allChunks) c with
                    | Some ch when regex.IsMatch(ch.Content) ->
                        let matchLine = ch.Content.Split('\n') |> Array.tryFind (fun l -> regex.IsMatch(l)) |> Option.map (fun l -> l.Trim()) |> Option.defaultValue ""
                        let count = regex.Matches(ch.Content).Count
                        let id = session.NextRef(i)
                        results.Add(mdict [ "id", box id; "heading", box c.Heading; "file", box (Path.GetFileName c.FilePath)
                                            "line", box c.StartLine; "matchLine", box matchLine; "count", box count
                                            "summary", box c.Summary; "tags", box c.Tags ])
                    | _ -> ()
            results.ToArray()

    // ── files ──

    let files (index: DocIndex) (pattern: string) =
        index.Chunks |> Array.groupBy (fun c -> c.FilePath)
        |> Array.choose (fun (filePath, chunks) ->
            let fileName = Path.GetFileName(filePath)
            if String.IsNullOrEmpty(pattern) || fileName.Contains(pattern, StringComparison.OrdinalIgnoreCase) || filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase) then
                let fm = index.Frontmatters |> Map.tryFind filePath
                let title = fm |> Option.map (fun f -> f.Title) |> Option.defaultValue ""
                let tags = fm |> Option.map (fun f -> f.Tags |> String.concat ",") |> Option.defaultValue ""
                let backlinks = IndexStore.backlinks index filePath
                Some (mdict [ "file", box fileName; "path", box filePath; "sections", box chunks.Length
                              "title", box title; "tags", box tags; "backlinks", box backlinks.Length
                              "words", box (chunks |> Array.sumBy (fun c -> c.WordCount)) ])
            else None)
        |> Array.sortBy (fun d -> string d.["file"])

    // ── backlinks ──

    let backlinks (index: DocIndex) (session: QuerySession) (fileName: string) =
        IndexStore.backlinks index fileName
        |> Array.map (fun l ->
            mdict [ "from", box (Path.GetFileName l.SourceFile); "section", box l.SourceHeading
                    "text", box l.LinkText; "line", box l.Line
                    "resolved", box (if l.TargetResolved <> "" then "✓" else "✗") ])

    // ── links (outgoing) ──

    let links (index: DocIndex) (fileName: string) =
        IndexStore.outlinks index fileName
        |> Array.map (fun l ->
            let resolved = if l.TargetResolved <> "" then Path.GetFileName l.TargetResolved else sprintf "⚠ %s" l.TargetPath
            mdict [ "to", box resolved; "text", box l.LinkText; "section", box l.SourceHeading; "line", box l.Line ])

    // ── orphans — docs with no incoming links ──

    let orphans (index: DocIndex) =
        let allFiles = index.Chunks |> Array.map (fun c -> c.FilePath) |> Array.distinct
        let linkedFiles = index.Links |> Array.choose (fun l -> if l.TargetResolved <> "" then Some l.TargetResolved else None) |> Set.ofArray
        allFiles
        |> Array.filter (fun f -> not (linkedFiles.Contains f))
        |> Array.map (fun f ->
            let fm = index.Frontmatters |> Map.tryFind f
            let title = fm |> Option.map (fun f -> f.Title) |> Option.defaultValue ""
            let sections = index.Chunks |> Array.filter (fun c -> c.FilePath = f)
            mdict [ "file", box (Path.GetFileName f); "path", box f; "title", box title; "sections", box sections.Length ])

    // ── broken — links pointing to nonexistent docs ──

    let broken (index: DocIndex) =
        index.Links
        |> Array.filter (fun l -> l.TargetResolved = "")
        |> Array.map (fun l ->
            mdict [ "from", box (Path.GetFileName l.SourceFile); "target", box l.TargetPath
                    "text", box l.LinkText; "section", box l.SourceHeading; "line", box l.Line ])

    // ── placement — where should new content go? ──

    let placement (index: DocIndex) (embeddingUrl: string) (content: string) (limit: int) =
        match embedQuery embeddingUrl content with
        | None -> [| mdict [ "error", box "embedding server not available — placement requires embeddings" ] |]
        | Some qEmb ->
            // Find most similar sections, then group by file to suggest placement
            let hits = IndexStore.search index qEmb (limit * 3)
            let byFile =
                hits |> Array.groupBy (fun (i, _) -> index.Chunks.[i].FilePath)
                |> Array.map (fun (file, matches) ->
                    let avgScore = matches |> Array.averageBy (fun (_, s) -> float s)
                    let bestMatch = matches |> Array.maxBy snd
                    let bestChunk = index.Chunks.[fst bestMatch]
                    file, avgScore, bestChunk.Heading, bestChunk.HeadingPath)
                |> Array.sortByDescending (fun (_, score, _, _) -> score)
                |> Array.truncate limit
            byFile |> Array.map (fun (file, score, heading, headingPath) ->
                let fm = index.Frontmatters |> Map.tryFind file
                let title = fm |> Option.map (fun f -> f.Title) |> Option.defaultValue ""
                mdict [ "file", box (Path.GetFileName file); "score", box (Math.Round(score, 3))
                        "nearSection", box heading; "sectionPath", box headingPath; "title", box title ])

    // ── walk — traverse the link graph ──

    let walk (index: DocIndex) (session: QuerySession) (startFile: string) (maxDepth: int) (direction: string) =
        let visited = HashSet<string>()
        let results = ResizeArray<Dictionary<string, obj>>()

        let rec trace (file: string) (depth: int) (trail: string list) =
            if depth > maxDepth || visited.Contains(file) || results.Count >= maxDepth * 10 then ()
            else
                visited.Add(file) |> ignore
                let neighbors =
                    if direction = "in" then
                        IndexStore.backlinks index file |> Array.map (fun l -> l.SourceFile, l.LinkText)
                    else
                        IndexStore.outlinks index file |> Array.map (fun l -> l.TargetResolved, l.LinkText)
                    |> Array.filter (fun (f, _) -> f <> "" && not (visited.Contains f))
                    |> Array.distinctBy fst

                for (nextFile, linkText) in neighbors do
                    let nextTrail = trail @ [sprintf "%s (%s)" (Path.GetFileName nextFile) linkText]
                    results.Add(mdict [
                        "hop", box depth; "file", box (Path.GetFileName nextFile)
                        "path", box nextFile; "via", box linkText
                        "trail", box (nextTrail |> String.concat " → ")
                    ])
                    trace nextFile (depth + 1) nextTrail

        let startResolved =
            index.Chunks |> Array.tryFind (fun c -> IndexStore.matchFile c.FilePath startFile) |> Option.map (fun c -> c.FilePath) |> Option.defaultValue startFile
        trace startResolved 1 [Path.GetFileName startResolved]
        results.ToArray()

    // ── novelty — what's new in this text vs existing knowledge? ──

    /// Heuristic: does this paragraph look like knowledge vs casual musing?
    let private knowledgeSignal (para: string) (index: DocIndex) =
        let lower = para.ToLowerInvariant()
        let mutable score = 0

        // Prescriptive language (knowledge patterns)
        let prescriptive = [| " should "; " must "; " always "; " never "; " when "; " ensure "; " requires "; " depends on "; " means that " |]
        for p in prescriptive do if lower.Contains(p) then score <- score + 2

        // Causal connectors (reasoning)
        let causal = [| " because "; " therefore "; " so that "; " in order to "; " consequence "; " implies "; " leads to " |]
        for c in causal do if lower.Contains(c) then score <- score + 2

        // Declarative structure (definitions/facts)
        let declarative = [| " is a "; " are "; " defines "; " represents "; " consists of "; " handles "; " processes " |]
        for d in declarative do if lower.Contains(d) then score <- score + 1

        // Hedging / uncertainty (musing patterns — deduct)
        let hedging = [| " maybe "; " perhaps "; " i wonder "; " not sure "; " might "; " could be "; " i think "; "?" |]
        for h in hedging do if lower.Contains(h) then score <- score - 2

        // Concrete code references (file names, types from the index)
        let codeRefRegex = Regex(@"\b\w+\.(fs|cs|js|ts|py|md)\b", RegexOptions.Compiled)
        let codeRefs = codeRefRegex.Matches(para).Count
        score <- score + codeRefs * 2

        // Type/module names from the index
        let indexNames = index.Chunks |> Array.map (fun c -> c.Heading) |> Array.distinct
        let nameHits = indexNames |> Array.filter (fun name -> name.Length > 3 && para.Contains(name, StringComparison.OrdinalIgnoreCase))
        score <- score + nameHits.Length

        // Length bonus — very short paragraphs are rarely knowledge
        if para.Length < 50 then score <- score - 2

        score

    /// Split text into paragraphs, embed each, compare to index.
    /// Classifies each paragraph as: off-topic, musing, novel, or covered.
    let novelty (index: DocIndex) (embeddingUrl: string) (text: string) (threshold: float) =
        let paragraphs =
            text.Split([| "\n\n"; "\r\n\r\n" |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun p -> p.Trim())
            |> Array.filter (fun p -> p.Length > 30 && not (p.StartsWith("```")) && not (p.StartsWith("|")))

        if paragraphs.Length = 0 then [||]
        else
            let prefixed = paragraphs |> Array.map (fun p -> sprintf "search_query: %s" (p.Substring(0, min 200 p.Length)))
            let embeddings =
                match EmbeddingService.embed embeddingUrl prefixed |> Async.AwaitTask |> Async.RunSynchronously with
                | Ok embs -> embs
                | Error _ -> [||]

            if embeddings.Length = 0 then [||]
            else
                paragraphs |> Array.mapi (fun i para ->
                    if i >= embeddings.Length || embeddings.[i].Length = 0 then
                        mdict [ "paragraph", box (para.Substring(0, min 80 para.Length) + "..."); "status", box "error"; "score", box 0.0 ]
                    else
                        let hits = IndexStore.search index embeddings.[i] 1
                        let bestScore, bestChunk =
                            if hits.Length > 0 then
                                let idx, sim = hits.[0]
                                float sim, Some index.Chunks.[idx]
                            else 0.0, None

                        let kSignal = knowledgeSignal para index
                        let status =
                            if bestScore < 0.5 then "off-topic"       // not in the project's semantic space
                            elif kSignal < 0 then "musing"            // in-space but reads like discussion, not knowledge
                            elif kSignal < 1 && bestScore < 0.6 then "musing" // weak signal + low relevance = not knowledge
                            elif bestScore >= threshold then "covered" // already captured
                            else "novel"                              // relevant, looks like knowledge, not yet captured

                        let preview = if para.Length > 120 then para.Substring(0, 120) + "..." else para
                        let nearDoc = bestChunk |> Option.map (fun c -> Path.GetFileName c.FilePath) |> Option.defaultValue ""
                        let nearHeading = bestChunk |> Option.map (fun c -> c.Heading) |> Option.defaultValue ""
                        mdict [ "paragraph", box preview; "status", box status
                                "score", box (Math.Round(bestScore, 3)); "signal", box kSignal
                                "nearDoc", box nearDoc; "nearSection", box nearHeading ])

    // ── gaps — cross-document entity coverage analysis ──

    /// Extract entity references from markdown text via regex.
    /// Returns normalized entity names: identifiers, file names, modules, paths.
    let private extractEntityRefs (text: string) =
        let refs = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        // Backticked file names: `SearchTools.fs`, `config.yaml`
        for m in Regex.Matches(text, @"`([A-Za-z]\w+\.(?:fs|cs|fsx|js|ts|py|go|rs|yaml|json|md|toml))`") do
            refs.Add(m.Groups.[1].Value) |> ignore

        // Bare source file names in prose: SearchTools.fs, AppOrchestrator.cs
        for m in Regex.Matches(text, @"\b([A-Z][A-Za-z]+\.(?:fs|cs|js|ts|py))\b") do
            refs.Add(m.Groups.[1].Value) |> ignore

        // Module-style names: AITeam.Orchestration, System.IO
        for m in Regex.Matches(text, @"\b((?:[A-Z][A-Za-z]+\.){1,4}[A-Z][A-Za-z]+)\b") do
            let full = m.Groups.[1].Value
            if not (full.EndsWith(".fs") || full.EndsWith(".cs") || full.EndsWith(".js") || full.EndsWith(".md")) then
                refs.Add(full) |> ignore
                // Also add short form (strip common prefixes)
                for prefix in [| "AITeam."; "System."; "Microsoft." |] do
                    if full.StartsWith(prefix) then
                        refs.Add(full.Substring(prefix.Length)) |> ignore

        // Backticked CamelCase identifiers: `DeliveryEngine`, `ICapabilityResolver`
        for m in Regex.Matches(text, @"`([A-Z][A-Za-z]{2,}(?:\.[A-Z][A-Za-z]+)*)`") do
            let v = m.Groups.[1].Value
            if not (Regex.IsMatch(v, @"\.\w{1,4}$")) then // skip file extensions already caught
                refs.Add(v) |> ignore

        // Path references: `src/foo/Bar.fs`, `.agents/tools/thing.py`
        for m in Regex.Matches(text, @"`((?:\.agents|src|tests|architecture)/[^\s`]+)`") do
            refs.Add(Path.GetFileName(m.Groups.[1].Value)) |> ignore

        refs |> Seq.toArray

    /// Normalize an entity name for deduplication.
    /// Strips common prefixes and file extensions so `DeliveryEngine.fs` and `DeliveryEngine` merge.
    let private normalizeEntity (name: string) =
        let stripped =
            [| "AITeam."; "System."; "Microsoft." |]
            |> Array.fold (fun (s: string) prefix ->
                if s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then s.Substring(prefix.Length) else s) name
        // Strip known file extensions for dedup
        let noExt =
            Regex.Replace(stripped, @"\.(fs|cs|fsx|js|ts|py|go|rs|yaml|json|md|toml)$", "", RegexOptions.IgnoreCase)
        noExt.ToLowerInvariant()

    /// Cross-document gap analysis. Works on whatever's in the index.
    /// Groups chunks by source file, extracts entity refs, builds a bipartite
    /// entity→files index, classifies: shared / isolated / god-node.
    let gaps (index: DocIndex) (chunks: DocChunk[] option) (scope: string) (minDocs: int) (signal: string) =
        // Use source chunks for content (richer), fall back to summaries from index
        let contentByChunk =
            match chunks with
            | Some chs ->
                chs |> Array.map (fun c -> c.FilePath, c.Content)
            | None ->
                index.Chunks |> Array.map (fun c -> c.FilePath, c.Summary)

        // Group content by source file
        let fileContents =
            contentByChunk
            |> Array.groupBy fst
            |> Array.map (fun (file, pairs) ->
                let combined = pairs |> Array.map snd |> String.concat "\n"
                file, combined)

        let totalFiles = fileContents.Length
        if totalFiles < 2 then
            [| mdict [ "note", box "Need at least 2 indexed files for gap analysis."; "files", box totalFiles ] |]
        else

        // Extract entity refs per file (filter noise: min 3 chars, no empty)
        let fileEntities =
            fileContents
            |> Array.map (fun (file, content) ->
                let entities =
                    extractEntityRefs content
                    |> Array.map normalizeEntity
                    |> Array.filter (fun e -> e.Length >= 3)
                    |> Array.distinct
                file, entities)

        // Build bipartite index: entity → set<file>
        let entityToFiles = Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        for (file, entities) in fileEntities do
            let displayName = Path.GetFileNameWithoutExtension(file)
            for entity in entities do
                if not (entityToFiles.ContainsKey(entity)) then
                    entityToFiles.[entity] <- HashSet<string>(StringComparer.OrdinalIgnoreCase)
                entityToFiles.[entity].Add(displayName) |> ignore

        // Filter by scope if provided (exact or prefix match, not substring)
        // Normalize scope input the same way entities are normalized (lowercase, strip prefixes/extensions)
        let scopeNorm = if String.IsNullOrWhiteSpace(scope) then "" else normalizeEntity scope
        let filtered =
            entityToFiles
            |> Seq.filter (fun kv ->
                if scopeNorm = "" then true
                else kv.Key = scopeNorm || kv.Key.StartsWith(scopeNorm + ".") || scopeNorm.StartsWith(kv.Key + "."))
            |> Seq.filter (fun kv -> kv.Value.Count >= 1)
            |> Seq.toArray

        // Classify and build results
        // God-node: top 5% of files or at least 5 — the most-connected concepts
        let godThreshold = max 5 (int (ceil (float totalFiles * 0.05)))

        // Importance heuristic for ranking isolated entities:
        // - Longer names are more specific/meaningful (penalize short generic terms)
        // - Names matching indexed file names are more important
        // - Dotted names (module.method) suggest concrete code references
        // - Names with mixed segments suggest compound identifiers
        let indexedFileNames =
            fileContents |> Array.map (fun (f, _) -> Path.GetFileNameWithoutExtension(f).ToLowerInvariant()) |> Set.ofArray
        let entityImportance (entity: string) =
            let lengthScore = min 5 (entity.Length / 3) // 0-5 points for length
            let fileBonus = if indexedFileNames.Contains(entity) then 3 else 0
            let structureBonus =
                if entity.Contains(".") then 2  // dotted module name (e.g., deliveryengine.send)
                elif entity.Length > 12 then 1  // long single word likely a compound identifier
                else 0
            lengthScore + fileBonus + structureBonus

        let preSignal =
            filtered
            |> Array.map (fun kv ->
                let entity = kv.Key
                let files = kv.Value |> Seq.sort |> Seq.toArray
                let count = files.Length
                let sig' =
                    if count >= godThreshold then "god-node"
                    elif count >= 2 then "shared"
                    else "isolated"
                let importance = entityImportance entity
                entity, files, count, sig', importance)

        let results =
            preSignal
            // Apply signal filter
            |> Array.filter (fun (_, _, count, sig', _) ->
                (signal = "" || signal = sig') && count >= minDocs)
            // Sort: god-nodes first, then shared by count desc, then isolated by importance desc
            |> Array.sortBy (fun (entity, _, count, sig', importance) ->
                let priority = match sig' with "god-node" -> 0 | "shared" -> 1 | _ -> 2
                priority, -count, -importance, entity)

        if results.Length = 0 then
            let totalEntities = entityToFiles.Count
            let scopeMatched = preSignal.Length
            let msg =
                if scopeNorm <> "" && scopeMatched = 0 then
                    sprintf "No entities matching scope '%s' found. The index has %d entities across %d files. Scope uses exact/prefix matching on normalized (lowercase) names — try a shorter prefix or gaps() with no scope to see available entities." scope totalEntities totalFiles
                elif scopeNorm <> "" && signal <> "" then
                    let actualSignals = preSignal |> Array.map (fun (_, _, _, s, _) -> s) |> Array.distinct |> String.concat ", "
                    sprintf "Scope '%s' matched %d entities, but none have signal '%s'. Found signals: %s. Try gaps({scope: '%s'}) without signal filter." scope scopeMatched signal actualSignals scope
                elif signal <> "" then
                    sprintf "No entities with signal '%s' found (minDocs=%d). The index has %d entities across %d files." signal minDocs totalEntities totalFiles
                else "No cross-document entity references found."
            [| mdict [ "note", box msg; "total_entities", box totalEntities; "total_files", box totalFiles ] |]
        else
            results
            |> Array.map (fun (entity, files, count, sig', importance) ->
                mdict [ "entity", box entity
                        "sources", box (files |> String.concat ", ")
                        "count", box count
                        "signal", box sig'
                        "importance", box importance
                        "total_files", box totalFiles ])

    // ── cluster — suggest subfolder groupings for an overcrowded directory ──

    /// Cosine similarity between two vectors.
    let private cosine (a: float32[]) (b: float32[]) =
        if a.Length = 0 || b.Length = 0 then 0.0f
        else
            let mutable dot = 0.0f
            let mutable na = 0.0f
            let mutable nb = 0.0f
            for i in 0 .. a.Length - 1 do
                dot <- dot + a.[i] * b.[i]
                na <- na + a.[i] * a.[i]
                nb <- nb + b.[i] * b.[i]
            if na = 0.0f || nb = 0.0f then 0.0f
            else dot / (sqrt na * sqrt nb)

    /// Simple greedy clustering: assign each doc to the nearest existing cluster center,
    /// or start a new cluster if similarity to all centers is below threshold.
    let private greedyCluster (items: (string * float32[])[]) (threshold: float) =
        let clusters = ResizeArray<ResizeArray<string> * float32[]>()
        for (name, emb) in items do
            let mutable bestIdx = -1
            let mutable bestSim = 0.0f
            for ci in 0 .. clusters.Count - 1 do
                let _, center = clusters.[ci]
                let sim = cosine emb center
                if sim > bestSim then bestSim <- sim; bestIdx <- ci
            if float bestSim >= threshold && bestIdx >= 0 then
                let members, _ = clusters.[bestIdx]
                members.Add(name)
            else
                let members = ResizeArray<string>()
                members.Add(name)
                clusters.Add((members, emb))
        clusters |> Seq.map (fun (members, _) -> members.ToArray()) |> Seq.toArray

    /// Suggest subfolder groupings for docs in a directory.
    /// Uses embeddings to cluster docs by semantic similarity.
    let cluster (index: DocIndex) (dir: string) (threshold: float) =
        let normDir = dir.Replace("\\", "/").TrimEnd('/')
        // Find docs in the target directory
        let docsInDir =
            index.Chunks
            |> Array.filter (fun c -> c.Level <= 1) // top-level sections only (one per doc)
            |> Array.filter (fun c ->
                let rel = c.FilePath.Replace("\\", "/")
                // Match docs directly in the target dir (not in subdirs)
                if normDir = "" || normDir = "." then
                    not (Path.GetFileName(rel) <> rel) // root-level only
                else
                    // Check if file is in this dir (works with both absolute and relative paths)
                    let dirWithSlash = normDir + "/"
                    let inDir = rel.StartsWith(dirWithSlash) || rel.Contains("/" + dirWithSlash)
                    if not inDir then false
                    else
                        // Only direct children, not in subdirs
                        let startIdx =
                            let i = rel.IndexOf(dirWithSlash)
                            if i >= 0 then i + dirWithSlash.Length else dirWithSlash.Length
                        let afterDir = rel.Substring(startIdx)
                        not (afterDir.Contains("/")))
            |> Array.distinctBy (fun c -> c.FilePath)

        if docsInDir.Length < 4 then
            // Not enough docs to warrant splitting
            [| mdict [ "suggestion", box "Folder has fewer than 4 docs — no split needed."; "docs", box docsInDir.Length ] |]
        else
            // Get embeddings for these docs
            let docEmbeddings =
                docsInDir |> Array.choose (fun c ->
                    let idx = index.Chunks |> Array.tryFindIndex (fun ch -> ch.FilePath = c.FilePath && ch.Heading = c.Heading)
                    match idx with
                    | Some i when i < index.Embeddings.Length && index.Embeddings.[i].Length > 0 ->
                        Some (Path.GetFileName c.FilePath, index.Embeddings.[i])
                    | _ -> None)

            if docEmbeddings.Length < 4 then
                [| mdict [ "suggestion", box "Not enough embedded docs to cluster."; "docs", box docEmbeddings.Length ] |]
            else
                let clusters = greedyCluster docEmbeddings threshold
                // Find common terms in each cluster for suggested folder names
                clusters
                |> Array.mapi (fun i members ->
                    let nameHint =
                        if members.Length = 1 then members.[0].Replace(".md", "")
                        else
                            // Find common prefix or common word
                            let words =
                                members
                                |> Array.collect (fun m -> m.Replace(".md", "").Replace("-", " ").Split(' '))
                                |> Array.countBy id
                                |> Array.sortByDescending snd
                                |> Array.truncate 2
                                |> Array.map fst
                            if words.Length > 0 then words |> String.concat "-"
                            else sprintf "group-%d" (i + 1)
                    mdict [ "suggestedFolder", box nameHint
                            "docs", box members.Length
                            "files", box (members |> String.concat ", ") ])

    // ── hygiene — role-aware, report-first maintenance workflow ──

    type private DocProfile = {
        FilePath: string
        RelativePath: string
        Title: string
        Tags: string[]
        Backlinks: int
        Sections: DocChunk[]
        Role: string
        RoleConfidence: float
        RoleEvidence: string[]
    }

    let private clamp01 (value: float) = Math.Max(0.0, Math.Min(1.0, value))

    let private lower (text: string) = if isNull text then "" else text.ToLowerInvariant()

    let private containsAny (text: string) (terms: string[]) =
        let haystack = lower text
        terms |> Array.exists haystack.Contains

    let private firstSnippet (text: string) =
        text.Split('\n')
        |> Array.map (fun line -> line.Trim())
        |> Array.tryFind (fun line -> line <> "")
        |> Option.defaultValue ""
        |> fun snippet ->
            if snippet.Length > 120 then snippet.Substring(0, 117) + "..."
            else snippet

    let private normalizedTokens (text: string) =
        Regex.Matches(lower text, @"[a-z0-9]{3,}")
        |> Seq.cast<Match>
        |> Seq.map (fun m -> m.Value)
        |> Set.ofSeq

    let private jaccard (left: Set<string>) (right: Set<string>) =
        if Set.isEmpty left || Set.isEmpty right then 0.0
        else
            let overlap = Set.intersect left right |> Set.count
            let universe = Set.union left right |> Set.count
            if universe = 0 then 0.0 else float overlap / float universe

    let private normalizeStatusText (text: string) =
        text.ToLowerInvariant()
        |> fun value -> Regex.Replace(value, @"\b\d+\b", "#")
        |> fun value -> Regex.Replace(value, @"[^\w\s#]", " ")
        |> fun value -> Regex.Replace(value, @"\s+", " ").Trim()

    let private relativePath (repoRoot: string) (filePath: string) =
        let normalizedRepoRoot = Path.GetFullPath(repoRoot)
        if Path.IsPathRooted filePath then Path.GetRelativePath(normalizedRepoRoot, filePath).Replace("\\", "/")
        else filePath.Replace("\\", "/")

    let private classifyDocRole (filePath: string) (title: string) (tags: string[]) (sections: DocChunk[]) (backlinks: int) =
        let scores = Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        let reasons = Dictionary<string, ResizeArray<string>>(StringComparer.OrdinalIgnoreCase)

        let add role points reason =
            let current = if scores.ContainsKey(role) then scores.[role] else 0
            scores.[role] <- current + points
            if not (reasons.ContainsKey(role)) then reasons.[role] <- ResizeArray<string>()
            reasons.[role].Add(reason)

        let titleLower = lower title
        let pathLower = lower filePath
        let textLower =
            sections
            |> Array.collect (fun section -> [| section.Heading; section.Content |])
            |> String.concat "\n"
            |> lower
        let tagLower = tags |> Array.map lower
        let tagsContain (terms: string[]) =
            terms
            |> Array.exists (fun (term: string) ->
                tagLower |> Array.exists (fun (tag: string) -> tag.Contains(term)))

        if containsAny pathLower [| "/status/"; "current-status"; "progress"; "status-report" |]
           || containsAny titleLower [| "current status"; "status"; "progress" |]
           || tagsContain [| "status"; "current" |] then
            add "canonical_live_status_owner" 3 "Title/path/tags indicate a live-status owner."
        if containsAny textLower [| "canonical owner"; "canonical status"; "other docs should point here" |] then
            add "canonical_live_status_owner" 3 "Content explicitly claims canonical live-status ownership."
        if backlinks > 0 && containsAny textLower [| "current status"; "active wave"; "tests baseline"; "delivered" |] then
            add "canonical_live_status_owner" (min 2 backlinks) (sprintf "Linked from %d other document(s)." backlinks)

        if containsAny pathLower [| "/archive/"; "history"; "historical"; "retro"; "review" |]
           || containsAny titleLower [| "archive"; "history"; "historical"; "review"; "retro" |]
           || tagsContain [| "archive"; "history"; "review" |] then
            add "historical_archive" 4 "File/title/tags look archival or historical."
        if containsAny textLower [| "historical snapshot"; "written at the time"; "past status"; "archive handling" |] then
            add "historical_archive" 2 "Content describes historical or archival status."

        if containsAny pathLower [| "spec"; "format"; "schema" |]
           || containsAny titleLower [| "spec"; "format"; "schema" |]
           || tagsContain [| "spec"; "schema"; "format" |] then
            add "implemented_spec" 2 "File/title/tags look specification-oriented."
        if containsAny textLower [| "api sketch"; "request"; "response"; "json"; "payload"; "endpoint"; "test baseline"; "fixtures" |] then
            add "implemented_spec" 3 "Content contains implementation scaffolding or API/test material."

        if containsAny pathLower [| "vision"; "design"; "analysis"; "deep-dive"; "deep_dive" |]
           || containsAny titleLower [| "vision"; "design"; "analysis"; "deep dive" |]
           || tagsContain [| "vision"; "design"; "analysis"; "deep-dive" |] then
            add "research_deep_dive" 2 "File/title/tags look like a design or deep-dive document."
        if containsAny textLower [| "core idea"; "invariant"; "deterministic"; "why this stays separate"; "tradeoff"; "must preserve" |] then
            add "research_deep_dive" 3 "Content contains deep-dive or invariant-oriented language."

        if containsAny pathLower [| "/research/"; "research-note"; "research-notes"; "experiment-log"; "notebook"; "hypothesis" |]
           || containsAny titleLower [| "research"; "experiment"; "hypothesis"; "notebook"; "negative result"; "open problem"; "known fragile" |]
           || tagsContain [| "research"; "experiment"; "hypothesis"; "notebook"; "fragile" |] then
            add "research_note" 3 "File/title/tags look like a research notebook or experiment note."
        if containsAny textLower [| "research note"; "experiment log"; "negative result"; "failed experiment"; "known fragile"; "open problem"; "hypothesis"; "artifact missing"; "evidence gap" |] then
            add "research_note" 3 "Content contains research-memory or experiment-log language."

        if containsAny pathLower [| "/decision-index/"; "decision-index"; "decision-register"; "decision-hub"; "decision-catalog"; "decision-registry" |]
           || containsAny titleLower [| "decision index"; "decision register"; "decision hub"; "decision catalog"; "decision registry"; "derived decision index" |]
           || tagsContain [| "decision-index"; "decision-register"; "decision-registry"; "derived-index" |] then
            add "decision_index" 4 "File/title/tags look like a decision index or register."
        if containsAny textLower [| "decision index"; "decision register"; "register entry"; "registry entry"; "derived index"; "historical snapshot"; "artifact id"; "canonical register" |] then
            add "decision_index" 3 "Content contains decision-index or register language."

        if containsAny pathLower [| "roadmap"; "scope"; "plan"; "brief"; "context"; "release"; "runbook"; "handoff"; "incident" |]
           || containsAny titleLower [| "roadmap"; "scope"; "plan"; "brief"; "context"; "release"; "runbook"; "handoff"; "incident" |]
           || tagsContain [| "roadmap"; "scope"; "plan"; "brief"; "context"; "release"; "runbook"; "incident"; "operations" |] then
            add "product_or_control_plane_doc" 3 "File/title/tags look roadmap or planning oriented."
        if containsAny textLower [| "bounded summary"; "summary only"; "non-authoritative"; "not the canonical owner"; "planning context"; "customer-facing summary"; "release note"; "runbook context"; "operator handoff"; "incident context" |] then
            add "product_or_control_plane_doc" 3 "Content explicitly marks the status note as bounded/non-authoritative."

        if containsAny pathLower [| "readme"; "index"; "overview" |]
           || containsAny titleLower [| "readme"; "index"; "overview" |] then
            add "entrypoint_or_index_doc" 3 "File/title looks like an entrypoint or index."

        if containsAny pathLower [| "adr"; "decision"; "proposal"; "review" |]
           || containsAny titleLower [| "adr"; "decision"; "proposal"; "review" |]
           || tagsContain [| "decision"; "proposal"; "review" |] then
            add "review_or_decision_record" 3 "File/title/tags look like review or decision material."
        if containsAny textLower [| "accepted"; "rejected"; "decision"; "proposal" |] then
            add "review_or_decision_record" 2 "Content uses review/decision terminology."

        let ranked =
            scores
            |> Seq.map (fun kv ->
                let evidence =
                    if reasons.ContainsKey(kv.Key) then reasons.[kv.Key] |> Seq.distinct |> Seq.toArray
                    else [||]
                kv.Key, kv.Value, evidence)
            |> Seq.sortByDescending (fun (_, score, _) -> score)
            |> Seq.toArray

        let topScore =
            if ranked.Length = 0 then 0
            else
                let _, score, _ = ranked.[0]
                score

        if ranked.Length = 0 || topScore < 3 then
            "unknown", 0.25, [| "No strong role signals were found; keeping the role honest as unknown." |]
        elif ranked.Length > 1 then
            let topRole, topScore, topEvidence = ranked.[0]
            let secondRole, secondScore, secondEvidence = ranked.[1]
            if secondScore >= topScore - 1 && secondScore >= 3 then
                let evidence =
                    Array.append
                        [| sprintf "Competing role signals: %s (%d) vs %s (%d)." topRole topScore secondRole secondScore |]
                        (Array.append topEvidence secondEvidence |> Array.distinct)
                "mixed", 0.55, evidence
            else
                let confidence = clamp01 (0.45 + (float topScore / 10.0) - (float secondScore / 20.0))
                topRole, confidence, topEvidence
        else
            let topRole, topScore, topEvidence = ranked.[0]
            let confidence = clamp01 (0.45 + (float topScore / 10.0))
            topRole, confidence, topEvidence

    let private buildDocProfiles (index: DocIndex) (repoRoot: string) (allChunks: DocChunk[]) =
        allChunks
        |> Array.groupBy (fun chunk -> normPath chunk.FilePath)
        |> Array.map (fun (_, sections) ->
            let filePath = sections.[0].FilePath
            let fm = index.Frontmatters |> Map.tryFind filePath
            let title =
                fm
                |> Option.map (fun frontmatter ->
                    if String.IsNullOrWhiteSpace(frontmatter.Title) then Path.GetFileNameWithoutExtension(filePath)
                    else frontmatter.Title)
                |> Option.defaultValue (Path.GetFileNameWithoutExtension(filePath))
            let tags = fm |> Option.map (fun frontmatter -> frontmatter.Tags) |> Option.defaultValue [||]
            let backlinks = IndexStore.backlinks index filePath |> Array.length
            let role, roleConfidence, roleEvidence = classifyDocRole filePath title tags sections backlinks
            {
                FilePath = filePath
                RelativePath = relativePath repoRoot filePath
                Title = title
                Tags = tags
                Backlinks = backlinks
                Sections = sections |> Array.sortBy (fun section -> section.StartLine)
                Role = role
                RoleConfidence = roleConfidence
                RoleEvidence = roleEvidence
            })

    let private statusSectionScore (section: DocChunk) =
        let headingLower = lower section.Heading
        let textLower = lower (section.Heading + "\n" + section.Content)
        let mutable score = 0
        if containsAny headingLower [| "status"; "progress"; "current"; "wave"; "gate"; "baseline"; "platform note"; "context snapshot" |] then score <- score + 2
        if containsAny textLower [| "current status"; "current gate"; "active wave"; "delivered"; "tests baseline"; "current platform note"; "currently" |] then score <- score + 2
        if containsAny textLower [| "tests"; "passing"; "baseline"; "wave"; "gate" |] then score <- score + 1
        score

    let private statusPhraseHits (text: string) =
        [| "current status"; "current gate"; "active wave"; "tests baseline"; "delivered"; "current platform note"; "currently" |]
        |> Array.filter (fun phrase -> lower text |> fun haystack -> haystack.Contains(phrase))

    let private statusSimilarity (left: DocChunk) (right: DocChunk) =
        let leftTokens = normalizedTokens (normalizeStatusText (left.Heading + "\n" + left.Content))
        let rightTokens = normalizedTokens (normalizeStatusText (right.Heading + "\n" + right.Content))
        jaccard leftTokens rightTokens

    let private sectionSignals (section: DocChunk) =
        let textLower = lower (section.Heading + "\n" + section.Content)
        let uniqueHits =
            [| "must"; "invariant"; "deterministic"; "tradeoff"; "why"; "core idea"; "preserve"; "separate" |]
            |> Array.filter (fun term -> textLower.Contains(term))
        let genericHits =
            [| "api"; "endpoint"; "json"; "payload"; "request"; "response"; "schema"; "test baseline"; "fixtures"; "example" |]
            |> Array.filter (fun term -> textLower.Contains(term))
        uniqueHits, genericHits

    let private decisionArtifactReferenceCount (doc: DocProfile) =
        doc.Sections
        |> Array.sumBy (fun section -> Regex.Matches(section.Content, @"\ba-[a-z0-9]{8}\b", RegexOptions.IgnoreCase).Count)

    let private decisionSectionSignals (section: DocChunk) =
        let headingLower = lower section.Heading
        let textLower = lower (section.Heading + "\n" + section.Content)
        let decisionHits =
            [|
                "decision"; "rationale"; "consequences"; "status"; "accepted"; "rejected"; "withdrawn"; "promoted";
                "strategic bet"; "candidate"; "considered"; "artifact"; "adr"; "proposal"
            |]
            |> Array.filter (fun term -> headingLower.Contains(term) || textLower.Contains(term))
        decisionHits

    let private researchSectionSignals (section: DocChunk) =
        let headingLower = lower section.Heading
        let textLower = lower (section.Heading + "\n" + section.Content)
        let researchHits =
            [|
                "research note"; "research notebook"; "research log"; "experiment"; "experiment log"; "finding";
                "observation"; "hypothesis"; "negative result"; "failed experiment"; "disproven"; "known fragile";
                "open problem"; "candidate memory"; "probe"; "investigation"; "evidence gap"; "artifact missing"
            |]
            |> Array.filter (fun term -> headingLower.Contains(term) || textLower.Contains(term))
        let conflictHits =
            [|
                "conflicts with"; "contradicts"; "disagrees with"; "competing hypothesis"; "conflicting note";
                "unresolved contradiction"; "needs human review"
            |]
            |> Array.filter (fun term -> textLower.Contains(term))
        let evidenceGapHits =
            [|
                "artifact missing"; "missing artifact"; "evidence gap"; "evidence link missing";
                "trace capture link is missing"; "restore evidence"; "restore the link"
            |]
            |> Array.filter (fun term -> textLower.Contains(term))
        researchHits, conflictHits, evidenceGapHits

    let private decisionIndexSectionSignals (section: DocChunk) =
        let headingLower = lower section.Heading
        let textLower = lower (section.Heading + "\n" + section.Content)
        let indexHits =
            [|
                "decision index"; "decision register"; "decision hub"; "decision catalog"; "decision registry";
                "register entry"; "registry entry"; "index entry"; "canonical register"; "decision target"
            |]
            |> Array.filter (fun term -> headingLower.Contains(term) || textLower.Contains(term))
        let contradictionHits =
            [|
                "contradiction"; "linked target disagrees"; "target disagrees"; "status mismatch";
                "reconcile this contradiction"; "correction guidance"; "needs human review"
            |]
            |> Array.filter (fun term -> textLower.Contains(term))
        let missingTargetHits =
            [|
                "artifact id unknown"; "artifact id missing"; "unknown artifact id"; "missing artifact id";
                "broken link"; "moved link"; "target moved"; "target missing"; "repair the target"; "repair the link"
            |]
            |> Array.filter (fun term -> textLower.Contains(term))
        let derivedHits =
            [|
                "derived index"; "derived register"; "derived decision index"; "generated from";
                "not the canonical decision register"; "not the canonical register"
            |]
            |> Array.filter (fun term -> textLower.Contains(term))
        let historicalHits =
            [|
                "historical snapshot"; "dated provenance"; "written at the time"; "recorded on"; "as of "
            |]
            |> Array.filter (fun term -> textLower.Contains(term))
        let datedHits =
            [|
                if Regex.IsMatch(textLower, @"\b20\d{2}[-/]\d{2}[-/]\d{2}\b", RegexOptions.IgnoreCase) then
                    "dated snapshot marker"
            |]
        indexHits, contradictionHits, missingTargetHits, derivedHits, Array.append historicalHits datedHits

    let private sectionBrokenLinks (index: DocIndex) (sourceFile: string) (section: DocChunk) =
        index.Links
        |> Array.filter (fun link ->
            normPath link.SourceFile = normPath sourceFile
            && link.SourceHeading = section.Heading
            && String.IsNullOrWhiteSpace(link.TargetResolved))

    let private docBrokenLinks (index: DocIndex) (sourceFile: string) =
        index.Links
        |> Array.filter (fun link ->
            normPath link.SourceFile = normPath sourceFile
            && String.IsNullOrWhiteSpace(link.TargetResolved))

    let private claimsCurrentAuthority (text: string) =
        let textLower = lower text
        let negated =
            containsAny textLower [|
                "not current"; "not the current"; "not latest"; "not the latest"; "not next"; "not the next"
            |]
        let positive =
            containsAny textLower [|
                "current authority"; "latest authority"; "next authority"; "current gate"; "active wave";
                "current status"; "latest status"; "next gate"; "current recommendation"
            |]
        positive && not negated

    let private sectionPairSimilarity (left: DocChunk) (right: DocChunk) =
        let leftTokens = normalizedTokens (left.Heading + "\n" + left.Content)
        let rightTokens = normalizedTokens (right.Heading + "\n" + right.Content)
        jaccard leftTokens rightTokens

    let private sectionSimilarity (section: DocChunk) (otherSections: DocChunk[]) =
        otherSections
        |> Array.filter (fun other ->
            not (normPath other.FilePath = normPath section.FilePath
                 && other.StartLine = section.StartLine
                 && other.Heading = section.Heading))
        |> Array.map (sectionPairSimilarity section)
        |> Array.fold max 0.0

    let private primarySectionPath (doc: DocProfile) =
        doc.Sections
        |> Array.tryFind (fun section -> section.Level > 0 && firstSnippet section.Content <> "")
        |> Option.orElseWith (fun () -> doc.Sections |> Array.tryFind (fun section -> section.Level > 0))
        |> Option.orElseWith (fun () -> doc.Sections |> Array.tryHead)
        |> Option.map (fun section -> section.HeadingPath)
        |> Option.defaultValue doc.Title

    let private docTokenSet (doc: DocProfile) =
        doc.Sections
        |> Array.map (fun section -> section.Heading + "\n" + section.Content)
        |> String.concat "\n"
        |> normalizedTokens

    let private docSimilarity (left: DocProfile) (right: DocProfile) =
        jaccard (docTokenSet left) (docTokenSet right)

    let private nearestDocCluster (docs: DocProfile[]) (doc: DocProfile) =
        docs
        |> Array.filter (fun other -> normPath other.FilePath <> normPath doc.FilePath)
        |> Array.map (fun other -> other, docSimilarity doc other)
        |> Array.sortByDescending snd
        |> Array.tryHead
        |> Option.filter (fun (_, similarity) -> similarity >= 0.18)
        |> Option.map (fun (other, similarity) -> other.RelativePath, similarity)

    let private nearestSectionCluster (docs: DocProfile[]) (section: DocChunk) =
        docs
        |> Array.collect (fun doc -> doc.Sections |> Array.map (fun other -> doc, other))
        |> Array.filter (fun (_, other) ->
            not (normPath other.FilePath = normPath section.FilePath
                 && other.StartLine = section.StartLine
                 && other.Heading = section.Heading))
        |> Array.map (fun (doc, other) -> doc, other, sectionPairSimilarity section other)
        |> Array.sortByDescending (fun (_, _, similarity) -> similarity)
        |> Array.tryHead
        |> Option.filter (fun (_, _, similarity) -> similarity >= 0.15)
        |> Option.map (fun (doc, other, similarity) -> sprintf "%s :: %s" doc.RelativePath other.HeadingPath, similarity)

    let private documentLinksToTarget (index: DocIndex) (sourceFile: string) (targetFile: string) =
        index.Links
        |> Array.exists (fun link ->
            normPath link.SourceFile = normPath sourceFile
            && link.TargetResolved <> ""
            && normPath link.TargetResolved = normPath targetFile)

    let private sectionLinksToTarget (index: DocIndex) (sourceFile: string) (section: DocChunk) (targetFile: string) =
        index.Links
        |> Array.exists (fun link ->
            normPath link.SourceFile = normPath sourceFile
            && link.SourceHeading = section.Heading
            && link.TargetResolved <> ""
            && normPath link.TargetResolved = normPath targetFile)

    let private boundedLiveSummarySignals (index: DocIndex) (doc: DocProfile) (section: DocChunk) (candidateDoc: DocProfile) =
        let textLower = lower (section.Heading + "\n" + section.Content)
        let reasons = ResizeArray<string>()
        let scopeCue =
            containsAny textLower [|
                "summary only"; "bounded summary"; "context only"; "context snapshot"; "planning context"; "for planning";
                "customer-facing summary"; "customer recap"; "brief status note"; "reader-facing summary"; "status recap";
                "non-authoritative summary"; "not the canonical owner"; "for this brief"; "for this update"; "for operators skimming";
                "release note"; "release recap"; "runbook context"; "operator handoff"; "incident context"; "operational summary"
            |]
            || containsAny section.Heading [| "context snapshot"; "brief"; "recap"; "summary"; "release"; "handoff" |]
        let redirectCue =
            containsAny textLower [|
                "authoritative live snapshot"; "authoritative status"; "see current status"; "for exact status";
                "for the full live snapshot"; "for the current live snapshot"; "for full details"
            |]
        let linkToOwner =
            sectionLinksToTarget index doc.FilePath section candidateDoc.FilePath
            || documentLinksToTarget index doc.FilePath candidateDoc.FilePath

        if scopeCue then reasons.Add("Content explicitly scopes the live summary as bounded/non-authoritative.")
        if linkToOwner then reasons.Add(sprintf "Section links to canonical owner `%s`." candidateDoc.RelativePath)
        if redirectCue then reasons.Add("Section redirects readers to the canonical owner for full detail.")
        if doc.Role = "product_or_control_plane_doc" then reasons.Add("Document role is planning/control-plane rather than canonical owner.")

        scopeCue && linkToOwner, reasons.ToArray()

    let private sectionMentionsEntity (entity: string) (section: DocChunk) =
        let haystack = lower (section.Heading + "\n" + section.Content)
        let compactEntity = entity.Replace(".", "").Replace("-", "").Replace("_", "")
        haystack.Contains(entity)
        || haystack.Contains(entity.Replace(".", " "))
        || (compactEntity <> entity && haystack.Replace(" ", "").Contains(compactEntity))

    let private determineAction proposedAction confidence risk =
        if confidence < 0.70 || risk = "high" then "needs_human_review" else proposedAction

    let private normalizeSuggestedAction (suggestedAction: string) =
        match suggestedAction with
        | "replace_with_pointer" -> "link"
        | "compact" -> "reduce"
        | "move_to_archive" -> "archive"
        | "needs_owner" -> "needs_human_review"
        | _ -> suggestedAction

    let private normalizeNearestOwnerOrCluster (nearestOwnerOrCluster: string) =
        if String.IsNullOrWhiteSpace(nearestOwnerOrCluster) then "unknown"
        else nearestOwnerOrCluster

    let private hygieneFinding
        (findingType: string)
        (scenarioId: string)
        (sourceFile: string)
        (sourceSection: string)
        (files: string[])
        (sections: string[])
        (docRole: string)
        (canonicalOwnerCandidate: string)
        (canonicalOwnerConfidence: float)
        (canonicalOwnerStatus: string)
        (nearestOwnerOrCluster: string)
        (evidence: string[])
        (suggestedAction: string)
        (expectedHumanActionShape: string)
        (confidence: float)
        (risk: string)
        (preserveNotes: string)
        (whyFlagged: string) =
        let nearestOwnerOrCluster = normalizeNearestOwnerOrCluster nearestOwnerOrCluster
        let suggestedAction = normalizeSuggestedAction suggestedAction
        mdict [
            "finding_type", box findingType
            "acceptance_scenario_id", box scenarioId
            "source_file", box sourceFile
            "source_section", box sourceSection
            "files", box files
            "sections", box sections
            "doc_role", box docRole
            "canonical_owner_candidate", box canonicalOwnerCandidate
            "canonical_owner_confidence", box (Math.Round(canonicalOwnerConfidence, 3))
            "canonical_owner_status", box canonicalOwnerStatus
            "nearest_owner_or_cluster", box nearestOwnerOrCluster
            "evidence", box evidence
            "suggested_action", box suggestedAction
            "expected_human_action_shape", box expectedHumanActionShape
            "confidence", box (Math.Round(confidence, 3))
            "risk", box risk
            "preserve_notes", box preserveNotes
            "why_flagged", box whyFlagged
        ]

    let hygiene (index: DocIndex) (chunks: DocChunk[] option) (repoRoot: string) =
        match chunks with
        | None -> [| mdict [ "error", box "source chunks not loaded — run 'knowledge-sight index' first" ] |]
        | Some allChunks ->
            let docs = buildDocProfiles index repoRoot allChunks
            let findings = ResizeArray<Dictionary<string, obj>>()

            let docsWithStatus =
                docs
                |> Array.choose (fun doc ->
                    let statusSections =
                        doc.Sections
                        |> Array.filter (fun section ->
                            section.Level > 0
                            && firstSnippet section.Content <> ""
                            && statusSectionScore section >= 3)
                    if statusSections.Length = 0 then None else Some (doc, statusSections))

            if docsWithStatus.Length > 0 then
                let rankedCandidates =
                    docsWithStatus
                    |> Array.map (fun (doc, statusSections) ->
                        let reasons = ResizeArray<string>()
                        let mutable score = statusSections.Length
                        if doc.Role = "canonical_live_status_owner" then
                            score <- score + 4
                            reasons.Add("Document role is canonical_live_status_owner.")
                        if containsAny doc.RelativePath [| "status"; "current-status"; "progress" |]
                           || containsAny doc.Title [| "status"; "progress" |] then
                            score <- score + 3
                            reasons.Add("File/title looks like a live-status owner.")
                        if doc.Backlinks > 0 then
                            score <- score + (min 2 doc.Backlinks)
                            reasons.Add(sprintf "Referenced by %d other document(s)." doc.Backlinks)
                        if doc.Role = "historical_archive" then
                            score <- score - 2
                            reasons.Add("Archive role lowers canonical-owner confidence.")
                        doc, statusSections, score, reasons.ToArray())
                    |> Array.sortByDescending (fun (_, _, score, _) -> score)

                let candidateDoc, candidateSections, candidateScore, candidateReasons = rankedCandidates.[0]
                let secondScore =
                    if rankedCandidates.Length > 1 then
                        let _, _, score, _ = rankedCandidates.[1]
                        score
                    else 0
                let candidateConfidence =
                    clamp01 (0.45 + (float candidateScore / 12.0) + (float (max 0 (candidateScore - secondScore)) / 15.0))
                let candidateStatus =
                    if candidateConfidence >= 0.85 && candidateScore - secondScore >= 3 then "asserted" else "candidate"
                let candidateSuggestedAction =
                    determineAction
                        (if candidateStatus = "asserted" then "preserve" else "needs_owner")
                        candidateConfidence
                        (if candidateStatus = "asserted" then "low" else "medium")
                findings.Add(
                    hygieneFinding
                        "canonical_owner_candidate"
                        "neon-live-status"
                        candidateDoc.RelativePath
                        (primarySectionPath candidateDoc)
                        [| candidateDoc.RelativePath |]
                        (candidateSections |> Array.map (fun section -> section.HeadingPath))
                        candidateDoc.Role
                        candidateDoc.RelativePath
                        candidateConfidence
                        candidateStatus
                        candidateDoc.RelativePath
                        (Array.append
                            [| sprintf "Candidate score %d vs next-best %d." candidateScore secondScore |]
                            (Array.append candidateReasons candidateDoc.RoleEvidence |> Array.distinct))
                        candidateSuggestedAction
                        (if candidateStatus = "asserted" then "ignore" else "needs_human_review")
                        candidateConfidence
                        (if candidateStatus = "asserted" then "low" else "medium")
                        (if candidateStatus = "asserted" then "Preserve as the current canonical live-status owner." else "Keep as the leading candidate until a maintainer confirms ownership.")
                        "Role-aware live-status triage needs a canonical owner candidate before duplicate/stale copies can be judged safely.")

                for (doc, statusSections) in docsWithStatus |> Array.filter (fun (doc, _) -> doc.RelativePath <> candidateDoc.RelativePath) do
                    let bestSimilarity =
                        statusSections
                        |> Array.map (fun section ->
                            candidateSections
                            |> Array.map (fun candidateSection -> statusSimilarity section candidateSection)
                            |> Array.fold max 0.0)
                        |> Array.fold max 0.0
                    let staleMarkers =
                        statusSections
                        |> Array.collect (fun section -> statusPhraseHits section.Content)
                        |> Array.distinct
                    let boundedSections =
                        statusSections
                        |> Array.choose (fun section ->
                            let bounded, reasons = boundedLiveSummarySignals index doc section candidateDoc
                            if bounded then Some (section, reasons) else None)
                    let explicitBounded = boundedSections.Length > 0
                    let sourceSection =
                        if explicitBounded then boundedSections.[0] |> fst |> fun section -> section.HeadingPath
                        else statusSections.[0].HeadingPath
                    let boundedEvidence =
                        boundedSections
                        |> Array.collect snd
                        |> Array.distinct
                    let findingType, scenarioId, proposedAction, expectedHumanActionShape, risk, preserveNotes, whyFlagged =
                        if doc.Role = "historical_archive" then
                            "live_status_triage",
                            "neon-live-status-stale",
                            "move_to_archive",
                            "archive",
                            "medium",
                            "Historical current-state text should move behind archive framing.",
                            "This document contains role-aware live-state text that overlaps with or competes with the canonical current-status owner."
                        elif explicitBounded then
                            "bounded_live_summary_protection",
                            (if doc.Role = "product_or_control_plane_doc" then "bounded-live-summary-control" else "bounded-live-summary-protection"),
                            "preserve",
                            "ignore",
                            "low",
                            "Bounded summaries are valid when they stay clearly scoped and non-authoritative.",
                            "This section mentions current state, but it is explicitly bounded and points readers back to the canonical owner."
                        elif bestSimilarity >= 0.45 then
                            "live_status_triage",
                            "neon-live-status",
                            "replace_with_pointer",
                            "link",
                            (if doc.Role = "mixed" || doc.Role = "unknown" then "high" else "low"),
                            "Prefer replacing duplicate current-state text with a pointer to the canonical owner.",
                            "This document contains role-aware live-state text that overlaps with or competes with the canonical current-status owner."
                        elif staleMarkers.Length > 0 then
                            "live_status_triage",
                            "neon-live-status-stale",
                            "compact",
                            "reduce",
                            (if doc.Role = "mixed" || doc.Role = "unknown" then "high" else "medium"),
                            "",
                            "This document contains role-aware live-state text that overlaps with or competes with the canonical current-status owner."
                        else
                            "live_status_triage",
                            "neon-live-status-stale",
                            "needs_human_review",
                            "needs_human_review",
                            "high",
                            "",
                            "This document contains live-state language, but the workflow cannot safely classify its relationship to the canonical owner."

                    let confidence =
                        if explicitBounded then
                            clamp01 (0.74 + (float boundedEvidence.Length / 20.0))
                        else
                            clamp01 (0.40 + (bestSimilarity / 1.4) + (float staleMarkers.Length / 12.0))
                    let finalAction =
                        if explicitBounded then "preserve"
                        else determineAction proposedAction confidence risk
                    let evidence =
                        Array.concat [|
                            [| sprintf "Best similarity to canonical owner candidate `%s`: %.2f." candidateDoc.RelativePath bestSimilarity |]
                            if staleMarkers.Length > 0 then [| sprintf "Stale-prone live-state markers: %s." (String.concat ", " staleMarkers) |] else [||]
                            if boundedEvidence.Length > 0 then boundedEvidence else [||]
                            [| sprintf "Section snippet: %s" (statusSections |> Array.map (fun section -> firstSnippet section.Content) |> Array.filter ((<>) "") |> Array.tryHead |> Option.defaultValue "(no snippet)") |]
                            doc.RoleEvidence
                        |]
                    if not explicitBounded then
                        findings.Add(
                            hygieneFinding
                                findingType
                                scenarioId
                                doc.RelativePath
                                sourceSection
                                [| doc.RelativePath |]
                                (statusSections |> Array.map (fun section -> section.HeadingPath))
                                doc.Role
                                candidateDoc.RelativePath
                                candidateConfidence
                                candidateStatus
                                candidateDoc.RelativePath
                                evidence
                                finalAction
                                expectedHumanActionShape
                                confidence
                                risk
                                preserveNotes
                                whyFlagged)

            for doc in docs do
                let meaningfulSections =
                    doc.Sections
                    |> Array.filter (fun section -> section.Level > 0 && firstSnippet section.Content <> "")

                let nonStatusSections =
                    meaningfulSections
                    |> Array.filter (fun section -> statusSectionScore section < 3)

                if doc.Role = "mixed" then
                    for section in nonStatusSections do
                        let uniqueHits, genericHits = sectionSignals section
                        let maxSimilarity = sectionSimilarity section allChunks
                        let nearestCluster =
                            nearestSectionCluster docs section
                            |> Option.map fst
                            |> Option.defaultValue ""
                        let isUnique = uniqueHits.Length >= 2 || (uniqueHits.Length >= 1 && maxSimilarity < 0.18)
                        let isGeneric = genericHits.Length >= 2 || (genericHits.Length >= 1 && maxSimilarity >= 0.12)
                        let proposedAction, expectedHumanActionShape, preserveNotes, risk, whyFlagged =
                            if isUnique && not isGeneric then
                                "preserve",
                                "ignore",
                                "Protect the design/invariant section even inside a mixed-role document.",
                                "medium",
                                "Mixed-role docs need section-level preserve/reduce output rather than a whole-document judgment."
                            elif isGeneric && not isUnique then
                                (if maxSimilarity >= 0.25 then "replace_with_pointer" else "compact"),
                                (if maxSimilarity >= 0.25 then "link" else "reduce"),
                                "",
                                "medium",
                                "This section looks like reducible scaffolding inside a mixed-role document."
                            else
                                "needs_human_review",
                                "needs_human_review",
                                "Section has both preserve and reduce signals or is too ambiguous for a safe automatic call.",
                                "high",
                                "This section needs explicit human review so mixed-role documents do not silently lose important context."
                        let confidence =
                            clamp01 (0.45 + (float uniqueHits.Length / 10.0) + (float genericHits.Length / 10.0) + (maxSimilarity / 4.0))
                        let finalAction =
                            if proposedAction = "needs_human_review" then proposedAction
                            else determineAction proposedAction confidence risk
                        let evidence =
                            Array.concat [|
                                if uniqueHits.Length > 0 then [| sprintf "Unique markers: %s." (String.concat ", " uniqueHits) |] else [||]
                                if genericHits.Length > 0 then [| sprintf "Generic scaffolding markers: %s." (String.concat ", " genericHits) |] else [||]
                                [| sprintf "Max similarity to other sections: %.2f." maxSimilarity |]
                                [| sprintf "Section snippet: %s" (firstSnippet section.Content) |]
                                doc.RoleEvidence
                            |]
                        let hasStatusCompanion =
                            meaningfulSections
                            |> Array.exists (fun other -> other.Level > 0 && statusSectionScore other >= 3)
                        let hasCompanionMixedTask =
                            nonStatusSections
                            |> Array.exists (fun other ->
                                if other.HeadingPath = section.HeadingPath then
                                    false
                                else
                                    let otherUniqueHits, otherGenericHits = sectionSignals other
                                    let otherMaxSimilarity = sectionSimilarity other allChunks
                                    let otherIsUnique = otherUniqueHits.Length >= 2 || (otherUniqueHits.Length >= 1 && otherMaxSimilarity < 0.18)
                                    let otherIsGeneric = otherGenericHits.Length >= 2 || (otherGenericHits.Length >= 1 && otherMaxSimilarity >= 0.12)
                                    not (otherIsUnique && not otherIsGeneric))
                        let suppressMixedPreserve =
                            proposedAction = "preserve"
                            && expectedHumanActionShape = "ignore"
                            && (hasStatusCompanion || hasCompanionMixedTask)
                        if not suppressMixedPreserve then
                            findings.Add(
                                hygieneFinding
                                    "section_triage"
                                    "section-preserve-reduce"
                                    doc.RelativePath
                                    section.HeadingPath
                                    [| doc.RelativePath |]
                                    [| section.HeadingPath |]
                                    doc.Role
                                    ""
                                    0.0
                                    ""
                                    nearestCluster
                                    evidence
                                    finalAction
                                    expectedHumanActionShape
                                    confidence
                                    risk
                                    preserveNotes
                                    whyFlagged)
                elif doc.Role = "decision_index" then
                    for section in nonStatusSections do
                        let textLower = lower (section.Heading + "\n" + section.Content)
                        let uniqueHits, genericHits = sectionSignals section
                        let decisionHits = decisionSectionSignals section
                        let indexHits, contradictionHits, missingTargetHits, derivedHits, historicalHits = decisionIndexSectionSignals section
                        let brokenLinks = sectionBrokenLinks index doc.FilePath section
                        let maxSimilarity = sectionSimilarity section allChunks
                        let nearestCluster =
                            nearestSectionCluster docs section
                            |> Option.map fst
                            |> Option.defaultValue ""
                        let docHasStatusSections =
                            doc.Sections
                            |> Array.exists (fun other -> other.Level > 0 && statusSectionScore other >= 3)
                        let boundedDiscoverabilitySectionCue =
                            containsAny textLower [| "add this register to knowledge overview"; "link this register from the overview"; "add an entry link" |]
                        let preservesDecisionIndexMemory =
                            indexHits.Length >= 1
                            || decisionHits.Length >= 1
                            || derivedHits.Length >= 1
                            || historicalHits.Length >= 1
                            || (uniqueHits.Length >= 1 && genericHits.Length = 0)
                        let looksLikeResidue =
                            genericHits.Length >= 2
                            || (genericHits.Length >= 1 && maxSimilarity >= 0.12)
                            || containsAny textLower [| "current recommendation"; "implementation step"; "operator checklist"; "stale instruction" |]
                        let isHistoricalSnapshot =
                            historicalHits.Length > 0 && not (claimsCurrentAuthority textLower)
                        let hasMissingTarget = missingTargetHits.Length > 0 || brokenLinks.Length > 0
                        let proposedAction, expectedHumanActionShape, preserveNotes, risk, whyFlagged =
                            if contradictionHits.Length > 0 then
                                "needs_human_review",
                                "link",
                                "Reconcile the contradiction between the index metadata and its linked decision target before treating this register as trustworthy.",
                                "medium",
                                "Decision-index target contradictions need explicit review or correction guidance rather than silent protection."
                            elif hasMissingTarget && preservesDecisionIndexMemory then
                                "preserve",
                                "link",
                                "Preserve the decision index, but repair the missing artifact or broken target link so the register stays trustworthy and discoverable.",
                                "medium",
                                "This decision index still carries durable navigational value, but one or more decision targets are missing or broken."
                            elif derivedHits.Length > 0 then
                                "preserve",
                                "link",
                                "Keep the derived decision index, but point readers back to the canonical register so duplicated navigation stays bounded.",
                                "low",
                                "Derived decision indexes can stay reviewable when they clearly point back to the canonical register."
                            elif doc.Backlinks = 0 && boundedDiscoverabilitySectionCue then
                                "preserve",
                                "link",
                                "Preserve the decision index and add the explicit overview link named in the note.",
                                "low",
                                "This decision index keeps durable value and already names the bounded discoverability step needed to keep it reviewable."
                            elif isHistoricalSnapshot then
                                "preserve",
                                "accept",
                                "Preserve dated gate/wave provenance when the register is clearly historical rather than current authority.",
                                "low",
                                "Historical decision snapshots are durable provenance, not duplicate current-owner drift."
                            elif preservesDecisionIndexMemory && docHasStatusSections then
                                "preserve",
                                "link",
                                "Preserve the long-lived register entry, but keep readers pointed at the canonical current-status owner for live rollout detail.",
                                "low",
                                "Decision indexes can preserve durable navigation while still linking away copied live rollout detail."
                            elif preservesDecisionIndexMemory && not looksLikeResidue then
                                "preserve",
                                "accept",
                                "Protect durable decision navigation and register memory when the index is clearly pointing to long-lived decision targets.",
                                "low",
                                "Decision indexes should preserve navigational value unless a section becomes stale instruction or live-state drift."
                            elif looksLikeResidue && not preservesDecisionIndexMemory then
                                (if maxSimilarity >= 0.25 then "replace_with_pointer" else "compact"),
                                (if maxSimilarity >= 0.25 then "link" else "reduce"),
                                "",
                                "medium",
                                "This section looks like stale rollout or recommendation residue inside a decision index."
                            else
                                "needs_human_review",
                                "link",
                                "Decision-index sections that mix registry value with residue need explicit review before they are rewritten.",
                                "medium",
                                "This decision-index section mixes preserve and reduce signals, so the workflow should stay honest and route it to review."
                        let confidence =
                            clamp01 (
                                0.48
                                + (float indexHits.Length / 12.0)
                                + (float decisionHits.Length / 14.0)
                                + (float contradictionHits.Length / 10.0)
                                + (float missingTargetHits.Length / 12.0)
                                + (float derivedHits.Length / 12.0)
                                + (float historicalHits.Length / 12.0)
                                + (float brokenLinks.Length / 10.0)
                                + (float uniqueHits.Length / 18.0)
                                + (float genericHits.Length / 18.0)
                                + (maxSimilarity / 4.0))
                        let finalAction =
                            if proposedAction = "needs_human_review" then proposedAction
                            elif boundedDiscoverabilitySectionCue && proposedAction = "preserve" && expectedHumanActionShape = "link" then "preserve"
                            else determineAction proposedAction confidence risk
                        let evidence =
                            Array.concat [|
                                if indexHits.Length > 0 then [| sprintf "Decision-index markers: %s." (String.concat ", " indexHits) |] else [||]
                                if decisionHits.Length > 0 then [| sprintf "Decision-memory markers: %s." (String.concat ", " decisionHits) |] else [||]
                                if contradictionHits.Length > 0 then [| sprintf "Contradiction markers: %s." (String.concat ", " contradictionHits) |] else [||]
                                if missingTargetHits.Length > 0 then [| sprintf "Missing-target markers: %s." (String.concat ", " missingTargetHits) |] else [||]
                                if derivedHits.Length > 0 then [| sprintf "Derived-index markers: %s." (String.concat ", " derivedHits) |] else [||]
                                if historicalHits.Length > 0 then [| sprintf "Historical markers: %s." (String.concat ", " historicalHits) |] else [||]
                                if brokenLinks.Length > 0 then
                                    brokenLinks
                                    |> Array.map (fun link ->
                                        sprintf "Broken outgoing link: %s." (if String.IsNullOrWhiteSpace(link.TargetPath) then "(empty target)" else link.TargetPath))
                                else [||]
                                if uniqueHits.Length > 0 then [| sprintf "Unique markers: %s." (String.concat ", " uniqueHits) |] else [||]
                                if genericHits.Length > 0 then [| sprintf "Generic scaffolding markers: %s." (String.concat ", " genericHits) |] else [||]
                                [| sprintf "Max similarity to other sections: %.2f." maxSimilarity |]
                                [| sprintf "Section snippet: %s" (firstSnippet section.Content) |]
                                doc.RoleEvidence
                            |]
                        let suppressPureDecisionIndexPreserve =
                            proposedAction = "preserve"
                            && expectedHumanActionShape = "accept"
                            && not isHistoricalSnapshot
                        if not suppressPureDecisionIndexPreserve then
                            findings.Add(
                                hygieneFinding
                                    "section_triage"
                                    (if contradictionHits.Length > 0 then "decision-index-contradiction"
                                     elif hasMissingTarget then "decision-index-target-missing"
                                     elif derivedHits.Length > 0 then "decision-index-derived"
                                     elif isHistoricalSnapshot then "decision-index-historical-snapshot"
                                     else "decision-index-section-preserve-reduce")
                                    doc.RelativePath
                                    section.HeadingPath
                                    [| doc.RelativePath |]
                                    [| section.HeadingPath |]
                                    doc.Role
                                    ""
                                    0.0
                                    ""
                                    nearestCluster
                                    evidence
                                    finalAction
                                    expectedHumanActionShape
                                    confidence
                                    risk
                                    preserveNotes
                                    whyFlagged)
                elif doc.Role = "research_note" then
                    for section in nonStatusSections do
                        let textLower = lower (section.Heading + "\n" + section.Content)
                        let uniqueHits, genericHits = sectionSignals section
                        let researchHits, conflictHits, evidenceGapHits = researchSectionSignals section
                        let maxSimilarity = sectionSimilarity section allChunks
                        let nearestCluster =
                            nearestSectionCluster docs section
                            |> Option.map fst
                            |> Option.defaultValue ""
                        let preservesResearchMemory =
                            researchHits.Length >= 1 || (uniqueHits.Length >= 1 && genericHits.Length = 0)
                        let looksLikeResidue =
                            genericHits.Length >= 2
                            || (genericHits.Length >= 1 && maxSimilarity >= 0.12)
                            || containsAny textLower [| "setup"; "migration"; "operator sign-off"; "implementation residue"; "stale setup"; "rollout checklist" |]
                        let proposedAction, expectedHumanActionShape, preserveNotes, risk, whyFlagged =
                            if conflictHits.Length > 0 then
                                "needs_human_review",
                                "link",
                                "Keep both research notes available and add comparison links rather than guessing a winner.",
                                "medium",
                                "Conflicting research notes need explicit human review so durable memory is preserved without inventing a false resolution."
                            elif evidenceGapHits.Length > 0 && preservesResearchMemory then
                                "preserve",
                                "link",
                                "Preserve the research note, but restore the missing artifact/evidence link before treating it as settled.",
                                "medium",
                                "This research note carries durable learning, but its supporting artifact/evidence trail is incomplete."
                            elif preservesResearchMemory && not looksLikeResidue then
                                "preserve",
                                "link",
                                "Preserve durable research memory and keep it discoverable from a stable nearby home.",
                                "low",
                                "Long-lived research findings, negative results, and open-problem notes should stay reviewable rather than being compacted away."
                            elif looksLikeResidue && not preservesResearchMemory then
                                (if maxSimilarity >= 0.25 then "replace_with_pointer" else "compact"),
                                (if maxSimilarity >= 0.25 then "link" else "reduce"),
                                "",
                                "medium",
                                "This section looks like setup or implementation residue inside a research note."
                            else
                                "needs_human_review",
                                "link",
                                "Research notes that mix durable findings with setup residue need explicit review before they are rewritten.",
                                "medium",
                                "This research-note section mixes preserve and reduce signals, so the workflow should stay honest and route it to review."
                        let confidence =
                            clamp01 (
                                0.50
                                + (float researchHits.Length / 12.0)
                                + (float conflictHits.Length / 10.0)
                                + (float evidenceGapHits.Length / 10.0)
                                + (float uniqueHits.Length / 14.0)
                                + (float genericHits.Length / 16.0)
                                + (maxSimilarity / 4.0))
                        let finalAction =
                            if proposedAction = "needs_human_review" then proposedAction
                            else determineAction proposedAction confidence risk
                        let evidence =
                            Array.concat [|
                                if researchHits.Length > 0 then [| sprintf "Research-memory markers: %s." (String.concat ", " researchHits) |] else [||]
                                if conflictHits.Length > 0 then [| sprintf "Conflict markers: %s." (String.concat ", " conflictHits) |] else [||]
                                if evidenceGapHits.Length > 0 then [| sprintf "Evidence-gap markers: %s." (String.concat ", " evidenceGapHits) |] else [||]
                                if uniqueHits.Length > 0 then [| sprintf "Unique markers: %s." (String.concat ", " uniqueHits) |] else [||]
                                if genericHits.Length > 0 then [| sprintf "Generic scaffolding markers: %s." (String.concat ", " genericHits) |] else [||]
                                [| sprintf "Max similarity to other sections: %.2f." maxSimilarity |]
                                [| sprintf "Section snippet: %s" (firstSnippet section.Content) |]
                                doc.RoleEvidence
                            |]
                        findings.Add(
                            hygieneFinding
                                "section_triage"
                                (if conflictHits.Length > 0 then "research-note-conflict"
                                 elif evidenceGapHits.Length > 0 then "research-note-evidence-gap"
                                 else "research-note-section-preserve-reduce")
                                doc.RelativePath
                                section.HeadingPath
                                [| doc.RelativePath |]
                                [| section.HeadingPath |]
                                doc.Role
                                ""
                                0.0
                                ""
                                nearestCluster
                                evidence
                                finalAction
                                expectedHumanActionShape
                                confidence
                                risk
                                preserveNotes
                                whyFlagged)
                elif doc.Role = "review_or_decision_record" then
                    for section in nonStatusSections do
                        let uniqueHits, genericHits = sectionSignals section
                        let decisionHits = decisionSectionSignals section
                        let maxSimilarity = sectionSimilarity section allChunks
                        let nearestCluster =
                            nearestSectionCluster docs section
                            |> Option.map fst
                            |> Option.defaultValue ""
                        let preservesDecisionMemory = decisionHits.Length >= 1 || (uniqueHits.Length >= 1 && genericHits.Length = 0)
                        let isGeneric = genericHits.Length >= 2 || (genericHits.Length >= 1 && maxSimilarity >= 0.12)
                        let proposedAction, expectedHumanActionShape, preserveNotes, risk, whyFlagged =
                            if preservesDecisionMemory && not isGeneric then
                                "preserve",
                                "accept",
                                "Protect durable decision memory and rationale inside long-lived decision records.",
                                "low",
                                "Decision records should preserve durable rationale unless a section is clearly stale implementation residue."
                            elif isGeneric && not preservesDecisionMemory then
                                (if maxSimilarity >= 0.25 then "replace_with_pointer" else "compact"),
                                (if maxSimilarity >= 0.25 then "link" else "reduce"),
                                "",
                                "medium",
                                "This section looks like stale implementation or process residue inside a long-lived decision record."
                            else
                                "needs_human_review",
                                "needs_human_review",
                                "Section mixes durable decision memory with stale implementation/process details.",
                                "medium",
                                "Decision-record sections that mix rationale and residue need explicit human review so long-lived value is not dropped."
                        let confidence =
                            clamp01 (0.45 + (float decisionHits.Length / 12.0) + (float uniqueHits.Length / 12.0) + (float genericHits.Length / 12.0) + (maxSimilarity / 4.0))
                        let finalAction =
                            if proposedAction = "needs_human_review" then proposedAction
                            else determineAction proposedAction confidence risk
                        let evidence =
                            Array.concat [|
                                if decisionHits.Length > 0 then [| sprintf "Decision-memory markers: %s." (String.concat ", " decisionHits) |] else [||]
                                if uniqueHits.Length > 0 then [| sprintf "Unique markers: %s." (String.concat ", " uniqueHits) |] else [||]
                                if genericHits.Length > 0 then [| sprintf "Generic scaffolding markers: %s." (String.concat ", " genericHits) |] else [||]
                                [| sprintf "Max similarity to other sections: %.2f." maxSimilarity |]
                                [| sprintf "Section snippet: %s" (firstSnippet section.Content) |]
                                doc.RoleEvidence
                            |]
                        findings.Add(
                            hygieneFinding
                                "section_triage"
                                "decision-record-section-preserve-reduce"
                                doc.RelativePath
                                section.HeadingPath
                                [| doc.RelativePath |]
                                [| section.HeadingPath |]
                                doc.Role
                                ""
                                0.0
                                ""
                                nearestCluster
                                evidence
                                finalAction
                                expectedHumanActionShape
                                confidence
                                risk
                                preserveNotes
                                whyFlagged)
                elif doc.Role = "research_deep_dive" then
                    let preserveSections =
                        nonStatusSections
                        |> Array.filter (fun section ->
                            let uniqueHits, genericHits = sectionSignals section
                            let maxSimilarity = sectionSimilarity section allChunks
                            (uniqueHits.Length >= 1 && genericHits.Length <= 1 && maxSimilarity < 0.25)
                            || containsAny section.Heading [| "core idea"; "invariants"; "why" |])
                    let hasStatusSections =
                        meaningfulSections
                        |> Array.exists (fun section -> section.Level > 0 && statusSectionScore section >= 3)
                    let suppressDeepDivePreserve =
                        preserveSections.Length > 0
                        && (doc.Backlinks = 0 || hasStatusSections)
                    if preserveSections.Length > 0 && not suppressDeepDivePreserve then
                        let nearestCluster =
                            nearestSectionCluster docs preserveSections.[0]
                            |> Option.map fst
                            |> Option.defaultValue ""
                        let evidence =
                            Array.append
                                [| sprintf "Research deep-dive role with %d preserve-worthy section(s)." preserveSections.Length |]
                                doc.RoleEvidence
                        findings.Add(
                            hygieneFinding
                                "section_triage"
                                "neon-deep-dive-protection"
                                doc.RelativePath
                                preserveSections.[0].HeadingPath
                                [| doc.RelativePath |]
                                (preserveSections |> Array.map (fun section -> section.HeadingPath))
                                doc.Role
                                ""
                                0.0
                                ""
                                nearestCluster
                                evidence
                                "preserve"
                                "ignore"
                                0.88
                                "low"
                                "High-novelty deep-dive content should be preserved in the first report slice."
                                "Deep-dive protection is part of the first-cut acceptance gate, not a later optimization.")

            let orphanDocs =
                docs
                |> Array.filter (fun doc -> doc.Backlinks = 0)

            for doc in orphanDocs do
                let nearestCluster, nearestSimilarity =
                    nearestDocCluster docs doc
                    |> Option.defaultValue ("", 0.0)
                let outlinkCount =
                    doc.Sections
                    |> Array.sumBy (fun section -> section.OutLinks.Length)
                let artifactReferenceCount = decisionArtifactReferenceCount doc
                let brokenLinkCount = docBrokenLinks index doc.FilePath |> Array.length
                let looksLikeIndexDoc =
                    doc.Role = "entrypoint_or_index_doc"
                    || containsAny doc.RelativePath [| "overview"; "index"; "readme" |]
                    || containsAny doc.Title [| "overview"; "index"; "readme" |]
                    || (doc.RoleEvidence |> Array.exists (fun evidence -> evidence.Contains("entrypoint or index")))
                let docTextLower =
                    doc.Sections
                    |> Array.collect (fun section -> [| section.Heading; section.Content |])
                    |> String.concat "\n"
                    |> lower
                let hasResearchEvidenceGap =
                    containsAny docTextLower [| "artifact missing"; "missing artifact"; "evidence gap"; "evidence link missing"; "restore evidence" |]
                let hasDecisionIndexEvidenceGap =
                    brokenLinkCount > 0
                    || containsAny docTextLower [| "artifact id unknown"; "artifact id missing"; "unknown artifact id"; "missing artifact id"; "broken link"; "target moved"; "repair the target"; "repair the link" |]
                let hasBoundedDiscoverabilityCue =
                    containsAny docTextLower [| "add this register to knowledge overview"; "link this register from the overview"; "add an entry link"; "repair the target"; "repair the link"; "restore the target" |]
                let suggestedAction, expectedHumanActionShape, confidence, risk, preserveNotes, whyFlagged =
                    if doc.Role = "decision_index" && hasDecisionIndexEvidenceGap then
                        "preserve",
                        "link",
                        0.84,
                        "medium",
                        "Preserve the decision index, but repair the missing artifact or broken target link before treating the register as trustworthy.",
                        "This decision index has no incoming links and one or more targets are missing or broken, so the registry should stay preserved while the target path is repaired."
                    elif doc.Role = "decision_index" && (outlinkCount >= 2 || artifactReferenceCount >= 1) then
                        "preserve",
                        "ignore",
                        0.86,
                        "low",
                        "Decision indexes with clear registry links can remain lightly linked by design.",
                        "This decision index has no incoming links, but its outgoing targets or artifact references show clear long-lived registry value."
                    elif doc.Role = "decision_index" && hasBoundedDiscoverabilityCue then
                        "preserve",
                        "link",
                        0.8,
                        "medium",
                        "Preserve the decision index and add the bounded entry link or correction step named in the note.",
                        "This decision index has no incoming links, but the next discoverability step is explicit enough to keep the output action-worthy."
                    elif looksLikeIndexDoc && outlinkCount >= 3 then
                        "preserve",
                        "ignore",
                        0.92,
                        "low",
                        "Entrypoint/root docs can legitimately have no backlinks.",
                        "This doc has no incoming links, but its role signals match an intentional root or index."
                    elif doc.Role = "historical_archive" then
                        "preserve",
                        "ignore",
                        0.82,
                        "low",
                        "Historical/archive docs may remain lightly linked by design.",
                        "This doc has no incoming links, but archive/history role signals suggest it is intentionally retained."
                    elif doc.Role = "research_deep_dive" then
                        "preserve",
                        "link",
                        0.86,
                        "low",
                        "Protect intentional deep-dive knowledge; if discoverability feels low, add an entry link rather than reducing content.",
                        "This doc has no incoming links, but its role signals indicate intentional deep-dive knowledge that should be preserved."
                    elif doc.Role = "review_or_decision_record" && (artifactReferenceCount >= 2 || outlinkCount >= 2) then
                        "preserve",
                        "ignore",
                        0.85,
                        "low",
                        "Long-lived decision records with clear artifact or cross-reference value should not trigger orphan panic by default.",
                        "This decision record has no incoming links, but its artifact references or outgoing links show clear long-lived decision value."
                    elif doc.Role = "review_or_decision_record" then
                        "preserve",
                        "link",
                        0.8,
                        "medium",
                        "Preserve long-lived decision memory; if discoverability feels weak, add an entry link rather than reducing the record.",
                        "This decision record has no incoming links, but it still carries durable value; review discoverability before treating it as stray content."
                    elif doc.Role = "research_note" && hasResearchEvidenceGap then
                        "preserve",
                        "link",
                        0.82,
                        "medium",
                        "Preserve research intent, but restore the missing evidence path so the note stays trustworthy and discoverable.",
                        "This research note has no incoming links and its evidence trail is incomplete, so the note should stay preserved while the missing link is restored."
                    elif doc.Role = "research_note" then
                        "preserve",
                        "link",
                        0.8,
                        "medium",
                        "Preserve long-lived research memory; if discoverability feels weak, add an entry link rather than reducing the note.",
                        "This research note has no incoming links, but it still carries durable findings or open questions that should remain reviewable."
                    elif outlinkCount = 0 && nearestCluster = "" then
                        "needs_human_review",
                        "link",
                        0.74,
                        "medium",
                        "Check whether this doc needs an entry link or should remain isolated by intent.",
                        "This doc has no incoming links and no nearby owner/cluster, so discoverability may be weak."
                    else
                        "needs_human_review",
                        "link",
                        clamp01 (0.62 + (nearestSimilarity / 3.0)),
                        "medium",
                        "Review whether this doc needs a clearer inbound link from its nearest related cluster.",
                        "This doc has no incoming links, but it does have related material nearby; linkage may simply be weak."
                let evidence =
                    Array.concat [|
                        [| "Incoming link count: 0." |]
                        [| sprintf "Outgoing link count: %d." outlinkCount |]
                        if artifactReferenceCount > 0 then [| sprintf "Artifact reference count: %d." artifactReferenceCount |] else [||]
                        if brokenLinkCount > 0 then [| sprintf "Broken outgoing link count: %d." brokenLinkCount |] else [||]
                        if nearestCluster <> "" then [| sprintf "Nearest related cluster: %s (similarity %.2f)." nearestCluster nearestSimilarity |] else [| "No nearby owner/cluster was found." |]
                        doc.RoleEvidence
                    |]
                findings.Add(
                    hygieneFinding
                        "orphan_triage"
                        "orphan-actionability"
                        doc.RelativePath
                        (primarySectionPath doc)
                        [| doc.RelativePath |]
                        [| primarySectionPath doc |]
                        doc.Role
                        ""
                        0.0
                        ""
                        nearestCluster
                        evidence
                        suggestedAction
                        expectedHumanActionShape
                        confidence
                        risk
                        preserveNotes
                        whyFlagged)

            let fileContents =
                allChunks
                |> Array.groupBy (fun chunk -> normPath chunk.FilePath)
                |> Array.map (fun (_, sections) ->
                    let doc = docs |> Array.find (fun item -> normPath item.FilePath = normPath sections.[0].FilePath)
                    doc, (sections |> Array.map (fun section -> section.Content) |> String.concat "\n"))

            let entityToDocs = Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
            for (doc, content) in fileContents do
                let entities =
                    extractEntityRefs content
                    |> Array.map normalizeEntity
                    |> Array.filter (fun entity -> entity.Length >= 3)
                    |> Array.distinct
                for entity in entities do
                    if not (entityToDocs.ContainsKey(entity)) then
                        entityToDocs.[entity] <- HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    entityToDocs.[entity].Add(doc.RelativePath) |> ignore

            let indexedFileNames =
                fileContents
                |> Array.map (fun (doc, _) -> Path.GetFileNameWithoutExtension(doc.RelativePath).ToLowerInvariant())
                |> Set.ofArray

            let entityImportance (entity: string) =
                let lengthScore = min 5 (entity.Length / 3)
                let fileBonus = if indexedFileNames.Contains(entity) then 3 else 0
                let structureBonus =
                    if entity.Contains(".") then 2
                    elif entity.Length > 12 then 1
                    else 0
                lengthScore + fileBonus + structureBonus

            let gapCandidates =
                entityToDocs
                |> Seq.map (fun kv ->
                    let sources = kv.Value |> Seq.sort |> Seq.toArray
                    let count = sources.Length
                    let signal =
                        if count >= max 5 (int (ceil (float fileContents.Length * 0.05))) then "god-node"
                        elif count >= 2 then "shared"
                        else "isolated"
                    kv.Key, sources, count, signal, entityImportance kv.Key)
                |> Seq.filter (fun (_, _, _, signal, importance) -> (signal = "isolated" || signal = "god-node") && importance >= 4)
                |> Seq.sortByDescending (fun (_, _, count, _, importance) -> count, importance)
                |> Seq.truncate 5
                |> Seq.toArray

            for (entity, sources, count, signal, importance) in gapCandidates do
                let sourceFile = sources.[0]
                let sourceDoc = docs |> Array.find (fun doc -> doc.RelativePath = sourceFile)
                let sourceSection =
                    sourceDoc.Sections
                    |> Array.tryFind (sectionMentionsEntity entity)
                    |> Option.orElseWith (fun () -> sourceDoc.Sections |> Array.tryFind (fun section -> section.Level > 0))
                    |> Option.map (fun section -> section.HeadingPath)
                    |> Option.defaultValue sourceDoc.Title
                let nearestOwnerOrCluster =
                    if count > 1 then String.concat ", " sources
                    else
                        nearestDocCluster docs sourceDoc
                        |> Option.map fst
                        |> Option.defaultValue ""
                let suggestedAction, expectedHumanActionShape, confidence, risk, preserveNotes, whyFlagged =
                    if signal = "god-node" then
                        "needs_human_review",
                        "reduce",
                        0.79,
                        "medium",
                        "Review whether this entity needs a clearer canonical explanation instead of repeated scattered mentions.",
                        "This entity appears across many docs, which can indicate over-centralized ownership or a missing canonical explanation."
                    else
                        "needs_human_review",
                        "link",
                        0.76,
                        "medium",
                        "Review whether this entity needs an additional link or a clearer home in adjacent docs.",
                        "This entity appears in only one doc, which can indicate weak coverage or low discoverability."
                let evidence =
                    [|
                        sprintf "Signal `%s`: entity appears in %d doc(s)." signal count
                        sprintf "Entity importance score: %d." importance
                        sprintf "Sources: %s." (String.concat ", " sources)
                        if nearestOwnerOrCluster <> "" then sprintf "Nearest owner/cluster considered: %s." nearestOwnerOrCluster else "No nearby owner/cluster was found."
                    |]
                findings.Add(
                    hygieneFinding
                        "gap_triage"
                        "gap-explainability"
                        sourceFile
                        sourceSection
                        sources
                        [| sourceSection |]
                        sourceDoc.Role
                        ""
                        0.0
                        ""
                        nearestOwnerOrCluster
                        evidence
                        suggestedAction
                        expectedHumanActionShape
                        confidence
                        risk
                        preserveNotes
                        whyFlagged)

            if findings.Count = 0 then
                [| mdict [ "note", box "No hygiene findings detected for the current index."; "docs", box docs.Length ] |]
            else
                findings
                |> Seq.sortByDescending (fun finding ->
                    match finding.["confidence"] with
                    | :? float as confidence -> confidence
                    | _ -> 0.0)
                |> Seq.toArray

    // ── changed ──

    /// changed(gitRef) — find chunks in files that changed since a git ref.
    let changed (index: DocIndex) (session: QuerySession) (repoRoot: string) (gitRef: string) =
        try
            let psi = System.Diagnostics.ProcessStartInfo("git", sprintf "diff --name-only %s" gitRef)
            psi.WorkingDirectory <- repoRoot
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            use proc = System.Diagnostics.Process.Start(psi)
            let output = proc.StandardOutput.ReadToEnd()
            proc.WaitForExit()
            if proc.ExitCode <> 0 then
                [| mdict [ "error", box (sprintf "git diff failed for ref '%s'" gitRef) ] |]
            else
                let changedFiles =
                    output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun f -> f.Trim().Replace('\\', '/'))
                    |> Set.ofArray
                let results = ResizeArray()
                for i in 0..index.Chunks.Length-1 do
                    let c = index.Chunks.[i]
                    let normFile = c.FilePath.Replace('\\', '/')
                    if changedFiles.Contains(normFile) then
                        let id = session.NextRef(i)
                        results.Add(mdict [
                            "id", box id; "heading", box c.Heading; "file", box (Path.GetFileName c.FilePath)
                            "path", box c.FilePath; "line", box c.StartLine; "summary", box c.Summary ])
                results.ToArray()
        with ex ->
            [| mdict [ "error", box (sprintf "git not available: %s" ex.Message) ] |]

    // ── explain ──

    /// explain(refId) — debug primitive showing index metadata and findSource diagnosis.
    let explain (index: DocIndex) (session: QuerySession) (chunks: DocChunk[] option) (refId: string) =
        match session.GetRef(refId) with
        | None -> mdict [ "error", box (sprintf "ref %s not found in session" refId) ]
        | Some idx when idx < 0 || idx >= index.Chunks.Length ->
            mdict [ "error", box (sprintf "ref %s points to chunk %d but index has %d chunks" refId idx index.Chunks.Length) ]
        | Some idx ->
            let c = index.Chunks.[idx]
            let cid = IndexStore.chunkId c.FilePath c.Heading c.StartLine
            let sourceMatch =
                match chunks with
                | None -> "source chunks not loaded"
                | Some chs ->
                    let cidMatch = chs |> Array.tryFind (fun ch ->
                        IndexStore.chunkId ch.FilePath ch.Heading ch.StartLine = cid)
                    match cidMatch with
                    | Some ch -> sprintf "CID match (%s), content length: %d" cid ch.Content.Length
                    | None ->
                        let tripleMatch = chs |> Array.tryFind (fun ch ->
                            ch.FilePath = c.FilePath && ch.Heading = c.Heading && ch.StartLine = c.StartLine)
                        match tripleMatch with
                        | Some ch -> sprintf "triple-key match (no CID), content length: %d" ch.Content.Length
                        | None ->
                            let pathMatch = chs |> Array.tryFind (fun ch -> normPath ch.FilePath = normPath c.FilePath && ch.Heading = c.Heading)
                            match pathMatch with
                            | Some ch -> sprintf "partial match (heading+path, line differs: source=%d vs index=%d), content length: %d" ch.StartLine c.StartLine ch.Content.Length
                            | None -> sprintf "NO MATCH — findSource will return None. CID=%s, FilePath=%s, Heading=%s, StartLine=%d" cid c.FilePath c.Heading c.StartLine
            mdict [
                "refId", box refId; "chunkIdx", box idx; "cid", box cid
                "filePath", box c.FilePath; "heading", box c.Heading
                "startLine", box c.StartLine; "endLine", box c.EndLine
                "summary", box c.Summary; "sourceMatch", box sourceMatch ]
