# Contributing to QuickHue

Thanks for helping improve QuickHue.

## Development setup

QuickHue requires Windows 11 and the .NET 10 SDK. Restore the locked dependency
graph, build the solution, verify formatting, and run the dependency-free test
executable:

```powershell
dotnet restore QuickHue.slnx --locked-mode
dotnet build QuickHue.slnx --configuration Release --no-restore
dotnet format QuickHue.slnx --verify-no-changes --no-restore
dotnet run --project .\tests\QuickHue.Tests\QuickHue.Tests.csproj --configuration Release --no-build
```

The test suite opens local loopback listeners and exercises Windows APIs,
including DPAPI, global hotkeys, mutexes, and certificate handling. Run it in a
normal interactive Windows user session.

## UI changes

Generate previews in both Windows themes before submitting a visual change:

```powershell
dotnet run --project .\tests\QuickHue.Tests\QuickHue.Tests.csproj --configuration Release -- --ui-preview .\artifacts\ui
```

Include relevant before-and-after screenshots in the pull request.

## Pull requests

- Keep changes focused and explain the user-visible effect.
- Add or update tests for behavior changes.
- Preserve certificate pinning and DPAPI protection.
- Never include a Hue application key, bridge address, local configuration, or
  other user data in commits, screenshots, logs, or issues.
- Ensure the CI workflow passes.
