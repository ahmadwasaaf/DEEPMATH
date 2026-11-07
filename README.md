# BATCH STUDIO Desktop

Windows accounting & invoicing app built on the BATCH STUDIO Excel system.

- One app, several entities - each entity has its own **Database** folder (e.g. `Afaq Database`, `Royal Blocks Database`).
- Data stays in Excel files inside the Database folder, kept in **two-way sync** with the app.
- Invoice templates live in `Database\Templates`; the app detects each template's variable cells automatically.
- English / Arabic UI, dark / light theme.

## Database folder
```
<Entity> Database\
  Suppliers\Suppliers.xlsx
  Clients\Clients.xlsx
  Items\Items.xlsx
  Categories\Categories.xlsx
  INVOICE HISTORY\Invoice History.xlsx
  Templates\            (INVOICE.xlsx, INV1.xlsx, Madar.xlsx, ... + .map.json)
  BATCH STUDIO\SUPPLIERS\
  BATCH STUDIO\CLIENTS\
  .batchstudio\         (hidden: entity info, backups)
```

## Build
Every push to `main` builds the installer on GitHub Actions (Actions tab -> latest run -> Artifacts -> **BatchStudio-Setup**).
Tag `v0.1.0` etc. to publish a release.

Local build: install the .NET 10 SDK, then `dotnet run --project src/BatchStudio.App`.
