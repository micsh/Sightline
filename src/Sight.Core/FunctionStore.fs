namespace AITeam.Sight.Core

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

type FunctionDef = {
    Name: string
    Params: string[]
    Body: string
    Description: string
}

type FunctionStoreConfig = {
    FileName: string
    ReservedNames: Set<string>
}

module FunctionStore =

    /// JS reserved words — shared across all tools.
    let jsReservedNames = set [
        "var"; "let"; "const"; "function"; "return"; "if"; "else"; "for"; "while"
        "do"; "switch"; "case"; "break"; "continue"; "new"; "delete"; "typeof"
        "instanceof"; "in"; "of"; "try"; "catch"; "finally"; "throw"; "class"
        "import"; "export"; "default"; "this"; "super"; "yield"; "async"; "await"
        "__result__"; "pipe"; "tap"; "mergeBy"; "print"
    ]

    let private filePath (config: FunctionStoreConfig) (repoRoot: string) =
        Path.Combine(repoRoot, config.FileName)

    let private jsIdentifierRegex = Regex(@"^[a-zA-Z_$][a-zA-Z0-9_$]*$", RegexOptions.Compiled)

    let isValidIdentifier (name: string) =
        not (String.IsNullOrWhiteSpace name) && jsIdentifierRegex.IsMatch(name)

    let validateName (config: FunctionStoreConfig) (name: string) =
        if not (isValidIdentifier name) then
            Error $"'{name}' is not a valid identifier (use letters, digits, _ or $; must start with letter/_ /$)"
        elif config.ReservedNames.Contains name then
            Error $"'{name}' is reserved (primitive or JS keyword)"
        else Ok name

    let validateParams (ps: string[]) =
        let bad = ps |> Array.tryFind (fun p -> not (isValidIdentifier p))
        match bad with
        | Some p -> Error $"Parameter '{p}' is not a valid identifier"
        | None -> Ok ps

    let private readRequiredString (el: JsonElement) (propertyName: string) =
        match el.TryGetProperty(propertyName) with
        | true, v when v.ValueKind = JsonValueKind.String ->
            let value = v.GetString()
            if String.IsNullOrWhiteSpace value then
                Error $"Property '{propertyName}' must be a non-empty string."
            else
                Ok value
        | true, _ -> Error $"Property '{propertyName}' must be a string."
        | _ -> Error $"Property '{propertyName}' is required."

    let private readOptionalString (el: JsonElement) (propertyName: string) =
        match el.TryGetProperty(propertyName) with
        | true, v when v.ValueKind = JsonValueKind.String -> Ok (v.GetString())
        | true, _ -> Error $"Property '{propertyName}' must be a string."
        | _ -> Ok ""

    let private readParams (el: JsonElement) =
        match el.TryGetProperty("params") with
        | true, v when v.ValueKind = JsonValueKind.Array ->
            v.EnumerateArray()
            |> Seq.map (fun x ->
                if x.ValueKind = JsonValueKind.String then
                    Ok (x.GetString())
                else
                    Error "Property 'params' must contain only strings.")
            |> Seq.fold
                (fun state next ->
                    match state, next with
                    | Ok values, Ok value -> Ok (value :: values)
                    | Error e, _ -> Error e
                    | _, Error e -> Error e)
                (Ok [])
            |> Result.map (List.rev >> List.toArray)
        | true, _ -> Error "Property 'params' must be an array."
        | _ -> Ok [||]

    let private parseFunctionDef (el: JsonElement) =
        match readRequiredString el "name" with
        | Error e -> Error e
        | Ok name ->
            match readParams el with
            | Error e -> Error e
            | Ok ps ->
                match readRequiredString el "body" with
                | Error e -> Error e
                | Ok body ->
                    match readOptionalString el "description" with
                    | Error e -> Error e
                    | Ok description ->
                        Ok {
                            Name = name
                            Params = ps
                            Body = body
                            Description = description
                        }

    let load (config: FunctionStoreConfig) (repoRoot: string) : Result<FunctionDef[], string> =
        let path = filePath config repoRoot
        if not (File.Exists path) then Ok [||]
        else
            try
                let json = File.ReadAllText(path)
                let doc = JsonDocument.Parse(json)
                doc.RootElement.EnumerateArray()
                |> Seq.map parseFunctionDef
                |> Seq.fold
                    (fun state next ->
                        match state, next with
                        | Ok values, Ok value -> Ok (value :: values)
                        | Error e, _ -> Error e
                        | _, Error e -> Error e)
                    (Ok [])
                |> Result.map (List.rev >> List.toArray)
            with ex ->
                Error $"Failed to load function store '{path}': {ex.Message}"

    let save (config: FunctionStoreConfig) (repoRoot: string) (fns: FunctionDef[]) =
        let path = filePath config repoRoot
        let options = JsonSerializerOptions(WriteIndented = true)
        let arr = fns |> Array.map (fun f ->
            dict [
                "name", box f.Name
                "params", box f.Params
                "body", box f.Body
                "description", box f.Description
            ])
        let json = JsonSerializer.Serialize(arr, options)
        File.WriteAllText(path, json)

    let add (config: FunctionStoreConfig) (repoRoot: string) (fn: FunctionDef) =
        match validateName config fn.Name, validateParams fn.Params with
        | Error e, _ | _, Error e -> Error e
        | Ok _, Ok _ ->
            match load config repoRoot with
            | Error e -> Error e
            | Ok existing ->
                let updated = existing |> Array.filter (fun f -> f.Name <> fn.Name) |> Array.append [| fn |]
                save config repoRoot updated
                let verb = if existing |> Array.exists (fun f -> f.Name = fn.Name) then "Updated" else "Added"
                Ok $"{verb} function '{fn.Name}'"

    let remove (config: FunctionStoreConfig) (repoRoot: string) (name: string) =
        match load config repoRoot with
        | Error e -> Error e
        | Ok existing ->
            if existing |> Array.exists (fun f -> f.Name = name) then
                let updated = existing |> Array.filter (fun f -> f.Name <> name)
                save config repoRoot updated
                Ok $"Removed function '{name}'"
            else
                Error $"Function '{name}' not found"
