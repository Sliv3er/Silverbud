# Contributing to Silverbud

Thanks for helping. Bug reports, captures from other Galaxy Buds models and pull requests are all welcome.

## Reporting a bug

[Open an issue](https://github.com/Sliv3er/Silverbud/issues/new/choose) and include:

- your Windows version, and your buds' model and firmware (shown in **Device → About these buds**)
- what you did, what you expected, and what happened
- the relevant part of `%APPDATA%\Silverbud\silverbud.log`. It lists every message exchanged with the buds. Remove anything you consider private, such as your device's Bluetooth address.

## Development setup

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows 10/11.
2. Open `Silverbud.sln` in Visual Studio or Rider, or use the CLI:

   ```bash
   dotnet build Silverbud.sln
   dotnet run --project tests/Silverbud.Tests
   ```

3. Run the app with `--demo` to work on the UI without buds.

## Guidelines

- **Keep the protocol code testable.** `Core/Protocol.cs` and `Core/BudsStatus.cs` have no Windows dependencies and are compiled into the test project. Add a check to `tests/Silverbud.Tests/Program.cs` for every new message layout, ideally using bytes captured from real buds.
- **Document what you learn.** New message ids, offsets or firmware quirks belong in [docs/PROTOCOL.md](docs/PROTOCOL.md). Mark anything verified on hardware as *observed*.
- **Match the existing style.** Same naming, comment density and UI vocabulary. Settings are named by what people control, not by protocol names.
- **Be careful with device actions.** Never send unknown message ids to real buds. Some ids reset or power off the buds.

## Releasing

1. Update `<Version>` in `src/Silverbud/Silverbud.csproj` and add an entry to `CHANGELOG.md`.
2. `dotnet publish src/Silverbud/Silverbud.csproj -c Release`
3. Create a GitHub release with the tag `vX.Y.Z` and attach `Silverbud.exe`.
