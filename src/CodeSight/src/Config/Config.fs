namespace AITeam.CodeSight

open System
open System.IO
open System.Text.Json

/// Configuration loaded from code-intel.json (or defaults).
/// A named scope maps to a set of source directories.
type ScopeDefinition = {
    Name: string
    Dirs: string[]
}

type CodeSightConfig = {
    RepoRoot: string
    SrcDirs: string[]
    Extensions: string[]
    Exclude: string[]
    IndexDir: string
    SummaryCache: string
    EmbeddingUrl: string
    EmbeddingTimeoutSeconds: int
    EmbeddingBatchSize: int
    LlmUrl: string
    LlmModel: string
    LlmMaxTokens: int
    LlmTemperature: float
    ChunkerScript: string
    NodePath: string
    MaxChunkChars: int
    NoiseWords: string[]
    UtilityPatterns: string[]
    StagesDir: string
    Scopes: ScopeDefinition[]
}

type CodeSightRuntimePaths = {
    ParsersDir: string
}

module Config =

    let private defaultExtensions = [| ".fs"; ".fsi"; ".cs"; ".js"; ".ts"; ".py"; ".go"; ".rs" |]
    let private defaultExclude = [| "node_modules"; "bin"; "obj"; ".git"; "wwwroot"; "dist"; "target"; "vendor"; "__pycache__" |]
    let private defaultEmbeddingUrl = "http://localhost:1234/v1/embeddings"
    let private defaultEmbeddingTimeoutSeconds = 120
    let private mergeExclude (customExclude: string[]) =
        Array.append defaultExclude customExclude |> Array.distinct

    /// Auto-detect source directories by looking for common patterns.
    let private detectSrcDirs (repoRoot: string) =
        let candidates = [| "src"; "lib"; "Source"; "app" |]
        let found = candidates |> Array.filter (fun d -> Directory.Exists(Path.Combine(repoRoot, d)))
        if found.Length > 0 then found
        else [| "." |]

    /// Auto-detect scopes from directory structure.
    let private detectScopes (repoRoot: string) (srcDirs: string[]) =
        let scopes = ResizeArray<ScopeDefinition>()
        // "all" scope = everything
        scopes.Add({ Name = "all"; Dirs = srcDirs })
        // Individual directories as scopes
        for d in srcDirs do
            scopes.Add({ Name = d; Dirs = [| d |] })
        // Check for common patterns
        let testDirs = [| "tests"; "test"; "Tests" |] |> Array.filter (fun d -> Directory.Exists(Path.Combine(repoRoot, d)))
        if testDirs.Length > 0 then
            scopes.Add({ Name = "tests"; Dirs = testDirs })
            scopes.Add({ Name = "all"; Dirs = Array.append srcDirs testDirs })
        let toolDirs = [| "tools"; "scripts" |] |> Array.filter (fun d -> Directory.Exists(Path.Combine(repoRoot, d)))
        if toolDirs.Length > 0 then
            scopes.Add({ Name = "tools"; Dirs = toolDirs })
        scopes.ToArray()

    /// Load config from code-intel.json, or build defaults.
    let load (repoRoot: string) (runtime: CodeSightRuntimePaths) =
        let configPath = Path.Combine(repoRoot, "code-intel.json")
        let indexDir = Path.Combine(repoRoot, ".code-intel")

        if File.Exists configPath then
            let json = File.ReadAllText(configPath)
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let str (p: string) d = match root.TryGetProperty(p) with true, v -> v.GetString() | _ -> d
            let int' (p: string) d = match root.TryGetProperty(p) with true, v -> v.GetInt32() | _ -> d
            let float' (p: string) d = match root.TryGetProperty(p) with true, v -> v.GetDouble() | _ -> d
            let strArr (p: string) d =
                match root.TryGetProperty(p) with
                | true, v -> v.EnumerateArray() |> Seq.map (fun x -> x.GetString()) |> Seq.toArray
                | _ -> d
            {
                RepoRoot = repoRoot
                SrcDirs = strArr "srcDirs" (detectSrcDirs repoRoot)
                Extensions = strArr "extensions" defaultExtensions
                Exclude = mergeExclude (strArr "exclude" [||])
                IndexDir = str "indexDir" indexDir
                SummaryCache = Path.Combine(indexDir, ".summary-cache.tsv")
                EmbeddingUrl = str "embeddingUrl" defaultEmbeddingUrl
                EmbeddingTimeoutSeconds = int' "embeddingTimeoutSeconds" defaultEmbeddingTimeoutSeconds
                EmbeddingBatchSize = int' "embeddingBatchSize" 50
                LlmUrl = str "llmUrl" "http://localhost:8090/v1/chat/completions"
                LlmModel = str "llmModel" "bonsai"
                LlmMaxTokens = int' "llmMaxTokens" 60
                LlmTemperature = float' "llmTemperature" 0.0
                ChunkerScript = str "chunkerScript" (Path.Combine(runtime.ParsersDir, "ts-chunker.js"))
                NodePath = str "nodePath" "node"
                MaxChunkChars = int' "maxChunkChars" 3000
                NoiseWords = strArr "noiseWords" [||]
                UtilityPatterns = strArr "utilityPatterns" [| "Helper"; "Utils"; "Common"; "Shared" |]
                StagesDir = Path.Combine(runtime.ParsersDir, "stages")
                Scopes =
                    match root.TryGetProperty("scopes") with
                    | true, scopesEl ->
                        scopesEl.EnumerateObject()
                        |> Seq.map (fun prop ->
                            { Name = prop.Name
                              Dirs = prop.Value.EnumerateArray() |> Seq.map (fun v -> v.GetString()) |> Seq.toArray })
                        |> Seq.toArray
                    | _ ->
                        let srcDirs = strArr "srcDirs" (detectSrcDirs repoRoot)
                        detectScopes repoRoot srcDirs
            }
        else
            let srcDirs = detectSrcDirs repoRoot
            {
                RepoRoot = repoRoot
                SrcDirs = srcDirs
                Extensions = defaultExtensions
                Exclude = defaultExclude
                IndexDir = indexDir
                SummaryCache = Path.Combine(indexDir, ".summary-cache.tsv")
                EmbeddingUrl = defaultEmbeddingUrl
                EmbeddingTimeoutSeconds = defaultEmbeddingTimeoutSeconds
                EmbeddingBatchSize = 50
                LlmUrl = "http://localhost:8090/v1/chat/completions"
                LlmModel = "bonsai"
                LlmMaxTokens = 60
                LlmTemperature = 0.0
                ChunkerScript = Path.Combine(runtime.ParsersDir, "ts-chunker.js")
                NodePath = "node"
                MaxChunkChars = 3000
                NoiseWords = [||]
                UtilityPatterns = [| "Helper"; "Utils"; "Common"; "Shared" |]
                StagesDir = Path.Combine(runtime.ParsersDir, "stages")
                Scopes = detectScopes repoRoot srcDirs
            }

    /// Get directories for a named scope.
    let scopeDirs (cfg: CodeSightConfig) (scope: string) =
        match cfg.Scopes |> Array.tryFind (fun s -> s.Name = scope) with
        | Some s -> s.Dirs
        | None -> cfg.SrcDirs
