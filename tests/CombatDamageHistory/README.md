# Owner damage history export coverage

From the repository root, pass the directory containing the matching game assemblies:

```powershell
dotnet run --project STS2MCP/tests/CombatDamageHistory/CombatDamageHistory.csproj -c Release -p:STS2GameDataDir="F:/MyDocuments/Coding/GitHub/Spire2Bot/target/game_data_v0_107_1"
```

The executable exercises the compiled mod exporter and the game's history predicates across side and owner-turn changes. It checks zero HP loss, full blocking, revival results, unrelated receivers, and unavailable history. It constructs damage results directly; native regressions and decompiled command ordering cover how those results arise. This test does not exercise Godot scenes or live HTTP publication.
