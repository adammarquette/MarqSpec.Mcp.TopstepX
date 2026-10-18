# McpTestHost (to be written when gh#713-#717 land)

Tier B assumes a small harness, not yet implemented because the tools it drives do not exist:

- `StartAsync(trading)` builds the host with `Program.ConfigureServices`, swaps `IExecutionGateway` for
  `CountingExecutionGateway`, `IMarketDataGateway` for `FakeMarketData` (Tier A), `TopstepXDbContext` for EF
  InMemory (Testcontainers Postgres optional), `TimeProvider` for `FakeTimeProvider`, and connects an MCP client
  over an in-memory stdio pipe.
- `StartHttpOAuthAsync(scopes)` does the same over `TestServer` with a locally minted test JWT (synthetic key
  pair generated per test run; never a real Cognito pool).
- `CallAsync(tool, args, confirm: false)`: when `confirm` is true and the tool returns `Preview`, it calls
  `confirm_trading_action` with the returned token (default HITL flow, gh#717).
- `Fixture(name)`, `RenameAccount`, `EngageKillSwitch`, `AdvanceClock`, `AuditRowsAsync`.

Tier B is excluded from compilation by default (`IncludeTierB=false`). TP-720-A13 goes red the moment a write
tool is registered, which is the signal to implement this harness and flip the flag.
