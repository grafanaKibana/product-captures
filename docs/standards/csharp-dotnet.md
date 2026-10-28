# C# and .NET engineering standard

Apply this guidance to C# and .NET changes, including project and build configuration. Read the repository's instructions and surrounding implementation first. Closer, explicit project requirements take precedence; preserve established public contracts and supported runtimes. These are general .NET guidelines, not a requirement to introduce a framework or rewrite existing code.

## Readability and organization

- Use descriptive domain names, explicit accessibility, PascalCase types/members and camelCase parameters/locals. Async methods that return tasks normally end in `Async`.
- Use four spaces, Allman braces, braces for every control-flow body, one statement/declaration per line, and expanded blocks. Keep project XML readable with one element per line.
- Keep methods cohesive. Extract a private helper when its name explains a distinct operation; preserve validation order, disposal scopes and observable behavior. Prefer existing utilities over new layers.
- Prefer one principal type per file when that improves navigation. Closely related small records and configuration types may stay together. Do not create ritual type files or arbitrary method-length limits.
- Use modern language features when they clarify the code and are supported by the project. Do not change SDK, target framework, packages or architecture as a side effect of style work.

## Correctness and boundaries

- Preserve public APIs, serialized names, defaults, errors, ownership boundaries and compatibility unless the requested change explicitly alters them.
- Validate untrusted input at the boundary. Keep failure order stable during refactoring; avoid logging secrets or broadening filesystem/network access.
- Use exceptions for failures, catch at a boundary that can recover or report meaningfully, and preserve stack traces with `throw;`. Dispose owned resources with `using`/`await using` or `finally`.
- Use asynchronous APIs for I/O where the existing contract permits them. Propagate cancellation through awaited work, retain token/source lifetimes, and await owned cleanup. Do not wrap synchronous I/O in `Task.Run` just to appear asynchronous or change a synchronous API during an unrelated refactor.
- For child processes, use structured arguments, drain stdout and stderr concurrently, observe exit codes and timeouts, and clean up owned descendants. Preserve startup-race and cancellation safeguards.
- Apply ASP.NET, persistence, UI, dependency injection or cloud practices only when that technology is already relevant. Add no framework or dependency merely to satisfy this document.

## Verification

Test observable behavior with deterministic, isolated checks. Add a regression before changing an uncovered behavior-sensitive boundary; do not duplicate tests solely for style. Run the repository's formatter, analyzers, targeted tests and build. Preserve runtime string values and generated assets during formatting; use byte/parity checks when output fidelity is a contract. Report unavailable checks and warnings accurately.

## Enforcement and project choices

The four-space/Allman/always-braces choices above are this standard's defaults, not universal C# requirements. Repository `.editorconfig` files and SDK analyzers enforce only the configured subset; agent instructions guide judgment and do not mechanically enforce prompts. `dotnet format --verify-no-changes --no-restore` complements `EnforceCodeStyleInBuild`; neither proves behavioral correctness. Do not add analyzer packages or suppress diagnostics to hide problems.

## Sources

This is an authored synthesis, not a copied framework checklist:

- [Microsoft C# coding conventions](https://learn.microsoft.com/en-us/dotnet/csharp/fundamentals/coding-style/coding-conventions): naming, layout and clarity.
- [dotnet/runtime coding style](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md): upstream conventions; project-specific choices differ.
- [GitHub Awesome Copilot C# instructions](https://github.com/github/awesome-copilot/blob/main/instructions/csharp.instructions.md): an upstream agent-instruction example; framework guidance is conditional.
- [Microsoft async scenarios](https://learn.microsoft.com/en-us/dotnet/csharp/asynchronous-programming/async-scenarios), [cancellation](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads), and [exception practices](https://learn.microsoft.com/en-us/dotnet/standard/exceptions/best-practices-for-exceptions): I/O, lifetimes and recovery.
- [Microsoft unit-testing practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices): meaningful, repeatable tests.
- [C# formatting options](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/csharp-formatting-options), [IDE0011 braces](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/style-rules/ide0011), [SDK code analysis](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview), and [dotnet format](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-format): enforceable configuration and its limits.
