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

using SourceRAG.Infrastructure.Vcs.State;

namespace SourceRAG.Infrastructure.Tests.Vcs.State;

public sealed class FileIndexStateStoreTests : IDisposable
{
    private readonly string _repoPath;
    private readonly FileIndexStateStore _sut;

    public FileIndexStateStoreTests()
    {
        _repoPath = Path.Combine(Path.GetTempPath(), $"sourcerag-state-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoPath);
        _sut = new FileIndexStateStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task SetAndGet_LastIndexedRevision_RoundTrips()
    {
        var revision  = "a3f9c12deadbeef";
        var indexedAt = new DateTimeOffset(2026, 4, 13, 10, 0, 0, TimeSpan.Zero);

        await _sut.SetLastIndexedRevisionAsync(_repoPath, revision, indexedAt, CancellationToken.None);
        var result = await _sut.GetLastIndexedRevisionAsync(_repoPath, CancellationToken.None);
        var at     = await _sut.GetLastIndexedAtAsync(_repoPath, CancellationToken.None);

        Assert.Equal(revision, result);
        Assert.Equal(indexedAt, at);
    }

    [Fact]
    public async Task Get_BeforeSet_ReturnsNull()
    {
        var revision = await _sut.GetLastIndexedRevisionAsync(_repoPath, CancellationToken.None);
        var at       = await _sut.GetLastIndexedAtAsync(_repoPath, CancellationToken.None);

        Assert.Null(revision);
        Assert.Null(at);
    }
}
