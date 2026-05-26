using Newtonsoft.Json.Linq;

namespace Formulaar1
{
    /// <summary>
    /// Direct-HTTP access to Sonarr's manual import endpoints. Same shim
    /// treatment as the series/history/queue shims -- the bundled
    /// <c>APIv3SonarrDotcore</c> client doesn't expose these endpoints,
    /// and even if it did the deeply-nested response would trip the
    /// <c>MediaCoverTypes</c> deserialiser.
    ///
    /// <para>
    /// Two halves to the flow, used when <c>ImportMode: "manualimport"</c>
    /// is set (the fix22 config flag):
    /// </para>
    /// <list type="number">
    /// <item><see cref="GetSuggestionsAsync"/> -- GETs candidate items from
    /// a folder; Sonarr's parser auto-populates quality / language / episode
    /// metadata on each.</item>
    /// <item><see cref="CommitViaCommandAsync"/> -- POSTs the enriched items
    /// to <c>/api/v3/command</c> as a <c>ManualImport</c> command. This goes
    /// through Sonarr's task queue, which IS what publishes
    /// <c>EpisodeFileImported</c> and <c>SeriesUpdated</c> events to SignalR
    /// (so the web UI auto-refreshes). The direct
    /// <c>/api/v3/manualimport</c> POST endpoint imports inline but bypasses
    /// the event pipeline -- we tried it in fix14-17, hit the UI-refresh
    /// regression, and switched to the command bus in fix23.</item>
    /// </list>
    /// <para>
    /// <see cref="GetCommandStatusAsync"/> polls the dispatched command's
    /// status so the caller knows whether the import actually succeeded
    /// (fix24) before deleting the queue entry.
    /// </para>
    /// <para>
    /// We treat the response items as opaque <see cref="JObject"/> so we
    /// don't have to model Sonarr's full ManualImportResource shape; we
    /// just trust the GET output (with downloadId / episodeIds added) and
    /// hand it back to Sonarr.
    /// </para>
    /// </summary>
    internal static class SonarrManualImportShim
    {
        /// <summary>
        /// GET /api/v3/manualimport?folder=&lt;folder&gt;&amp;filterExistingFiles=true.
        ///
        /// <para>
        /// Note on downloadId: we deliberately do NOT pass it in the GET query.
        /// Empirical testing (fix15-era diagnostic curls against Sonarr's API)
        /// showed that GET /api/v3/manualimport?downloadId=X filters to 0
        /// results when the folder is anything other than the qBit download
        /// path -- Sonarr's logic appears to be "show me files belonging to
        /// download X in this folder," and our hardlink staging dir isn't
        /// qBit's path, so nothing matches. Without downloadId in the GET,
        /// Sonarr does a plain folder scan and returns the hardlinked file
        /// as importable. The caller still injects downloadId on each item
        /// before CommitAsync so the POST links the import to the queue
        /// entry atomically -- best of both worlds.
        /// </para>
        /// </summary>
        public static async Task<List<JObject>> GetSuggestionsAsync(
            HttpClient http, string basePath, string apiKey, string folder)
        {
            var url = $"{basePath.TrimEnd('/')}/api/v3/manualimport" +
                      $"?folder={Uri.EscapeDataString(folder)}" +
                      $"&filterExistingFiles=true";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            var arr = JArray.Parse(json);
            return arr.OfType<JObject>().ToList();
        }

