# Third-party notices

## grill-me (Matt Pocock) — MIT

Act 1 of this plugin (the "grill" interview: one question at a time, a recommended
answer per question, exploring the codebase instead of asking when possible) derives
from Matt Pocock's `grill-me` prompt, used under the MIT license, as carried forward by
the `grill-me-codex` skill and the Cursor Plan Forge Flow ported here to Codex.

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this
software and associated documentation files (the "Software"), to deal in the Software
without restriction, including without limitation the rights to use, copy, modify,
merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice shall be included in all copies
or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE
OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## .NET production dependencies

The bundled `planforge` executable is built with the following NuGet dependency:

- `System.CommandLine` 2.0.0, MIT License, Microsoft Corporation and contributors.
  Source: https://github.com/dotnet/command-line-api

The xUnit and Microsoft.NET.Test.Sdk packages are test-only dependencies and
are not included in release bundles.
