namespace AITeam.CodeSight

open System
open System.IO
open System.Numerics.Tensors

/// Index persistence and in-memory query operations.
module IndexStore =

    let private coreFields = [| "FilePath"; "Module"; "Name"; "Kind"; "StartLine"; "EndLine"; "Summary"; "Signature" |]
    let private semanticStateFileName = "semantic-state.json"
    let private sourceChunkCacheMetaFileName = "source-chunks.meta.json"
    let sourceChunkCacheVersion = 1

    type SourceChunkCacheStatus =
        | Fresh
        | Missing
        | Stale of string

    let private normalizeEmbeddings (embeddings: float32[][]) =
        if embeddings.Length = 0 then
            [||], 0
        else
            let targetDim =
                embeddings
                |> Array.tryPick (fun emb -> if emb.Length > 0 then Some emb.Length else None)
                |> Option.defaultValue 0

            let normalized =
                embeddings
                |> Array.map (fun emb ->
                    if targetDim = 0 then
                        [||]
                    elif emb.Length = targetDim then
                        emb
                    elif emb.Length = 0 then
                        Array.zeroCreate<float32> targetDim
                    elif emb.Length > targetDim then
                        emb.[0 .. targetDim - 1]
                    else
                        Array.append emb (Array.zeroCreate<float32> (targetDim - emb.Length)))

            normalized, targetDim

    let private inferSemanticState (chunkCount: int) (codeEmbeddings: float32[][]) (summaryEmbeddings: float32[][]) =
        let codeRowsMatch = codeEmbeddings.Length = chunkCount
        let summaryRowsMatch = summaryEmbeddings.Length = chunkCount
        let codeDim =
            codeEmbeddings
            |> Array.tryPick (fun emb -> if emb.Length > 0 then Some emb.Length else None)
            |> Option.defaultValue 0
        let summaryDim =
            summaryEmbeddings
            |> Array.tryPick (fun emb -> if emb.Length > 0 then Some emb.Length else None)
            |> Option.defaultValue 0
        let codeHealthy = codeRowsMatch && codeDim > 0 && (codeEmbeddings |> Array.forall (fun emb -> emb.Length = codeDim))
        let summaryHealthy = summaryRowsMatch && summaryDim > 0 && (summaryEmbeddings |> Array.forall (fun emb -> emb.Length = summaryDim))

        if chunkCount = 0 || (codeHealthy && summaryHealthy) then
            "full", "", 0
        else
            "degraded", "Semantic index metadata is missing or incomplete; treating semantic queries as degraded.", 0

    /// Deterministic chunk identifier from key fields. Survives reindex as long as the chunk's position is stable.
    let chunkId (filePath: string) (name: string) (startLine: int) =
        let input = sprintf "%s|%s|%d" (filePath.Replace('\\', '/')) name startLine
        let bytes = System.Text.Encoding.UTF8.GetBytes(input)
        let hash = System.Security.Cryptography.SHA256.HashData(bytes)
        let hex = BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()
        hex.Substring(0, 12)

    // ── Source chunk cache ──

    let saveSourceChunks (dir: string) (maxChunkChars: int) (chunks: CodeChunk[]) =
        let path = Path.Combine(dir, "source-chunks.jsonl")
        use writer = new StreamWriter(path)
        for c in chunks do
            let cid = chunkId c.FilePath c.Name c.StartLine
            let json =
                System.Text.Json.JsonSerializer.Serialize({|
                    cid = cid
                    filePath = c.FilePath
                    moduleName = c.Module
                    name = c.Name
                    kind = c.Kind
                    startLine = c.StartLine
                    endLine = c.EndLine
                    content = c.Content
                    context = c.Context
                |})
            writer.WriteLine(json)
        File.WriteAllText(
            Path.Combine(dir, sourceChunkCacheMetaFileName),
            System.Text.Json.JsonSerializer.Serialize({|
                version = sourceChunkCacheVersion
                maxChunkChars = maxChunkChars
            |}))
        eprintfn "  Cached %d source chunks → source-chunks.jsonl" chunks.Length

    let loadSourceChunks (dir: string) : CodeChunk[] option =
        let path = Path.Combine(dir, "source-chunks.jsonl")
        if not (File.Exists path) then None
        else
            try
                let chunks =
                    File.ReadAllLines(path)
                    |> Array.choose (fun line ->
                        try
                            let doc = System.Text.Json.JsonDocument.Parse(line)
                            let r = doc.RootElement
                            let str (p: string) = match r.TryGetProperty(p) with true, v -> v.GetString() | _ -> ""
                            let int' (p: string) = match r.TryGetProperty(p) with true, v -> v.GetInt32() | _ -> 0
                            Some { FilePath = str "filePath"; Module = str "moduleName"; Name = str "name"
                                   Kind = str "kind"; StartLine = int' "startLine"; EndLine = int' "endLine"
                                   Content = str "content"; Context = str "context" }
                         with _ -> None)
                Some chunks
            with _ -> None

    let getSourceChunkCacheStatus (dir: string) (maxChunkChars: int) =
        let chunksPath = Path.Combine(dir, "source-chunks.jsonl")
        let metaPath = Path.Combine(dir, sourceChunkCacheMetaFileName)
        if not (File.Exists chunksPath) then
            Missing
        elif not (File.Exists metaPath) then
            Stale "source chunk cache metadata missing"
        else
            try
                use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metaPath))
                let root = doc.RootElement
                let getInt (name: string) =
                    match root.TryGetProperty(name) with
                    | true, value when value.ValueKind = System.Text.Json.JsonValueKind.Number -> value.GetInt32()
                    | _ -> -1

                let version = getInt "version"
                let cachedMaxChunkChars = getInt "maxChunkChars"

                if version <> sourceChunkCacheVersion then
                    Stale (sprintf "source chunk cache version %d does not match expected %d" version sourceChunkCacheVersion)
                elif cachedMaxChunkChars <> maxChunkChars then
                    Stale (sprintf "source chunk cache maxChunkChars %d does not match configured %d" cachedMaxChunkChars maxChunkChars)
                else
                    Fresh
            with ex ->
                Stale (sprintf "source chunk cache metadata unreadable: %s" ex.Message)

    // ── Persistence helpers ──

    let private escape (s: string) =
        s.Replace("\t", " ").Replace("\n", " ").Replace("\r", "")

    let private writeEmbeddings (path: string) (embeddings: float32[][]) =
        let normalizedEmbeddings, dim = normalizeEmbeddings embeddings
        use fs = File.Create(path)
        use bw = new BinaryWriter(fs)
        bw.Write(normalizedEmbeddings.Length)
        if normalizedEmbeddings.Length > 0 then
            bw.Write(dim)
            for emb in normalizedEmbeddings do
                for v in emb do bw.Write(v)

    let private readEmbeddings (path: string) =
        if not (File.Exists path) then None
        else
            try
                use fs = File.OpenRead(path)
                use br = new BinaryReader(fs)
                let count = br.ReadInt32()
                if count = 0 then Some [||]
                else
                    let dim = br.ReadInt32()
                    Some (Array.init count (fun _ -> Array.init dim (fun _ -> br.ReadSingle())))
            with _ ->
                None

    // ── Save ──

    let save (dir: string) (index: CodeIndex) =
        Directory.CreateDirectory(dir) |> ignore

        let extraKeys =
            index.Chunks
            |> Array.collect (fun c -> c.Extra |> Map.toArray |> Array.map fst)
            |> Array.distinct |> Array.sort

        let allFields = Array.append coreFields extraKeys
        let header = sprintf "#fields:%s" (allFields |> String.concat "\t")
        let chunkLines =
            index.Chunks |> Array.map (fun c ->
                let core = sprintf "%s\t%s\t%s\t%s\t%d\t%d\t%s\t%s"
                               (escape c.FilePath) (escape c.Module) (escape c.Name)
                               c.Kind c.StartLine c.EndLine (escape c.Summary) (escape c.Signature)
                let extras = extraKeys |> Array.map (fun k -> c.Extra |> Map.tryFind k |> Option.defaultValue "" |> escape)
                if extras.Length > 0 then sprintf "%s\t%s" core (extras |> String.concat "\t")
                else core)
        File.WriteAllLines(Path.Combine(dir, "chunks.tsv"), Array.append [| header |] chunkLines)

        writeEmbeddings (Path.Combine(dir, "code.emb")) index.CodeEmbeddings
        writeEmbeddings (Path.Combine(dir, "summary.emb")) index.SummaryEmbeddings
        File.WriteAllText(
            Path.Combine(dir, semanticStateFileName),
            System.Text.Json.JsonSerializer.Serialize(
                {| state = index.SemanticState
                   message = index.SemanticMessage
                   failedEmbeddingBatches = index.FailedEmbeddingBatches |}))

        let importLines = index.Imports |> Array.map (fun (f, m) -> sprintf "%s\t%s" (escape f) (escape m))
        File.WriteAllLines(Path.Combine(dir, "imports.tsv"), importLines)

        let refLines = index.TypeRefs |> Array.map (fun (f, refs) -> sprintf "%s\t%s" (escape f) (refs |> String.concat ","))
        File.WriteAllLines(Path.Combine(dir, "typerefs.tsv"), refLines)

        eprintfn "  Index saved: %d chunks (%d extra fields), %d imports → %s"
            index.Chunks.Length extraKeys.Length index.Imports.Length dir

    // ── Load ──

    let load (dir: string) : CodeIndex option =
        let chunkFile = Path.Combine(dir, "chunks.tsv")
        if not (File.Exists chunkFile) then None
        else
            let allLines = File.ReadAllLines(chunkFile)
            let headerLine, dataLines =
                if allLines.Length > 0 && allLines.[0].StartsWith("#fields:") then
                    Some (allLines.[0].Substring(8).Split('\t')), allLines.[1..]
                else None, allLines

            let extraFieldNames =
                match headerLine with
                | Some fields when fields.Length > 8 -> fields.[8..]
                | _ -> [||]

            let chunks =
                dataLines |> Array.choose (fun line ->
                    let p = line.Split('\t')
                    if p.Length >= 7 then
                        let sigStr = if p.Length >= 8 then p.[7] else ""
                        let extra =
                            extraFieldNames
                            |> Array.mapi (fun i name ->
                                let colIdx = 8 + i
                                if colIdx < p.Length then Some (name, p.[colIdx]) else None)
                            |> Array.choose id |> Map.ofArray
                        Some { FilePath = p.[0]; Module = p.[1]; Name = p.[2]; Kind = p.[3]
                               StartLine = int p.[4]; EndLine = int p.[5]; Summary = p.[6]
                               Signature = sigStr; Extra = extra }
                    else None)
            let codeEmbs = readEmbeddings (Path.Combine(dir, "code.emb")) |> Option.defaultValue [||]
            let sumEmbFile = Path.Combine(dir, "summary.emb")
            let sumEmbs = if File.Exists sumEmbFile then readEmbeddings sumEmbFile |> Option.defaultValue [||] else [||]
            let imports =
                let f = Path.Combine(dir, "imports.tsv")
                if File.Exists f then
                    File.ReadAllLines(f) |> Array.choose (fun line ->
                        let p = line.Split('\t')
                        if p.Length >= 2 then Some (p.[0], p.[1]) else None)
                else [||]
            let typeRefs =
                let f = Path.Combine(dir, "typerefs.tsv")
                if File.Exists f then
                    File.ReadAllLines(f) |> Array.choose (fun line ->
                        let p = line.Split('\t')
                        if p.Length >= 2 then Some (p.[0], p.[1].Split(',') |> Array.filter (fun s -> s <> ""))
                        else None)
                else [||]
            let dim =
                codeEmbs
                |> Array.tryPick (fun emb -> if emb.Length > 0 then Some emb.Length else None)
                |> Option.defaultValue 0
            let loadedSemanticState, loadedSemanticMessage, loadedFailedEmbeddingBatches =
                let statePath = Path.Combine(dir, semanticStateFileName)
                if File.Exists statePath then
                    try
                        use doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(statePath))
                        let root = doc.RootElement
                        let getString (name: string) =
                            match root.TryGetProperty(name) with
                            | true, value when value.ValueKind = System.Text.Json.JsonValueKind.String -> value.GetString()
                            | _ -> ""
                        let getInt (name: string) =
                            match root.TryGetProperty(name) with
                            | true, value when value.ValueKind = System.Text.Json.JsonValueKind.Number -> value.GetInt32()
                            | _ -> 0
                        getString "state", getString "message", getInt "failedEmbeddingBatches"
                    with _ ->
                        inferSemanticState chunks.Length codeEmbs sumEmbs
                else
                    inferSemanticState chunks.Length codeEmbs sumEmbs
            let inferredSemanticState, inferredSemanticMessage, _ =
                inferSemanticState chunks.Length codeEmbs sumEmbs
            let semanticState, semanticMessage, failedEmbeddingBatches =
                if String.IsNullOrWhiteSpace(loadedSemanticState) then
                    (inferredSemanticState, inferredSemanticMessage, loadedFailedEmbeddingBatches)
                elif loadedSemanticState = "full" && inferredSemanticState <> "full" then
                    let correctedMessage =
                        if String.IsNullOrWhiteSpace(loadedSemanticMessage) then
                            "Persisted semantic state claimed full health, but embedding files are missing, empty, or corrupt."
                        else
                            loadedSemanticMessage
                    ("degraded", correctedMessage, loadedFailedEmbeddingBatches)
                else
                    (loadedSemanticState, loadedSemanticMessage, loadedFailedEmbeddingBatches)
            Some
                { Chunks = chunks
                  CodeEmbeddings = codeEmbs
                  SummaryEmbeddings = sumEmbs
                  Imports = imports
                  TypeRefs = typeRefs
                  EmbeddingDim = dim
                  SemanticState = semanticState
                  SemanticMessage = semanticMessage
                  FailedEmbeddingBatches = failedEmbeddingBatches }

    // ── Query functions ──

    let search (index: CodeIndex) (queryEmbedding: float32[]) (k: int) =
        if index.SemanticState <> "full" || index.SummaryEmbeddings.Length = 0 || queryEmbedding.Length = 0 then [||]
        else
            let ranked =
                index.SummaryEmbeddings
                |> Array.indexed
                |> Array.choose (fun (i, emb) ->
                    if emb.Length = 0 || emb.Length <> queryEmbedding.Length then None
                    else
                        let sim = TensorPrimitives.CosineSimilarity(ReadOnlySpan(queryEmbedding), ReadOnlySpan(emb))
                        Some (i, sim))
                |> Array.sortByDescending snd
            ranked |> Array.take (min k ranked.Length)

    let similar (index: CodeIndex) (chunkIdx: int) (k: int) (useSummary: bool) =
        let embeddings = if useSummary then index.SummaryEmbeddings else index.CodeEmbeddings
        if index.SemanticState <> "full" || embeddings.Length = 0 || chunkIdx >= embeddings.Length then [||]
        else
            let target = embeddings.[chunkIdx]
            if target.Length = 0 then [||]
            else
                let ranked =
                    embeddings
                    |> Array.indexed
                    |> Array.choose (fun (i, emb) ->
                        if i = chunkIdx || emb.Length = 0 || emb.Length <> target.Length then None
                        else Some (i, TensorPrimitives.CosineSimilarity(ReadOnlySpan(target), ReadOnlySpan(emb))))
                    |> Array.sortByDescending snd
                ranked |> Array.take (min k ranked.Length)

    /// Normalize file input: agents may pass full path, relative path, or just filename.
    let matchFile (filePath: string) (input: string) =
        let inputLower = input.Replace("\\", "/").ToLowerInvariant()
        let pathLower = filePath.Replace("\\", "/").ToLowerInvariant()
        let fileNameLower = Path.GetFileName(filePath).ToLowerInvariant()
        // Match: exact filename, or path ends with input, or input ends with filename
        fileNameLower = inputLower
        || pathLower = inputLower
        || pathLower.EndsWith("/" + inputLower)
        || inputLower.EndsWith("/" + fileNameLower)

    let fileContextByName (index: CodeIndex) (fileName: string) =
        index.Chunks |> Array.indexed
        |> Array.filter (fun (_, c) -> matchFile c.FilePath fileName)

    let fileImports (index: CodeIndex) (fileName: string) =
        index.Imports
        |> Array.filter (fun (f, _) -> matchFile f fileName)
        |> Array.map snd

    let dependents (index: CodeIndex) (moduleName: string) =
        let target = moduleName.ToLowerInvariant()
        index.Imports
        |> Array.filter (fun (_, m) -> m.ToLowerInvariant().Contains(target))
        |> Array.map fst |> Array.distinct

    let typeImpact (index: CodeIndex) (typeName: string) =
        index.TypeRefs
        |> Array.filter (fun (_, refs) -> refs |> Array.exists (fun r -> r = typeName))
        |> Array.map fst

    let formatChunk (index: CodeIndex) (chunkIdx: int) =
        let c = index.Chunks.[chunkIdx]
        let file = Path.GetFileName(c.FilePath)
        let sigStr = if c.Signature <> "" then sprintf "  %s" c.Signature else ""
        let extraStr =
            if c.Extra.IsEmpty then ""
            else c.Extra |> Map.toSeq |> Seq.map (fun (k, v) -> sprintf " [%s=%s]" k v) |> String.concat ""
        sprintf "%s:%s (%s:%d)%s — %s%s" c.Kind c.Name file c.StartLine sigStr c.Summary extraStr