        /// <summary>
        /// Command-bus form of manual import (fix23). POSTs to
        /// <c>/api/v3/command</c> with <c>{ name: "ManualImport", importMode,
        /// files: [...] }</c>. Sonarr dispatches a <c>ManualImportCommand</c>
        /// through its task queue, which IS what publishes
        /// <c>EpisodeFileImported</c> and <c>SeriesUpdated</c> events to
        /// SignalR -- so the web UI auto-refreshes the series page and any
        /// other connected client sees the import in real time.
        ///
        /// <para>
        /// Async dispatch: Sonarr returns 201 Created with a command id
        /// immediately; the actual import runs in the background. The 30s
        /// wait in the caller's smart queue cleanup is plenty for the
        /// command to finish for a single file.
        /// </para>
        ///
        /// <para>
        /// The <paramref name="items"/> JObjects must include the fields
        /// Sonarr's <c>ManualImportFile</c> model needs: <c>path</c>,
        /// <c>folderName</c>, <c>seriesId</c>, <c>episodeIds</c>,
        /// <c>quality</c>, <c>languages</c>, <c>releaseGroup</c>,
        /// <c>indexerFlags</c>, <c>downloadId</c>. Most come straight from
        /// the GET response; the caller promotes the nested ones and
        /// derives <c>folderName</c> from <c>path</c>.
        /// </para>
        /// </summary>
        /// <summary>
        /// Result of dispatching the ManualImport command. <see cref="CommandId"/>
        /// is populated when Sonarr accepts the POST and returns the queued
        /// command resource; null on failure (the body is in <see cref="Detail"/>).
        /// Callers use <see cref="GetCommandStatusAsync"/> to poll to completion.
        /// </summary>
        public sealed class CommandDispatchResult
        {
            public bool Success { get; init; }
            public int? CommandId { get; init; }
            public string Detail { get; init; } = string.Empty;
        }

        public static async Task<CommandDispatchResult> CommitViaCommandAsync(
            HttpClient http, string basePath, string apiKey, List<JObject> items, string importMode = "auto")
        {
            var payload = new JObject
            {
                ["name"] = "ManualImport",
                ["importMode"] = importMode,
                ["files"] = new JArray(items.Cast<JToken>().ToArray()),
            };
            var url = $"{basePath.TrimEnd('/')}/api/v3/command";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Api-Key", apiKey);
            req.Content = new StringContent(payload.ToString(), System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();

            if (!resp.IsSuccessStatusCode)
            {
                return new CommandDispatchResult { Success = false, Detail = respBody };
            }

            // Sonarr returns the queued CommandResource: { "id": 12345, "name":
            // "ManualImport", "status": "queued", ... }. Extract the id so the
            // caller can poll for completion.
            int? cmdId = null;
            try
            {
                var parsed = JObject.Parse(respBody);
                cmdId = parsed["id"]?.Value<int?>();
            }
            catch
            {
                // Body wasn't JSON we could parse; treat as success-without-id
                // (caller will fall back to the legacy timed cleanup path).
            }
            return new CommandDispatchResult { Success = true, CommandId = cmdId, Detail = respBody };
        }

        /// <summary>
        /// Status of a Sonarr command, as returned by GET /api/v3/command/{id}.
        /// We only model the fields we read; everything else (queued/started
        /// timestamps, priority, etc.) is irrelevant to import-success polling.
        /// </summary>
        public sealed class CommandStatus
        {
            // Sonarr's CommandStatus enum: queued | started | completed | failed | aborted | cancelled | orphaned
            public string Status { get; init; } = "";
            // CommandResult enum: unknown | successful | unsuccessful
            public string Result { get; init; } = "";
            public string? Exception { get; init; }
            public bool IsEnded => Status switch
            {
                "completed" => true,
                "failed" => true,
                "aborted" => true,
                "cancelled" => true,
                "orphaned" => true,
                _ => false,
            };
        }

        /// <summary>
        /// GET /api/v3/command/{id}. Returns the current status of a previously
        /// dispatched command. Used by the import flow (fix24) to poll the
        /// ManualImport command to completion so we know whether the import
        /// actually succeeded before we DELETE the queue entry.
        /// </summary>
        public static async Task<CommandStatus?> GetCommandStatusAsync(
            HttpClient http, string basePath, string apiKey, int commandId)
        {
            var url = $"{basePath.TrimEnd('/')}/api/v3/command/{commandId}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", apiKey);
            using var resp = await http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            try
            {
                var parsed = JObject.Parse(json);
                return new CommandStatus
                {
                    Status = parsed["status"]?.ToString() ?? "",
                    Result = parsed["result"]?.ToString() ?? "",
                    Exception = parsed["exception"]?.ToString(),
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
