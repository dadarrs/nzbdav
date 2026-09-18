# Regression checks

From the repository root, with the .NET 10 SDK installed:

```sh
dotnet test tests/NzbWebDAV.Tests/NzbWebDAV.Tests.csproj
```

The upload tests use a temporary SQLite database and synthetic NZBs. They do not
connect to providers or run a queue worker. They cover duplicate API responses,
concurrent duplicate insertion and blob cleanup, category scoping, and unrelated
database errors.

With frontend dependencies installed:

```sh
cd frontend
node --import tsx tests/range-selection.test.ts
npm run typecheck
npm run build
```

Range selection applies to the currently displayed page in Downloads, History,
and the health-check schedule. Click a checkbox to set the anchor; Shift-, Ctrl-,
or Cmd-click another checkbox to apply its checked state to the inclusive range.
Rows outside that range keep their selection. Page changes and the select-all
checkbox reset the anchor; live row updates use IDs and the current row order.
