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

using System.ComponentModel;
using MediatR;
using ModelContextProtocol.Server;
using SourceRAG.Application.Indexing;

namespace SourceRAG.McpHost.Tools;

[McpServerToolType]
public sealed class IndexRepositoryTool(IMediator mediator)
{
    [McpServerTool, Description("Trigger full or incremental reindex of the repository")]
    public async Task<IndexJobResult> IndexAsync(
        [Description("'full' or 'incremental'")] string mode = "incremental",
        CancellationToken ct = default)
        => await mediator.Send(new IndexRepositoryCommand(mode == "full"), ct);
}
