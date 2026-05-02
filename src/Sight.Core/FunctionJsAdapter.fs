namespace AITeam.Sight.Core

open System

module FunctionJsAdapter =

    /// Generate a JS function declaration for injection into the Jint engine.
    let tryRenderDeclaration (fn: FunctionDef) =
        try
            let paramList = fn.Params |> String.concat ", "
            let trimmed = fn.Body.Trim()
            let bodyJs =
                if trimmed.Contains("return ") then
                    trimmed
                else
                    let lines = trimmed.Split('\n') |> Array.map (fun s -> s.Trim()) |> Array.filter (fun s -> s <> "")
                    let joined = lines |> String.concat " "
                    let lastSemi = joined.LastIndexOf(';')
                    if lastSemi > 0 && lastSemi < joined.Length - 2 then
                        let stmts = joined.Substring(0, lastSemi + 1)
                        let expr = joined.Substring(lastSemi + 1).Trim()
                        if expr.Length > 0 then $"{stmts} return {expr};"
                        else $"return {joined};"
                    else
                        $"return {joined};"
            Ok $"function {fn.Name}({paramList}) {{ {bodyJs} }}"
        with ex ->
            Error $"Malformed function '{fn.Name}': {ex.Message}"
