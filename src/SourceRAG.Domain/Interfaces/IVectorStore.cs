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

public interface IVectorStore
{
    Task EnsureCollectionAsync(int dimensions, CancellationToken ct);
    Task UpsertAsync(Guid pointId, float[] vector, ChunkMetadata metadata, CancellationToken ct);
    Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int topK, CancellationToken ct);
    Task DeleteAsync(Guid pointId, CancellationToken ct);
    /// <summary>
    /// Deletes all points whose payload contains file_path == <paramref name="filePath"/>.
    /// Used to clean up chunks when a file is deleted from the repository.
    /// </summary>
    Task DeleteByFilePathAsync(string filePath, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
