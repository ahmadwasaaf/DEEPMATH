using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BatchStudio.Core.Analysis;

/// <summary>
/// What the analysis read from one document as plain text: the same layout as the engine's v16_export.text_of
/// (to test the engine without adding a supplier).
/// </summary>
public static class AnalysisText
{
    private static readonly Dictionary<string, string> Notes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["test_document"] = "Sample / training document",
        ["name_from_heading"] = "Name taken from the document heading",
        ["name_from_signature"] = "Name taken from the signature",
        ["account_unlabelled"] = "Account number had no label",
        ["account_differs"] = "The two readers read different accounts",
        ["no_account"] = "No account number found",
        ["no_swift"] = "No SWIFT / bank code found",
        ["swift_check"] = "Check against the document before approving",
        ["swift_corrected"] = "SWIFT corrected from the document: check it",
        ["multiple_accounts"] = "Several accounts: check which one",
        ["future_account"] = "A future-dated account is kept as a further account (use it from its date)",
        ["swift_glyph_checked"] = "SWIFT look-alike characters (O / 0, S / 5) decided from the image",
        ["v16_extra_ocr"] = "V16 read a field with its extra OCR",
        ["v16_filled"] = "V16 filled a field V15 left empty or corrected one",
        ["iban_check_failed"] = "IBAN check digits are wrong",
        ["ifsc_check_failed"] = "IFSC format is wrong",
        ["cnaps_check_failed"] = "CNAPS is not 12 digits",
        ["swift_iban_country_differs"] = "SWIFT country differs from the IBAN country",
        ["bank_name_from_saved"] = "Bank name added from the saved suppliers (same SWIFT)",
        ["amount_from_name_text"] = "Amount taken from the text of the file name (the name is not only an amount): check it",
        ["intermediary_swift_known"] = "Intermediary SWIFT corrected to a well-known correspondent bank's code: check it",
        ["category_from_name"] = "Category taken from the company name (the document does not say what the supplier does)",
        ["account_misread"] = "The account read is part of a saved account of the supplier (same SWIFT): most likely misread; the saved account is used",
        ["bank_name_from_text"] = "Bank name read from its label in the text",
        ["currency_differs"] = "The document's currency is not the account's currency",
        ["conflicting_instructions"] = "CONFLICT: contradictory payment instructions",
        ["beneficiary_mismatch"] = "CONFLICT: invoice issuer is not the account holder",
        ["ambiguous_swift"] = "CONFLICT: SWIFT given two ways",
        ["third_party_payment"] = "CONFLICT: payment to a third party",
        ["verified_manually"] = "Banking details corrected by hand in DEEPMATH",
    };

    /// <param name="review">What DEEPMATH's Review shows for this document (status, account, remarks, the conflict in full);
    /// null outside the review.</param>
    public static string TextOf(AnalysisResult r, string version, DateTime now, IReadOnlyList<string>? review = null)
    {
        var l = new List<string>
        {
            "DEEPMATH analysis - " + r.FileName,
            "Engine: " + version,
            "Read on: " + now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            "Source file: " + r.File,
            "",
        };
        if (!r.Ok) l.AddRange(["RESULT: NOT READ - " + (r.Error ?? ""), ""]);
        string D(string v) => string.IsNullOrWhiteSpace(v) ? "-" : v;
        l.Add("Amount (from file name): " + (r.Amount is decimal a ? a.ToString("0.##", CultureInfo.InvariantCulture) : "-"));
        if (r.DocumentAmount is decimal da)
            l.Add("Total in the document: " + (r.DocumentCurrency.Length > 0 ? r.DocumentCurrency + " " : "") + da.ToString("#,##0.##", CultureInfo.InvariantCulture));
        l.Add("Supplier name: " + D(r.CompanyName));
        l.Add("Chinese / local name: " + D(r.LocalName));
        l.Add("Company address: " + D(r.Address));
        l.Add("Contact: " + D(r.Contact));
        l.Add("Business category: " + D(r.BusinessCategory));
        l.Add("Activity: " + D(r.Activity));
        l.Add("");
        l.Add("--- Banking details ---");
        l.Add(string.IsNullOrWhiteSpace(r.Bank) ? "(none)" : r.Bank);
        int n = 2;
        foreach (var alt in r.AltBanks) { l.Add(""); l.Add($"--- Other active account {n++} ---"); l.Add(alt); }
        if (r.Ids.Count > 0)
        {
            l.Add(""); l.Add("--- Identifiers (for matching) ---");
            foreach (var (k, v) in r.Ids) l.Add($"{k}: {string.Join(", ", v)}");
        }
        foreach (var c in r.Conflicts) { l.Add(""); l.Add($"CONFLICT {c.Code}: {c.Detail}"); }
        if (r.Candidates.Count > 0)
        {
            l.Add(""); l.Add("--- Unverified bank candidates (never chosen automatically) ---");
            l.AddRange(r.Candidates.Select(c => "- " + c));
        }
        if (r.TransactionNotes.Count > 0)
        {
            l.Add(""); l.Add("--- This payment only (not saved with the supplier) ---");
            l.AddRange(r.TransactionNotes.Select(t => "- " + t));
        }
        if (r.Notes.Count > 0)
        {
            l.Add(""); l.Add("--- Remarks ---");
            foreach (var x in r.Notes) l.Add("- " + (Notes.TryGetValue(x, out var t) ? t : x));
        }
        // what V16 changed and the result of its checks (only in the engine's own line)
        if (r.RawJson.Length > 0)
            try
            {
                using var doc = JsonDocument.Parse(r.RawJson);
                if (doc.RootElement.TryGetProperty("v16", out var v16) && v16.ValueKind == JsonValueKind.Object)
                {
                    var lines = new List<string>();
                    if (v16.TryGetProperty("changes", out var ch) && ch.ValueKind == JsonValueKind.Array)
                        foreach (var c in ch.EnumerateArray()) lines.Add("- " + (c.ValueKind == JsonValueKind.String ? c.GetString() : c.ToString()));
                    if (v16.TryGetProperty("checks", out var ck) && ck.ValueKind == JsonValueKind.Object)
                        foreach (var p in ck.EnumerateObject())
                            lines.Add($"- check {p.Name}: {(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.ToString())}");
                    if (lines.Count > 0) { l.Add(""); l.Add("--- What V16 did ---"); l.AddRange(lines); }
                }
            }
            catch (JsonException) { }
        if (review is { Count: > 0 })
        {
            l.Add(""); l.Add("--- What DEEPMATH shows in Review ---");
            l.AddRange(review);
        }
        l.Add("");
        l.Add("Time: " + r.Seconds.ToString("0.##", CultureInfo.InvariantCulture) + "s");
        // the engine's answer exactly as it came (every key, even the ones no line above shows)
        if (r.RawJson.Length > 0)
        {
            l.Add(""); l.Add("--- Engine output (as returned) ---");
            try
            {
                using var doc = JsonDocument.Parse(r.RawJson);
                l.Add(JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
            }
            catch (JsonException) { l.Add(r.RawJson); }
        }
        return string.Join("\r\n", l).Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n";
    }

    /// <summary>Writes "&lt;document name&gt;.txt" in <paramref name="folder"/> (" (2)" when taken); returns its path.</summary>
    public static string Export(AnalysisResult r, string version, string folder, IReadOnlyList<string>? review = null)
    {
        Directory.CreateDirectory(folder);
        var stem = Path.GetFileNameWithoutExtension(r.FileName.Length > 0 ? r.FileName : r.File);
        foreach (var c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        if (stem.Length == 0) stem = "document";
        var path = Path.Combine(folder, stem + ".txt");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{stem} ({i}).txt");
        File.WriteAllText(path, TextOf(r, version, DateTime.Now, review), new UTF8Encoding(true));
        return path;
    }
}
