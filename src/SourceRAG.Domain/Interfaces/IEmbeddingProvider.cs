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

namespace SourceRAG.Domain.Interfaces;

public interface IEmbeddingProvider
{
    int Dimensions { get; }

    /// <summary>
    /// Warms up the provider and ensures <see cref="Dimensions"/> reflects the
    /// actual model size. Implementations may be no-ops for API-based providers.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    Task InitializeAsync(CancellationToken ct);

    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}
