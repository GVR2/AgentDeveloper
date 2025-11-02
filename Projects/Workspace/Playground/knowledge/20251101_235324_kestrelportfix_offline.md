# Research (offline): kestrel port fix
- Context: manual:kestrel port fix
- Time: 2025-11-01 23:53:24Z

## Kestrel: порт зайнятий (Only one usage of each socket address)
https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel

**Proposed actions:**
- #fix: Change dev server port
- dotnet run --urls http://localhost:0

Ця помилка означає, що порт уже зайнято. Запусти із іншим портом або звільни існуючий процес. У коді агента в нас є Healer, що вміє перейти на вільний порт.
