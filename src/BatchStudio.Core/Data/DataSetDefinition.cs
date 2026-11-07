namespace BatchStudio.Core.Data;

/// <summary>Describes one master-data set stored as an Excel file inside the entity's Database folder.</summary>
public sealed record DataSetDefinition(
    string Key,              // stable id: suppliers, clients, items, categories, history
    string Folder,           // sub-folder of the Database root
    string FileName,         // Excel file inside the folder
    string SheetName,        // worksheet holding the table (row 1 = headers)
    string IdPrefix,         // used to generate new ids, e.g. "SUP" -> SUP-052 ("" = user enters id)
    IReadOnlyList<string> DefaultHeaders)
{
    public string RelativePath => Path.Combine(Folder, FileName);
    public string FullPath(string root) => Path.Combine(root, Folder, FileName);
}

public static class DataSets
{
    public static readonly DataSetDefinition Suppliers = new(
        "suppliers", "Suppliers", "Suppliers.xlsx", "Suppliers", "SUP",
        ["Supplier ID", "Supplier Name (English)", "Chinese Name", "Company Address",
         "Banking Details (full paragraph)", "Category (English | Arabic)", "DEFAULT TEMPLATE",
         "Company’s Activities"]);

    public static readonly DataSetDefinition Clients = new(
        "clients", "Clients", "Clients.xlsx", "Clients", "CLI",
        ["Client ID", "Client Name", "Client Category", "Company’s Activities"]);

    public static readonly DataSetDefinition Items = new(
        "items", "Items", "Items.xlsx", "Items", "ITM",
        ["Code", "Item Name", "Approx. Price", "Category ID", "Status"]);

    public static readonly DataSetDefinition Categories = new(
        "categories", "Categories", "Categories.xlsx", "Categories", "",
        ["Category ID", "Category (English | Arabic)", "Status"]);

    public static readonly DataSetDefinition InvoiceHistory = new(
        "history", "INVOICE HISTORY", "Invoice History.xlsx", "Invoice History", "INV",
        ["#", "Name", "Amount", "Date", "Invoice No", "Status", "Category", "Party Type", "File Path"]);

    /// <summary>Data sets edited on the master-data pages (phase 1).</summary>
    public static IReadOnlyList<DataSetDefinition> MasterData { get; } = [Suppliers, Clients, Items, Categories];

    public static IReadOnlyList<DataSetDefinition> All { get; } = [Suppliers, Clients, Items, Categories, InvoiceHistory];

    public static DataSetDefinition? ByKey(string key) =>
        All.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
}
