using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchStudio.Core.Templates;

public static class TemplateFields
{
    public const string PartyName = "party_name";
    public const string PartyNativeName = "party_native_name";
    public const string InvoiceNo = "invoice_no";
    public const string IssueDate = "issue_date";
    public const string Total = "total";
    public const string AmountWords = "amount_words";
    public const string Bank = "bank";
    public const string Address = "address";
    public const string BuyerBlock = "buyer_block";
    public const string VatNo = "vat_no";
    public const string VatAmount = "vat_amount";

    /// <summary>The core variable cells every template should have (shown first in the review screen).</summary>
    public static IReadOnlyList<string> Core { get; } =
        [PartyName, PartyNativeName, IssueDate, InvoiceNo, Total, Bank, Address];

    public static IReadOnlyList<string> Optional { get; } = [AmountWords, BuyerBlock, VatNo, VatAmount];

    public static IReadOnlyList<string> All { get; } = [.. Core, .. Optional];
}

public sealed class FieldMapping
{
    /// <summary>A1 reference such as "F28" (null = not present in this template).</summary>
    public string? Cell { get; set; }
    public double Confidence { get; set; }
    public string Method { get; set; } = "";
    public string? Sample { get; set; }
    public string? Note { get; set; }
    /// <summary>True once the user confirmed or changed it in the review screen.</summary>
    public bool Confirmed { get; set; }
}

public sealed class ItemsTableMapping
{
    public int HeaderRow { get; set; }
    public int FirstRow { get; set; }
    public int LastRow { get; set; }
    /// <summary>code, description, qty, unit, price, amount -> column letter.</summary>
    public Dictionary<string, string> Columns { get; set; } = new();
    public double Confidence { get; set; }
    public int Capacity => LastRow >= FirstRow ? LastRow - FirstRow + 1 : 0;
}

/// <summary>Saved next to the template as &lt;template&gt;.map.json.</summary>
public sealed class TemplateMap
{
    public string TemplateFile { get; set; } = "";
    public string Sheet { get; set; } = "";
    public string FileHash { get; set; } = "";
    public DateTime DetectedUtc { get; set; }
    public Dictionary<string, FieldMapping> Fields { get; set; } = new();
    public ItemsTableMapping? Items { get; set; }
    public double? VatRate { get; set; }
    public List<string> Warnings { get; set; } = [];

    [JsonIgnore]
    public double CoreCoverage =>
        TemplateFields.Core.Count(f => Fields.TryGetValue(f, out var m) && m.Cell != null) / (double)TemplateFields.Core.Count;

    public static string MapPathFor(string templatePath) =>
        Path.Combine(Path.GetDirectoryName(templatePath)!, Path.GetFileNameWithoutExtension(templatePath) + ".map.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Save(string templatePath)
    {
        TemplateFile = Path.GetFileName(templatePath);
        File.WriteAllText(MapPathFor(templatePath), JsonSerializer.Serialize(this, Json));
    }

    public static TemplateMap? LoadFor(string templatePath)
    {
        var p = MapPathFor(templatePath);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<TemplateMap>(File.ReadAllText(p), Json); }
        catch { return null; }
    }

    public static string HashFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(fs));
    }
}
