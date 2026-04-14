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
using SourceRAG.Application.Common;
using SourceRAG.Domain.Entities;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Vcs.Svn;

public sealed class SvnReindexStrategy : IReindexStrategy
{
    private readonly IVcsProvider _vcsProvider;
    private readonly SourceRagOptions _options;

    public SvnReindexStrategy(IVcsProvider vcsProvider, IOptions<SourceRagOptions> options)
    {
        _vcsProvider = vcsProvider;
        _options     = options.Value;
    }

    public async Task<ReindexScope> DetermineChangedFilesAsync(
        string repoPath, string lastIndexedRevision, CancellationToken ct)
    {
        var changedFiles    = await _vcsProvider.GetChangedFilesSinceAsync(repoPath, lastIndexedRevision, ct);
        var currentRevision = _vcsProvider.GetCurrentRevision(repoPath);
        return new ReindexScope(changedFiles, lastIndexedRevision, currentRevision);
    }
}
