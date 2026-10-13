# Card value export coverage

From the repository root:

```powershell
dotnet run --project STS2MCP/tests/CardValues/CardValues.csproj
```

The executable compiles the real `McpMod.CardValues.cs` against small game type stand-ins. It injects errors after a variable has been changed, in an enchantment preview, and during description formatting. It also catches an error inside the simulated DTO builder to check that the outer state-build scope still refuses publication.

The checks cover restoration of both enchanted and preview values, nested description rendering, request cleanup after failure, successful unpowered output, and the existing powered display fallback. It does not exercise Godot, real game hooks, or the HTTP server. Build the mod against the game assemblies separately; a running-game acceptance check is still required after installation.
