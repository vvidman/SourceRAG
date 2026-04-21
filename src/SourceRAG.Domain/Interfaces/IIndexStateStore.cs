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

using SourceRAG.Domain.Entities;

namespace SourceRAG.Domain.Interfaces;

public interface IIndexStateStore
{
    Task<string?> GetLastIndexedRevisionAsync(string repoPath, CancellationToken ct);
    Task SetLastIndexedRevisionAsync(string repoPath, string revision, DateTimeOffset indexedAt, CancellationToken ct);
    Task<DateTimeOffset?> GetLastIndexedAtAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Saves a mid-run checkpoint. Called after each successfully processed file
    /// during a full reindex so the run can resume after a crash.
    /// </summary>
    Task SaveCheckpointAsync(string repoPath, string revision, string lastProcessedFile, CancellationToken ct);

    /// <summary>
    /// Returns the current checkpoint for the given repo, or null if none exists.
    /// </summary>
    Task<IndexCheckpoint?> GetCheckpointAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Removes the checkpoint after a successful full reindex completion.
    /// </summary>
    Task ClearCheckpointAsync(string repoPath, CancellationToken ct);
}
