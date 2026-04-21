/*
   Copyright 2026 Viktor Vidman (vvidman)

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/

using System.Text.Json;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Vcs.State;

public sealed class FileIndexStateStore : IIndexStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<string?> GetLastIndexedRevisionAsync(string repoPath, CancellationToken ct)
    {
        var state = await ReadStateAsync(repoPath, ct);
        return state?.LastIndexedRevision;
    }

    public async Task SetLastIndexedRevisionAsync(
        string repoPath, string revision, DateTimeOffset indexedAt, CancellationToken ct)
    {
        var state = new IndexState
        {
            LastIndexedRevision = revision,
            LastIndexedAt       = indexedAt
        };
        await WriteStateAsync(repoPath, state, ct);
    }

    public async Task<DateTimeOffset?> GetLastIndexedAtAsync(string repoPath, CancellationToken ct)
    {
        var state = await ReadStateAsync(repoPath, ct);
        return state?.LastIndexedAt;
    }

    // ── persistence ──────────────────────────────────────────────────────────

    private static string StateFilePath(string repoPath) =>
        Path.Combine(repoPath, ".sourcerag-state.json");

    private static async Task<IndexState?> ReadStateAsync(string repoPath, CancellationToken ct)
    {
        var path = StateFilePath(repoPath);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<IndexState>(stream, JsonOptions, ct);
    }

    private static async Task WriteStateAsync(string repoPath, IndexState state, CancellationToken ct)
    {
        var finalPath = StateFilePath(repoPath);
        var tempPath  = finalPath + ".tmp";

        await using (var stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, state, JsonOptions, ct);
        }

        File.Move(tempPath, finalPath, overwrite: true);
    }

    public async Task SaveCheckpointAsync(
        string repoPath, string revision, string lastProcessedFile, CancellationToken ct)
    {
        var existing = await ReadStateAsync(repoPath, ct);
        var updated  = new IndexState
        {
            LastIndexedRevision = existing?.LastIndexedRevision,
            LastIndexedAt       = existing?.LastIndexedAt,
            CheckpointRevision  = revision,
            CheckpointLastFile  = lastProcessedFile
        };
        await WriteStateAsync(repoPath, updated, ct);
    }

    public async Task<IndexCheckpoint?> GetCheckpointAsync(string repoPath, CancellationToken ct)
    {
        var state = await ReadStateAsync(repoPath, ct);
        if (state?.CheckpointRevision is null || state.CheckpointLastFile is null)
            return null;
        return new IndexCheckpoint(state.CheckpointRevision, state.CheckpointLastFile);
    }

    public async Task ClearCheckpointAsync(string repoPath, CancellationToken ct)
    {
        var existing = await ReadStateAsync(repoPath, ct);
        if (existing is null) return;
        await WriteStateAsync(repoPath, new IndexState
        {
            LastIndexedRevision = existing.LastIndexedRevision,
            LastIndexedAt       = existing.LastIndexedAt,
            CheckpointRevision  = null,
            CheckpointLastFile  = null
        }, ct);
    }

    // ── inner model ──────────────────────────────────────────────────────────

    private sealed class IndexState
    {
        public string?         LastIndexedRevision { get; init; }
        public DateTimeOffset? LastIndexedAt       { get; init; }
        // Checkpoint fields — null when no in-progress run exists
        public string?         CheckpointRevision  { get; init; }
        public string?         CheckpointLastFile  { get; init; }
    }
}
