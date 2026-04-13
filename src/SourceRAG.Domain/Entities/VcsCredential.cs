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

namespace SourceRAG.Domain.Entities;

public abstract record VcsCredential;
public sealed record NoCredential                                              : VcsCredential;
public sealed record PatCredential(string Pat)                                 : VcsCredential;
public sealed record UserPasswordCredential(string Username, string Password)  : VcsCredential;
public sealed record SshCredential(string KeyPath, string? Passphrase)        : VcsCredential;
