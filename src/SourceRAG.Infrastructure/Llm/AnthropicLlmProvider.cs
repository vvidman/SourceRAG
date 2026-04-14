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

using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Microsoft.Extensions.Options;
using SourceRAG.Application.Common;
using SourceRAG.Domain.Interfaces;

namespace SourceRAG.Infrastructure.Llm;

public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly AnthropicOptions _options;

    public AnthropicLlmProvider(IOptions<SourceRagOptions> options)
    {
        _options = options.Value.Anthropic;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, CancellationToken ct)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY environment variable is not set.");

        var client = new AnthropicClient(new APIAuthentication(apiKey));

        var parameters = new MessageParameters
        {
            Model     = _options.Model,
            MaxTokens = 4096,
            System    = [new SystemMessage(systemPrompt, null!)],
            Messages  = [new Message(RoleType.User, userMessage, null!)]
        };

        var response = await client.Messages.GetClaudeMessageAsync(parameters, ct);

        return response.FirstMessage.Text;
    }
}
