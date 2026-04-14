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

using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Enums;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.VectorStore;

public sealed class QdrantVectorStore : IVectorStore
{
    private readonly QdrantClient   _client;
    private readonly QdrantOptions  _options;

    public QdrantVectorStore(QdrantClient client, IOptions<SourceRagOptions> options)
    {
        _client  = client;
        _options = options.Value.Qdrant;
    }

    public async Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
    {
        var exists = await _client.CollectionExistsAsync(_options.CollectionName, ct);
        if (!exists)
        {
            await _client.CreateCollectionAsync(
                _options.CollectionName,
                new VectorParams { Size = (ulong)dimensions, Distance = Distance.Cosine },
                cancellationToken: ct);
        }
    }

    public async Task UpsertAsync(Guid pointId, float[] vector, ChunkMetadata metadata, CancellationToken ct)
    {
        var denseVector = new DenseVector();
        denseVector.Data.AddRange(vector);

        var point = new PointStruct
        {
            Id      = new PointId { Uuid = pointId.ToString() },
            Vectors = new Vectors { Vector = new Vector { Dense = denseVector } }
        };

        point.Payload["file_path"]      = new Value { StringValue  = metadata.FilePath };
        point.Payload["revision"]       = new Value { StringValue  = metadata.Revision };
        point.Payload["author"]         = new Value { StringValue  = metadata.Author };
        point.Payload["commit_message"] = new Value { StringValue  = metadata.CommitMessage.Length > 500
                                                                        ? metadata.CommitMessage[..500]
                                                                        : metadata.CommitMessage };
        point.Payload["timestamp"]      = new Value { StringValue  = metadata.Timestamp.ToString("O") };
        point.Payload["branch"]         = new Value { StringValue  = metadata.Branch };
        point.Payload["symbol_type"]    = new Value { StringValue  = metadata.SymbolType.ToString() };
        point.Payload["start_line"]     = new Value { IntegerValue = metadata.StartLine };
        point.Payload["end_line"]       = new Value { IntegerValue = metadata.EndLine };

        if (metadata.SymbolName is not null)
            point.Payload["symbol_name"] = new Value { StringValue = metadata.SymbolName };

        await _client.UpsertAsync(_options.CollectionName, [point], cancellationToken: ct);
    }

    public async Task<IReadOnlyList<ScoredChunk>> SearchAsync(float[] queryVector, int topK, CancellationToken ct)
    {
        var results = await _client.SearchAsync(
            _options.CollectionName,
            queryVector,
            limit: (ulong)topK,
            cancellationToken: ct);

        return results
            .Select(r => new ScoredChunk(
                new CodeChunk(string.Empty, PayloadToMetadata(r.Payload)),
                r.Score))
            .ToList();
    }

    public async Task DeleteAsync(Guid pointId, CancellationToken ct)
    {
        await _client.DeleteAsync(
            _options.CollectionName,
            pointId,
            cancellationToken: ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        var result = await _client.CountAsync(_options.CollectionName, cancellationToken: ct);
        return (int)result;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ChunkMetadata PayloadToMetadata(
        Google.Protobuf.Collections.MapField<string, Value> payload) => new()
    {
        FilePath      = payload["file_path"].StringValue,
        Revision      = payload["revision"].StringValue,
        Author        = payload["author"].StringValue,
        CommitMessage = payload["commit_message"].StringValue,
        Timestamp     = DateTimeOffset.Parse(payload["timestamp"].StringValue),
        Branch        = payload["branch"].StringValue,
        SymbolName    = payload.TryGetValue("symbol_name", out var sn) ? sn.StringValue : null,
        SymbolType    = Enum.Parse<SymbolType>(payload["symbol_type"].StringValue),
        StartLine     = (int)payload["start_line"].IntegerValue,
        EndLine       = (int)payload["end_line"].IntegerValue
    };
}
