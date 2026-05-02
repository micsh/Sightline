namespace AITeam.Sight.Core

open System
open System.Collections.Generic
open System.IO
open System.Net.Http
open System.Net.Sockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

/// HTTP client for embedding model (OpenAI-compatible /v1/embeddings).
module EmbeddingService =

    let DefaultRequestTimeoutSeconds = 120

    let private client =
        let httpClient = new HttpClient()
        httpClient.Timeout <- Timeout.InfiniteTimeSpan
        httpClient

    let private trimForMessage (text: string) =
        if String.IsNullOrWhiteSpace(text) then ""
        else
            let compact = text.Replace("\r", " ").Replace("\n", " ").Trim()
            if compact.Length > 200 then compact.Substring(0, 200) + "…" else compact

    let private formatTimeout (timeout: TimeSpan) =
        if timeout.TotalMilliseconds < 1000.0 then sprintf "%.0fms" timeout.TotalMilliseconds
        elif abs (timeout.TotalSeconds - Math.Round(timeout.TotalSeconds)) < 0.0001 then sprintf "%.0fs" timeout.TotalSeconds
        else sprintf "%.1fs" timeout.TotalSeconds

    let private tryFindSocketException (ex: exn) =
        let rec loop (current: exn) =
            match current with
            | null -> None
            | :? SocketException as socket -> Some socket
            | _ -> loop current.InnerException
        loop ex

    let private malformedResponse detail =
        Error (sprintf "embedding response was malformed: %s" detail)

    let private parseEmbeddings expectedCount (json: string) =
        try
            use doc = JsonDocument.Parse(json)
            let data =
                match doc.RootElement.TryGetProperty("data") with
                | true, value when value.ValueKind = JsonValueKind.Array -> value
                | true, _ -> invalidOp "property 'data' was not an array"
                | _ -> invalidOp "missing property 'data'"

            let result =
                data.EnumerateArray()
                |> Seq.mapi (fun index item ->
                    let embedding =
                        match item.TryGetProperty("embedding") with
                        | true, value when value.ValueKind = JsonValueKind.Array -> value
                        | true, _ -> invalidOp "property 'embedding' was not an array"
                        | _ -> invalidOp "missing property 'embedding'"

                    let values =
                        embedding.EnumerateArray()
                        |> Seq.map (fun value -> value.GetSingle())
                        |> Seq.toArray

                    if values.Length = 0 then
                        invalidOp (sprintf "embedding at index %d was empty" index)

                    values)
                |> Seq.toArray

            if result.Length <> expectedCount then
                invalidOp (sprintf "expected %d embeddings but received %d" expectedCount result.Length)

            let expectedDim = result.[0].Length
            result
            |> Array.iteri (fun index values ->
                if values.Length <> expectedDim then
                    invalidOp (sprintf "embedding at index %d had dimension %d; expected %d" index values.Length expectedDim))

            Ok result
        with
        | :? JsonException as ex ->
            malformedResponse (trimForMessage ex.Message)
        | :? InvalidOperationException as ex ->
            malformedResponse (trimForMessage ex.Message)
        | :? KeyNotFoundException as ex ->
            malformedResponse (trimForMessage ex.Message)

    let private classifyHttpRequestException (url: string) (ex: HttpRequestException) =
        match tryFindSocketException ex with
        | Some socket when socket.SocketErrorCode = SocketError.ConnectionRefused ->
            sprintf "embedding request connect-refused for %s" url
        | Some socket when socket.SocketErrorCode = SocketError.HostNotFound
                           || socket.SocketErrorCode = SocketError.NoData
                           || socket.SocketErrorCode = SocketError.TryAgain ->
            sprintf "embedding request DNS/NXDOMAIN failure for %s" url
        | _ ->
            match ex.HttpRequestError with
            | HttpRequestError.NameResolutionError ->
                sprintf "embedding request DNS/NXDOMAIN failure for %s" url
            | _ ->
                sprintf "embedding request failed for %s: %s" url (trimForMessage ex.Message)

    let embedWithTimeout (timeout: TimeSpan) (url: string) (texts: string[]) = task {
        let body = JsonSerializer.Serialize {| input = texts |}
        use content = new StringContent(body, Encoding.UTF8, "application/json")
        use cts = new CancellationTokenSource(timeout)
        try
            let! response = client.PostAsync(url, content, cts.Token)
            if not response.IsSuccessStatusCode then
                let! errorBody = response.Content.ReadAsStringAsync(cts.Token)
                let detail = trimForMessage errorBody
                let suffix = if detail = "" then "" else sprintf ": %s" detail
                return Error (sprintf "embedding request failed with HTTP %d %s%s" (int response.StatusCode) response.ReasonPhrase suffix)
            else
                let! json = response.Content.ReadAsStringAsync(cts.Token)
                return parseEmbeddings texts.Length json
        with
        | :? TaskCanceledException when cts.IsCancellationRequested ->
            return Error (sprintf "embedding request timed out after %s for %s" (formatTimeout timeout) url)
        | :? HttpRequestException as ex ->
            return Error (classifyHttpRequestException url ex)
        | ex ->
            return Error (sprintf "embedding request threw %s: %s" (ex.GetType().Name) ex.Message)
    }

    let embed (url: string) (texts: string[]) =
        embedWithTimeout (TimeSpan.FromSeconds(float DefaultRequestTimeoutSeconds)) url texts

    /// Check if the embedding server is reachable.
    let probe (url: string) = task {
        try
            let! result = embed url [| "test" |]
            return result.IsOk
        with _ -> return false
    }
