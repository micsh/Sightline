namespace AITeam.CodeSight

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text
open System.Text.RegularExpressions

/// Wraps the Node.js tree-sitter chunker process (ts-chunker.js).
/// Communicates via JSONL over stdin/stdout.
module TreeSitterChunker =

    type private GlobSegment =
        | RecursiveWildcard
        | Pattern of Regex

    type private GlobExcludePattern = {
        Regex: Regex
        Segments: GlobSegment[]
    }

    type private ExcludePattern =
        | Segment of string
        | Glob of GlobExcludePattern

    type private ChunkerProcess(nodePath: string, chunkerScript: string, repoRoot: string) =
        let psi = ProcessStartInfo(nodePath, sprintf "\"%s\"" chunkerScript)
        do psi.WorkingDirectory <- repoRoot
        do psi.RedirectStandardInput <- true
        do psi.RedirectStandardOutput <- true
        do psi.RedirectStandardError <- true
        do psi.UseShellExecute <- false
        do psi.CreateNoWindow <- true
        let proc = Process.Start(psi)

        member _.Send(json: string) =
            proc.StandardInput.WriteLine(json)
            proc.StandardInput.Flush()
            let line = proc.StandardOutput.ReadLine()
            if line = null then failwith "ts-chunker process died"
            line

        interface IDisposable with
            member _.Dispose() =
                try proc.StandardInput.WriteLine("{\"cmd\":\"quit\"}")
                    proc.WaitForExit(2000) |> ignore
                    if not proc.HasExited then proc.Kill()
                with _ -> ()

    let private normalizePathLike (path: string) =
        path.Replace("\\", "/").Trim('/')

    let private trimLeadingCurrentDirectory (path: string) =
        if path.StartsWith("./") then path.Substring(2)
        else path

    let private containsGlobSyntax (pattern: string) =
        pattern.IndexOfAny([| '*'; '?' |]) >= 0

    let private globRegex (pattern: string) =
        let normalized = pattern |> trimLeadingCurrentDirectory |> normalizePathLike
        let builder = StringBuilder("^")
        let mutable index = 0
        while index < normalized.Length do
            match normalized.[index] with
            | '*' when index + 1 < normalized.Length && normalized.[index + 1] = '*' ->
                if index + 2 < normalized.Length && normalized.[index + 2] = '/' then
                    builder.Append("(?:.*/)?") |> ignore
                    index <- index + 3
                else
                    builder.Append(".*") |> ignore
                    index <- index + 2
            | '*' ->
                builder.Append("[^/]*") |> ignore
                index <- index + 1
            | '?' ->
                builder.Append("[^/]") |> ignore
                index <- index + 1
            | c ->
                builder.Append(Regex.Escape(string c)) |> ignore
                index <- index + 1
        builder.Append("$") |> ignore
        Regex(builder.ToString(), RegexOptions.CultureInvariant)

    let private segmentRegex (pattern: string) =
        let builder = StringBuilder()
        builder.Append("^") |> ignore
        for ch in pattern do
            match ch with
            | '*' -> builder.Append("[^/]*") |> ignore
            | '?' -> builder.Append("[^/]") |> ignore
            | _ -> builder.Append(Regex.Escape(string ch)) |> ignore
        builder.Append("$") |> ignore
        Regex(builder.ToString(), RegexOptions.CultureInvariant)

    let private globSegments (pattern: string) =
        pattern
        |> trimLeadingCurrentDirectory
        |> normalizePathLike
        |> fun normalized -> normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (function
            | "**" -> RecursiveWildcard
            | segment -> Pattern (segmentRegex segment))

    let private compileExcludePatterns (exclude: string[]) =
        exclude
        |> Array.choose (fun pattern ->
            let normalized = pattern |> trimLeadingCurrentDirectory |> normalizePathLike
            if String.IsNullOrWhiteSpace normalized then None
            elif containsGlobSyntax normalized then
                Some (Glob {
                    Regex = globRegex normalized
                    Segments = globSegments normalized
                })
            else Some (Segment normalized))

    let private pathSegments (relativePath: string) =
        (normalizePathLike relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries)

    let private matchesFileExclude (excludePatterns: ExcludePattern[]) (relativePath: string) =
        let normalized = normalizePathLike relativePath
        let segments = pathSegments normalized
        excludePatterns
        |> Array.exists (function
            | Segment segment -> segments |> Array.exists (fun pathSegment -> pathSegment = segment)
            | Glob glob -> glob.Regex.IsMatch(normalized))

    let private matchesDirectoryExclude (excludePatterns: ExcludePattern[]) (relativePath: string) =
        let normalized = normalizePathLike relativePath
        let segments = pathSegments normalized

        let canPruneDirectory (glob: GlobExcludePattern) =
            let cache = Collections.Generic.Dictionary<struct (int * int * bool * bool), bool>()

            let rec loop patternIndex segmentIndex matchedSpecific recursiveScope =
                let key = struct (patternIndex, segmentIndex, matchedSpecific, recursiveScope)
                match cache.TryGetValue key with
                | true, cached -> cached
                | _ ->
                    let result =
                        if segmentIndex = segments.Length then
                            if patternIndex = glob.Segments.Length then recursiveScope
                            else
                                match glob.Segments.[patternIndex] with
                                | RecursiveWildcard -> loop (patternIndex + 1) segmentIndex matchedSpecific (recursiveScope || matchedSpecific)
                                | Pattern _ -> recursiveScope
                        elif patternIndex = glob.Segments.Length then
                            false
                        else
                            match glob.Segments.[patternIndex] with
                            | RecursiveWildcard ->
                                let nextRecursiveScope = recursiveScope || matchedSpecific
                                loop (patternIndex + 1) segmentIndex matchedSpecific nextRecursiveScope
                                || loop patternIndex (segmentIndex + 1) matchedSpecific nextRecursiveScope
                            | Pattern regex ->
                                regex.IsMatch(segments.[segmentIndex])
                                && loop (patternIndex + 1) (segmentIndex + 1) true recursiveScope

                    cache.[key] <- result
                    result

            loop 0 0 false false

        excludePatterns
        |> Array.exists (function
            | Segment segment -> segments |> Array.exists (fun pathSegment -> pathSegment = segment)
            | Glob glob -> glob.Regex.IsMatch(normalized) || canPruneDirectory glob)

    let private sourceFiles (repoRoot: string) (dir: string) (extensions: string[]) (exclude: string[]) =
        let extSet = Set.ofArray extensions
        let excludePatterns = compileExcludePatterns exclude
        let results = ResizeArray<string>()

        let rec walk (currentDir: string) =
            for file in Directory.EnumerateFiles(currentDir) do
                let ext = Path.GetExtension(file).ToLowerInvariant()
                let relativePath = Path.GetRelativePath(repoRoot, file)
                if extSet.Contains ext && not (matchesFileExclude excludePatterns relativePath) then
                    results.Add(file)

            for childDir in Directory.EnumerateDirectories(currentDir) do
                let relativePath = Path.GetRelativePath(repoRoot, childDir)
                if not (matchesDirectoryExclude excludePatterns relativePath) then
                    walk childDir

        let rootRelativePath = Path.GetRelativePath(repoRoot, dir)
        if not (matchesDirectoryExclude excludePatterns rootRelativePath) then
            walk dir

        results.ToArray()

    let private truncationMarker = " …[truncated]"

    let private truncateToFit (limit: int) (text: string) =
        if limit <= 0 then ""
        elif text.Length <= limit then text
        elif limit <= truncationMarker.Length then text.Substring(0, limit)
        else text.Substring(0, limit - truncationMarker.Length) + truncationMarker

    let private buildContinuationPrefix (maxChars: int) (signature: string) =
        let minBodyChars = if maxChars > 0 then 1 else 0
        let candidates = [| "// continued: "; "// cont: "; "//↪ "; "↪ " |]

        let tryBuild (prefixBase: string) =
            let maxSignatureChars = maxChars - minBodyChars - prefixBase.Length - 1
            if maxSignatureChars < 0 then
                None
            else
                let safeSignature = truncateToFit maxSignatureChars signature
                Some (sprintf "%s%s\n" prefixBase safeSignature)

        candidates
        |> Array.tryPick tryBuild
        |> Option.defaultValue ""

    let private enforceMaxChunkChars (maxChars: int) (chunk: CodeChunk) =
        if maxChars <= 0 || chunk.Content.Length <= maxChars then
            [| chunk |]
        else
            let lines = chunk.Content.Split('\n')
            let signature =
                if lines.Length > 0 then truncateToFit 80 lines.[0]
                else chunk.Name
            let continuedPrefix = buildContinuationPrefix maxChars signature
            let continuedBudget = max 1 (maxChars - continuedPrefix.Length)
            let parts = ResizeArray<CodeChunk>()
            let currentLines = ResizeArray<string>()
            let mutable currentLength = 0
            let mutable currentStartLine = chunk.StartLine
            let mutable currentEndLine = chunk.StartLine
            let mutable partNumber = 1

            let currentBudget () =
                if partNumber = 1 then maxChars else continuedBudget

            let flush () =
                if currentLines.Count > 0 then
                    let body = String.Join("\n", currentLines)
                    let content =
                        if partNumber = 1 then body
                        else continuedPrefix + body
                    parts.Add(
                        { chunk with
                            Name = sprintf "%s_part%d" chunk.Name partNumber
                            StartLine = currentStartLine
                            EndLine = currentEndLine
                            Content = content })
                    partNumber <- partNumber + 1
                    currentLines.Clear()
                    currentLength <- 0

            let addLine (lineNumber: int) (line: string) =
                let rec loop (candidate: string) =
                    let budget = currentBudget ()
                    let normalized = truncateToFit budget candidate
                    let candidateLength =
                        if currentLines.Count = 0 then normalized.Length
                        else currentLength + 1 + normalized.Length
                    if currentLines.Count > 0 && candidateLength > budget then
                        flush ()
                        loop candidate
                    else
                        if currentLines.Count = 0 then currentStartLine <- lineNumber
                        currentLines.Add(normalized)
                        currentLength <- candidateLength
                        currentEndLine <- lineNumber

                loop line

            lines
            |> Array.iteri (fun idx line -> addLine (chunk.StartLine + idx) line)

            flush ()

            let emitted = parts.ToArray()
            if emitted.Length = 1 then
                [| { emitted.[0] with Name = chunk.Name } |]
            else
                emitted

    /// Chunk all source files in given directories.
    let chunkFiles (cfg: CodeSightConfig) (files: string[]) : CodeChunk[] =
        use chunker = new ChunkerProcess(cfg.NodePath, cfg.ChunkerScript, cfg.RepoRoot)
        let results = ResizeArray()
        for file in files do
            let relPath = Path.GetRelativePath(cfg.RepoRoot, file).Replace("\\", "/")
            let cmd = sprintf """{"cmd":"parse","file":"%s","maxChars":%d}""" (file.Replace("\\", "\\\\")) cfg.MaxChunkChars
            try
                let response = chunker.Send(cmd)
                let doc = JsonDocument.Parse(response)
                let root = doc.RootElement
                match root.TryGetProperty("chunks") with
                | true, chunks ->
                    for ch in chunks.EnumerateArray() do
                        let str (p: string) = match ch.TryGetProperty(p) with true, v -> v.GetString() | _ -> ""
                        let int' (p: string) = match ch.TryGetProperty(p) with true, v -> v.GetInt32() | _ -> 0
                        let chunk = {
                            FilePath = relPath
                            Module = str "module"
                            Name = str "name"
                            Kind = str "kind"
                            StartLine = int' "startLine"
                            EndLine = int' "endLine"
                            Content = str "content"
                            Context = str "context"
                        }
                        for boundedChunk in enforceMaxChunkChars cfg.MaxChunkChars chunk do
                            results.Add(boundedChunk)
                | _ -> ()
            with ex ->
                eprintfn "  Warning: failed to chunk %s: %s" relPath ex.Message
        results.ToArray()

    /// Extract imports from source files.
    let extractImports (cfg: CodeSightConfig) (files: string[]) : FileImport[] =
        use chunker = new ChunkerProcess(cfg.NodePath, cfg.ChunkerScript, cfg.RepoRoot)
        let results = ResizeArray()
        for file in files do
            let relPath = Path.GetRelativePath(cfg.RepoRoot, file).Replace("\\", "/")
            let cmd = sprintf """{"cmd":"imports","file":"%s"}""" (file.Replace("\\", "\\\\"))
            try
                let response = chunker.Send(cmd)
                let doc = JsonDocument.Parse(response)
                match doc.RootElement.TryGetProperty("imports") with
                | true, imports ->
                    for imp in imports.EnumerateArray() do
                        let str (p: string) = match imp.TryGetProperty(p) with true, v -> v.GetString() | _ -> ""
                        let int' (p: string) = match imp.TryGetProperty(p) with true, v -> v.GetInt32() | _ -> 0
                        results.Add({ FilePath = relPath; Module = str "module"; Line = int' "line"; Raw = str "raw" })
                | _ -> ()
            with _ -> ()
        results.ToArray()

    /// Extract signatures from source files.
    let extractSignatures (cfg: CodeSightConfig) (files: string[]) : DeclSignature[] =
        use chunker = new ChunkerProcess(cfg.NodePath, cfg.ChunkerScript, cfg.RepoRoot)
        let results = ResizeArray()
        for file in files do
            let relPath = Path.GetRelativePath(cfg.RepoRoot, file).Replace("\\", "/")
            let cmd = sprintf """{"cmd":"signatures","file":"%s"}""" (file.Replace("\\", "\\\\"))
            try
                let response = chunker.Send(cmd)
                let doc = JsonDocument.Parse(response)
                match doc.RootElement.TryGetProperty("signatures") with
                | true, sigs ->
                    for s in sigs.EnumerateArray() do
                        let str (p: string) = match s.TryGetProperty(p) with true, v -> v.GetString() | _ -> ""
                        let int' (p: string) = match s.TryGetProperty(p) with true, v -> v.GetInt32() | _ -> 0
                        results.Add({ Name = str "name"; Kind = str "kind"; Signature = str "signature"; FilePath = relPath; StartLine = int' "startLine" })
                | _ -> ()
            with _ -> ()
        results.ToArray()

    /// Extract type references from source files.
    let extractTypeRefs (cfg: CodeSightConfig) (files: string[]) : FileTypeRef[] =
        use chunker = new ChunkerProcess(cfg.NodePath, cfg.ChunkerScript, cfg.RepoRoot)
        let results = ResizeArray()
        for file in files do
            let relPath = Path.GetRelativePath(cfg.RepoRoot, file).Replace("\\", "/")
            let cmd = sprintf """{"cmd":"typerefs","file":"%s"}""" (file.Replace("\\", "\\\\"))
            try
                let response = chunker.Send(cmd)
                let doc = JsonDocument.Parse(response)
                match doc.RootElement.TryGetProperty("typeRefs") with
                | true, refs ->
                    let typeNames = refs.EnumerateArray() |> Seq.map (fun v -> v.GetString()) |> Seq.toArray
                    if typeNames.Length > 0 then
                        results.Add({ FilePath = relPath; TypeRefs = typeNames })
                | _ -> ()
            with _ -> ()
        results.ToArray()

    /// Enumerate source files matching config.
    let findSourceFiles (cfg: CodeSightConfig) =
        cfg.SrcDirs
        |> Array.collect (fun d ->
            let dir = Path.Combine(cfg.RepoRoot, d)
            if Directory.Exists dir then sourceFiles cfg.RepoRoot dir cfg.Extensions cfg.Exclude
            else [||])
