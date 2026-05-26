using Newtonsoft.Json.Linq;

namespace Formulaar1
{
    /// <summary>
    /// Minimal direct-HTTP access to Sonarr's <c>/api/v3/manualimport</c>
    /// endpoint. Same shim treatment as the series/history/queue shims --
    /// the bundled <c>APIv3SonarrDotcore</c> client doesn't expose this
    /// endpoint, and even if it did the deeply-nested response would trip
    /// the <c>MediaCoverTypes</c> deserialiser.
    ///
    /// <para>
    /// Why we use this instead of <c>DownloadedEpisodesScan</c> (opt-in via
    /// the <c>ImportMode: "manualimport"</c> config flag added in fix22):
    /// </para>
    /// <para>
    /// The scan command imports files from a folder but doesn't link the
    /// resulting import back to the queue entry it came from. Sonarr's
    /// Completed Download Handler then keeps the queue tracking entry in a
    /// stuck 'Waiting to Import / Invalid season or episode' state because
    /// it never sees a matching import event. Smart queue cleanup (fix19)
    /// catches this on the back end.
    /// </para>
    /// <para>
    /// <c>manualimport</c> instead accepts <c>downloadId</c> on each item
    /// (this is what the Manual Import dialog in the Sonarr UI uses) and
    /// links the import to the queue entry atomically. Faster than scan
    /// (no filesystem walk), more precise (we tell Sonarr the exact episode
    /// IDs instead of relying on its parser to extract SxxExx from the
    /// hardlinked filename), and avoids spinning up storage drives.
    /// </para>
    /// <para>
    /// We treat the response items as opaque <see cref="JObject"/> so we
    /// don't have to model Sonarr's full ManualImportResource shape; we
    /// just trust the GET output (with downloadId added) and POST it back.
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
        /// Direct POST to /api/v3/manualimport. Imports inline -- the call
        /// returns once the files are linked into the library -- but does
        /// NOT go through Sonarr's command/event pipeline. This was the
        /// fix14-17 default and was reverted in fix18 because the UI didn't
        /// auto-refresh after the import (the originating client, which is
        /// us, doesn't have a browser to self-refresh, and the
        /// EpisodeFileImported event that would have notified other clients
        /// over SignalR isn't fired on this endpoint).
        ///
        /// Kept here as a fallback / diagnostic option; <see cref="CommitViaCommandAsync"/>
        /// is the path that triggers SignalR events.
        /// </summary>
        public static async Task<(bool Success, string Detail)> CommitAsync(
            HttpClient http, string basePath, string apiKey, List<JObject> items)
        {
            var body = new JArray(items.ToArray()).ToString();
            var url = $"{basePath.TrimEnd('/')}/api/v3/manualimport";
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("X-Api-Key", apiKey);
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            using var resp = await http.SendAsync(req);
            var respBody = await resp.Content.ReadAsStringAsync();
            return (resp.IsSuccessStatusCode, respBody);
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
