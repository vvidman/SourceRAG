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

namespace SourceRAG.Application.Common;

public sealed class PipelineContext
{
    public string RepositoryPath       { get; set; } = string.Empty;
    public string Branch               { get; set; } = string.Empty;
    public string? LastIndexedRevision { get; set; }
    public int ProcessedFileCount      { get; set; }
    public int UpsertedChunkCount      { get; set; }
    public int DeletedChunkCount       { get; set; }
    public DateTimeOffset StartedAt    { get; } = DateTimeOffset.UtcNow;
}
