using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BatchStudio.Core.Invoices;

namespace BatchStudio.Core.Analysis;

/// <summary>One document of the Analysis folder as read by the extraction engine (one JSON line of deepmath_analyze.py).</summary>
public sealed class AnalysisResult
{
    public string File { get; init; } = "";
    public string FileName { get; init; } = "";
    /// <summary>Invoice total read from the file name (5000, 70,000 ...); null when the name is not an amount.</summary>
    public decimal? Amount { get; init; }
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string CompanyName { get; init; } = "";
    public string LocalName { get; init; } = "";
    public string Address { get; init; } = "";
    /// <summary>The supplier's phone / e-mail read in the document ("Tel: ... · Email: ..."), "" when none.</summary>
    public string Contact { get; init; } = "";
    /// <summary>The banking paragraph (completed with the bank's name from the saved suppliers when the document's was not read).</summary>
    public string Bank { get; set; } = "";
    public string Activity { get; init; } = "";
    /// <summary>The trade category the document states ("Business Category: ..."), "" when it says none.</summary>
    public string BusinessCategory { get; init; } = "";
    /// <summary>The total written in the document ("Total Amount: USD 10,000.00"); null when none was read.</summary>
    public decimal? DocumentAmount { get; init; }
    /// <summary>The currency of that total (USD, EUR ...), "" when not written.</summary>
    public string DocumentCurrency { get; init; } = "";
    /// <summary>Account no., IBAN, SWIFT ... (letters and digits only, upper case) by kind.</summary>
    public Dictionary<string, List<string>> Ids { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double Seconds { get; init; }
    /// <summary>Remarks of the engine: name_from_heading, name_from_signature, account_unlabelled, account_differs, no_swift ...</summary>
    public List<string> Notes { get; init; } = [];
    /// <summary>The document's other active accounts (another currency ...), as banking paragraphs: saved as further accounts.</summary>
    public List<string> AltBanks { get; init; } = [];
    /// <summary>
    /// The document contradicts itself (two payment directives, the invoice issuer is not the account holder, a SWIFT given
    /// two ways, a third-party payee ...): never decided automatically - a person verifies it with the supplier.
    /// </summary>
    public List<AnalysisConflict> Conflicts { get; init; } = [];
    public bool HasConflict => Conflicts.Count > 0;

    /// <summary>
    /// A blocked document's accounts / SWIFTs as written in it (with currency, SWIFT, bank and where): UNVERIFIED - shown
    /// to the user, never put in the banking details and never chosen automatically.
    /// </summary>
    public List<BankCandidate> Candidates { get; init; } = [];

    /// <summary>What the document says about this payment only (references to quote ...): shown, never saved with the supplier.</summary>
    public List<string> TransactionNotes { get; init; } = [];

    /// <summary>The engine's own line for this document (kept for the text export: V16 changes and checks).</summary>
    public string RawJson { get; init; } = "";

    public static AnalysisResult? Parse(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var r = doc.RootElement;
            string S(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var ids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (r.TryGetProperty("identifiers", out var idn) && idn.ValueKind == JsonValueKind.Object)
                foreach (var p in idn.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array)
                        ids[p.Name] = p.Value.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            var notes = r.TryGetProperty("notes", out var nt) && nt.ValueKind == JsonValueKind.Array
                ? nt.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : [];
            decimal? amount = r.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : null;
            return new AnalysisResult
            {
                RawJson = jsonLine,
                File = S("file"),
                FileName = S("name"),
                Amount = amount,
                Ok = r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True,
                Error = S("error") is { Length: > 0 } e ? e : null,
                CompanyName = S("company_name").Trim(),
                LocalName = S("company_name_local").Trim(),
                Address = S("company_address").Trim(),
                Contact = S("contact").Trim(),
                Bank = S("bank_details").Trim(),
                Activity = S("activity").Trim(),
                BusinessCategory = S("business_category").Trim(),
                DocumentAmount = r.TryGetProperty("document_amount", out var da) && da.ValueKind == JsonValueKind.Number ? da.GetDecimal() : null,
                DocumentCurrency = S("document_currency").Trim(),
                Ids = ids,
                Notes = notes,
                AltBanks = r.TryGetProperty("bank_details_alt", out var ab) && ab.ValueKind == JsonValueKind.Array
                    ? ab.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim()).Where(x => x.Length > 0).ToList()
                    : [],
                Conflicts = r.TryGetProperty("conflicts", out var cf) && cf.ValueKind == JsonValueKind.Array
                    ? cf.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                        .Select(x => new AnalysisConflict(
                            x.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "",
                            x.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : ""))
                        .Where(x => x.Code.Length > 0).ToList()
                    : [],
                Candidates = r.TryGetProperty("bank_candidates", out var bc) && bc.ValueKind == JsonValueKind.Array
                    ? bc.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object).Select(x =>
                    {
                        string P(string k) => x.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "").Trim() : "";
                        return new BankCandidate(P("account"), P("currency"), P("swift"), P("bank"), P("where"));
                    }).Where(c => c.Account.Length > 0 || c.Swift.Length > 0).ToList()
                    : [],
                TransactionNotes = r.TryGetProperty("transaction_instructions", out var ti) && ti.ValueKind == JsonValueKind.Array
                    ? ti.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => (x.GetString() ?? "").Trim()).Where(x => x.Length > 0).ToList()
                    : [],
                Seconds = r.TryGetProperty("seconds", out var sec) && sec.ValueKind == JsonValueKind.Number ? sec.GetDouble() : 0,
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// The document after a person resolved its conflict (checked with the supplier): the name and bank accounts they
    /// entered, no conflict left, marked verified_manually. The first account is the one paid; the others are further accounts.
    /// </summary>
    public AnalysisResult Verified(string file, string name, IReadOnlyList<string> banks)
    {
        var texts = banks.Select(b => (b ?? "").Trim()).Where(b => b.Length > 0).ToList();
        var first = texts.FirstOrDefault() ?? "";
        return new AnalysisResult
        {
            File = file,
            FileName = FileName,
            Amount = Amount,
            Ok = true,
            CompanyName = (name ?? "").Trim(),
            LocalName = LocalName,
            Address = Address,
            Contact = Contact,
            Bank = first,
            Activity = Activity,
            BusinessCategory = BusinessCategory,
            DocumentAmount = DocumentAmount,
            DocumentCurrency = DocumentCurrency,
            Ids = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["account"] = AnalysisMatcher.AccountIdsIn(first).ToList() },
            Seconds = Seconds,
            Notes = Notes.Where(n => n == "test_document").Append("verified_manually").ToList(),
            AltBanks = texts.Skip(1).ToList(),
            TransactionNotes = TransactionNotes,
            RawJson = RawJson,
        };
    }

    /// <summary>Account numbers and IBANs: what identifies the account money is paid to.</summary>
    public IEnumerable<string> AccountIds =>
        Ids.Where(kv => kv.Key is "account" or "iban").SelectMany(kv => kv.Value).Where(v => v.Length >= 6);
}

/// <summary>An account / SWIFT a blocked document names (not verified): "HK-EUR-5018-7742 · EUR · VTPBHKHHXXX · bank · where".</summary>
public sealed record BankCandidate(string Account, string Currency, string Swift, string Bank, string Where)
{
    public override string ToString() =>
        string.Join(" · ", new[] { Account, Currency, Swift, Bank }.Where(v => v.Length > 0)) + (Where.Length > 0 ? " - " + Where : "");
}

/// <summary>One contradiction found in a document: its kind (conflicting_instructions ...) and what contradicts (accounts, names).</summary>
public sealed record AnalysisConflict(string Code, string Detail);

public enum AnalysisStatus
{
    /// <summary>Known supplier, the account in the document is one of its accounts.</summary>
    Matched,
    /// <summary>Known supplier, the document has an account it does not have yet (Approve all adds it as Banking Details N; the default account stays).</summary>
    NewAccount,
    /// <summary>The supplier is not in the Suppliers list: it is added.</summary>
    NewSupplier,
    /// <summary>Something to look at: no amount in the file name, nothing read, two possible suppliers ...</summary>
    Attention,
}

/// <summary>What Analysis proposes for one document (shown for review; nothing is saved before "Approve all").</summary>
public sealed class AnalysisMatch
{
    public required AnalysisResult Result { get; init; }
    public AnalysisStatus Status { get; set; }
    /// <summary>The known supplier (Matched / NewAccount).</summary>
    public Party? Supplier { get; set; }
    /// <summary>Account of the supplier used for this invoice (Matched); null = the default account.</summary>
    public int? BankNumber { get; set; }
    /// <summary>How the supplier was recognised: "account", "name", "similar".</summary>
    public string MatchedBy { get; set; } = "";
    /// <summary>Why a document needs attention / a remark (key of a program text).</summary>
    public string Note { get; set; } = "";
    /// <summary>Other suppliers that fit as well (ambiguous name).</summary>
    public List<Party> Candidates { get; } = [];
    /// <summary>
    /// A new supplier already proposed by an earlier document of the same folder (two invoices of one new supplier):
    /// that document adds the supplier, this one uses it (its account, or a second account when different).
    /// </summary>
    public AnalysisMatch? Owner { get; set; }
    /// <summary>For a document with <see cref="Owner"/>: its account differs from the owner's (a second account).</summary>
    public bool OtherAccount { get; set; }
    /// <summary>
    /// Known by its account, and the SWIFT read is the one saved with that account (O and 0 alike): the SWIFT doubt of an
    /// image is settled by the supplier's verified details.
    /// </summary>
    public bool SwiftConfirmed { get; set; }
}

/// <summary>
/// Recognises the supplier of each analysed document among the suppliers already saved: first by its account
/// number / IBAN (the strongest proof - a name read by OCR can differ by a letter, "CO., LTD" / "COMPANY LIMITED"),
/// then by its name (the same after cleaning, or very close).
/// </summary>
public static partial class AnalysisMatcher
{
    /// <summary>Two names count as the same company from this similarity on (0..1).</summary>
    public const double SameNameScore = 0.90;

    public static List<AnalysisMatch> Match(IReadOnlyList<AnalysisResult> results, IReadOnlyList<Party> suppliers)
    {
        // every account number / IBAN already saved, with its supplier and account number
        var known = new List<(string Id, Party Party, int Bank)>();
        foreach (var p in suppliers)
            foreach (var b in p.Banks.Where(b => b.Text.Trim().Length > 0))
                foreach (var id in AccountIdsIn(b.Text))
                    known.Add((id, p, b.Number));
        var names = suppliers.Select(p => (Party: p, Key: NormalizeName(p.Name), Local: NormalizeLocal(p.NativeName))).ToList();

        var list = new List<AnalysisMatch>();
        var pending = new List<AnalysisMatch>();   // new suppliers proposed so far in this folder
        foreach (var r in results)
        {
            var m = new AnalysisMatch { Result = r };
            list.Add(m);
            if (!r.Ok || (r.CompanyName.Length == 0 && r.Bank.Length == 0))
            {
                m.Status = AnalysisStatus.Attention;
                m.Note = "Analysis.NoteNothingRead";
                continue;
            }

            // 1. the account number / IBAN
            var byAccount = r.AccountIds.SelectMany(id => known.Where(k => SameAccount(id, k.Id))).ToList();
            var accountParties = byAccount.Select(k => k.Party).Distinct().ToList();
            if (accountParties.Count > 1)
            {
                // the same account on two suppliers (copied by mistake ...): the name in the document decides
                var docKey = NormalizeName(r.CompanyName);
                var byName = accountParties
                    .Select(p => (Party: p, Score: docKey.Length > 0 ? Similarity(docKey, NormalizeName(p.Name)) : 0))
                    .Where(x => x.Score >= SameNameScore).OrderByDescending(x => x.Score).ToList();
                if (byName.Count == 1 || (byName.Count > 1 && byName[1].Score < byName[0].Score - 0.02))
                {
                    byAccount = byAccount.Where(k => k.Party == byName[0].Party).ToList();
                    accountParties = [byName[0].Party];
                }
            }
            if (accountParties.Count == 1)
            {
                m.Supplier = accountParties[0];
                m.BankNumber = byAccount.First(k => k.Party == m.Supplier).Bank;
                m.MatchedBy = "account";
                m.Status = AnalysisStatus.Matched;
            }
            else if (accountParties.Count > 1)
            {
                m.Candidates.AddRange(accountParties);
                m.Status = AnalysisStatus.Attention;
                m.Note = "Analysis.NoteTwoSuppliers";
            }
            else
            {
                // 2. the name (or the Chinese / native name)
                var key = NormalizeName(r.CompanyName);
                var local = NormalizeLocal(r.LocalName);
                var scored = names
                    .Select(n => (n.Party, Score: Math.Max(key.Length > 0 && n.Key.Length > 0 ? Similarity(key, n.Key) : 0,
                        local.Length > 1 && n.Local.Length > 1 && local == n.Local ? 1.0 : 0)))
                    .Where(x => x.Score >= SameNameScore)
                    .OrderByDescending(x => x.Score)
                    .ToList();
                if (scored.Count > 1 && scored[1].Score >= scored[0].Score - 0.02)
                {
                    m.Candidates.AddRange(scored.Select(x => x.Party));
                    m.Status = AnalysisStatus.Attention;
                    m.Note = "Analysis.NoteTwoSuppliers";
                }
                else if (scored.Count >= 1)
                {
                    m.Supplier = scored[0].Party;
                    m.MatchedBy = scored[0].Score >= 0.999 ? "name" : "similar";
                    if (r.Bank.Length > 0 && r.AccountIds.Any())
                    {
                        m.Status = AnalysisStatus.NewAccount;
                        // the number read is part of one of its saved accounts (same SWIFT): a misread, not a new account
                        if (NearSavedAccount(m.Supplier, r) is int near)
                        {
                            m.Status = AnalysisStatus.Matched;
                            m.BankNumber = near;
                            m.MatchedBy = "near";
                            m.Note = "Analysis.NoteAccountMisread";
                            if (!r.Notes.Contains("account_misread")) r.Notes.Add("account_misread");
                        }
                    }
                    else
                    {
                        // no account in the document: the supplier's default account
                        m.Status = AnalysisStatus.Matched;
                        m.Note = "Analysis.NoteNoBank";
                    }
                }
                else if (r.CompanyName.Length > 0 && r.Bank.Length > 0)
                {
                    m.Status = AnalysisStatus.NewSupplier;
                    // the same new supplier as an earlier document of the folder (same account, or same name)
                    var owner = pending.FirstOrDefault(o => r.AccountIds.Any(a => o.Result.AccountIds.Any(b => SameAccount(a, b))))
                                ?? pending.FirstOrDefault(o => Similarity(key, NormalizeName(o.Result.CompanyName)) >= SameNameScore);
                    if (owner != null)
                    {
                        m.Owner = owner;
                        m.OtherAccount = r.AccountIds.Any() && !r.AccountIds.Any(a => owner.Result.AccountIds.Any(b => SameAccount(a, b)));
                    }
                    else pending.Add(m);
                }
                else
                {
                    m.Status = AnalysisStatus.Attention;
                    m.Note = r.CompanyName.Length == 0 ? "Analysis.NoteNoName" : "Analysis.NoteNoBankNew";
                }
            }

            // 3. what the saved suppliers already prove: the SWIFT of the known account, the bank's name by its SWIFT
            if (m.Status == AnalysisStatus.Matched && m.MatchedBy == "account" && m.Supplier != null
                && m.Supplier.Banks.FirstOrDefault(b => b.Number == m.BankNumber) is { } saved
                && SwiftIn(saved.Text) is { Length: > 0 } savedSwift && SwiftIn(r.Bank) is { Length: > 0 } readSwift
                && SameSwift(savedSwift, readSwift))
                m.SwiftConfirmed = true;
            if (r.Bank.Length > 0 && r.AccountIds.Any() && !BankNameLine().IsMatch(r.Bank)
                && SwiftIn(r.Bank) is { Length: >= 6 } sw && BankNameBySwift(suppliers, sw) is { Length: > 0 } bankName)
            {
                var lines = r.Bank.Split('\n').ToList();
                lines.Insert(lines.Count > 0 && lines[0].StartsWith("Beneficiary Name:", StringComparison.OrdinalIgnoreCase) ? 1 : 0, "Beneficiary Bank: " + bankName);
                r.Bank = string.Join("\n", lines);
                if (!r.Notes.Contains("bank_name_from_saved")) r.Notes.Add("bank_name_from_saved");
            }

            // no amount in the file name: the supplier is still recognised; the amount is typed in the review
            if (m.Status != AnalysisStatus.Attention && r.Amount is null or <= 0 && m.Note.Length == 0)
                m.Note = "Analysis.NoteNoAmount";
        }
        return list;
    }

    // ------------------------------------------------------------------ SWIFT and bank names
    [GeneratedRegex(@"(?im)^\s*(?:beneficiary\s*(?:'s\s*)?bank(?:\s*name)?|bank\s*name|name\s+of\s+(?:the\s+)?bank|bank)\s*[:：]")]
    private static partial Regex BankNameLine();

    [GeneratedRegex(@"(?im)^\s*(?:beneficiary\s*(?:'s\s*)?bank(?:\s*name)?|bank\s*name|name\s+of\s+(?:the\s+)?bank|bank)\s*[:：]\s*(.+)$")]
    private static partial Regex BankNameValue();

    [GeneratedRegex(@"(?im)^\s*(?!intermediary)[^\n]{0,30}?\b(?:swift|bic)\b[^:：\n]{0,20}[:：]?\s*([A-Z0-9]{4}\s?[A-Z]{2}\s?[A-Z0-9]{2}(?:\s?[A-Z0-9]{3})?)\b")]
    private static partial Regex SwiftLine();

    /// <summary>The beneficiary bank's SWIFT in a banking paragraph (not an intermediary's), upper case, no spaces.</summary>
    public static string SwiftIn(string bankText)
    {
        foreach (var line in (bankText ?? "").Split('\n'))
        {
            if (Regex.IsMatch(line, @"(?i)intermediary|correspondent")) continue;
            var m = SwiftLine().Match(line);
            if (m.Success) return m.Groups[1].Value.Replace(" ", "").ToUpperInvariant();
        }
        return "";
    }

    /// <summary>The same SWIFT: 8 letters = 11 with XXX; in the location / branch part (7-11) O and 0 count as one.</summary>
    public static bool SameSwift(string a, string b)
    {
        static string N(string s)
        {
            s = Clean(s);
            if (s.Length == 8) s += "XXX";
            return s.Length == 11 ? s[..6] + s[6..].Replace('O', '0') : s;
        }
        return N(a).Length == 11 && N(a) == N(b);
    }

    /// <summary>
    /// The bank's name for a SWIFT (its bank + country part, 6 letters) from the saved suppliers' banking paragraphs;
    /// "" when no saved paragraph with that SWIFT names its bank.
    /// </summary>
    public static string BankNameBySwift(IReadOnlyList<Party> suppliers, string swift)
    {
        var key = Clean(swift);
        if (key.Length < 6) return "";
        key = key[..6];
        foreach (var p in suppliers)
            foreach (var b in p.Banks)
            {
                var saved = SwiftIn(b.Text);
                if (saved.Length < 6 || saved[..6] != key) continue;
                var name = BankNameValue().Match(b.Text);
                if (name.Success && name.Groups[1].Value.Trim() is { Length: >= 3 } v) return v;
            }
        return "";
    }

    // ------------------------------------------------------------------ accounts
    [GeneratedRegex(@"(?im)^\s*(?:beneficiary\s+)?(?:account\s*(?:number|no\.?|#|/\s*iban)|iban\s*/\s*account(?:\s*(?:number|no\.?))?|a/c\s*(?:no\.?|number)?|acct\.?\s*no\.?|iban|账号|账户号码|银行账号|收款账号)(?:\s*\([^)]{0,20}\))?\s*[:：#\-]?\s*(.+)$")]
    private static partial Regex LabeledAccount();

    [GeneratedRegex(@"(?<![0-9A-Za-z])(?:[A-Z]{2}\d{2}[ \-]?(?:[0-9A-Z]{4}[ \-]?){2,7}[0-9A-Z]{1,4}|[A-Z]{2,6}[ \-]?\d[\d \-]{6,40}\d|\d(?:[\d \-]{6,40})\d)(?![0-9A-Za-z])")]
    private static partial Regex LongNumber();

    /// <summary>Account numbers / IBANs written in a saved banking paragraph (letters and digits only).</summary>
    public static HashSet<string> AccountIdsIn(string bankText)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in (bankText ?? "").Split('\n'))
        {
            if (Regex.IsMatch(line, @"(?i)^\s*(?:intermediary|swift|bic|routing|aba|ifsc|cnaps|sort\s*code|bank\s*code|branch|phone|tel|fax|mobile)"))
                continue;
            var lm = LabeledAccount().Match(line);
            if (lm.Success)
            {
                // "NRA2010... (USD)" / "(USD) NRA2010...": a note in brackets is not part of the number
                var v = Clean(Regex.Replace(lm.Groups[1].Value, @"\([^)]*\)", " "));
                if (v.Length >= 6) ids.Add(v);
                continue;
            }
            // unlabelled long numbers (an account written alone on its line ...)
            foreach (Match n in LongNumber().Matches(line))
            {
                var v = Clean(n.Value);
                if (v.Count(char.IsDigit) >= 8) ids.Add(v);
            }
        }
        return ids;
    }

    /// <summary>
    /// The saved account of a supplier that the account read is part of, or holds (at least 6 letters / digits, not equal):
    /// "JSD100152" read for the saved "083JSD100152". When both name a SWIFT it must be the same. null = none.
    /// </summary>
    public static int? NearSavedAccount(Party supplier, AnalysisResult r)
    {
        var readSwift = SwiftIn(r.Bank);
        foreach (var b in supplier.Banks.Where(b => b.Text.Trim().Length > 0))
        {
            var savedSwift = SwiftIn(b.Text);
            if (readSwift.Length > 0 && savedSwift.Length > 0 && !SameSwift(readSwift, savedSwift)) continue;
            foreach (var k in AccountIdsIn(b.Text).Select(Clean))
                foreach (var a in r.AccountIds.Select(Clean))
                {
                    if (a == k || Math.Min(a.Length, k.Length) < 6) continue;
                    if (k.Contains(a, StringComparison.Ordinal) || a.Contains(k, StringComparison.Ordinal)) return b.Number;
                }
        }
        return null;
    }

    public static string Clean(string s) => new string((s ?? "").Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();

    /// <summary>The same account: equal, or one inside the other (an IBAN holds the account number) when long enough.</summary>
    public static bool SameAccount(string a, string b)
    {
        a = Clean(a);
        b = Clean(b);
        if (a.Length < 6 || b.Length < 6) return false;
        if (a == b) return true;
        var (shortId, longId) = a.Length < b.Length ? (a, b) : (b, a);
        return shortId.Length >= 8 && shortId.All(char.IsDigit) && longId.EndsWith(shortId, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ names
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CO"] = "COMPANY", ["COMP"] = "COMPANY", ["CORP"] = "CORPORATION", ["LTD"] = "LIMITED", ["LIMTED"] = "LIMITED",
        ["INTL"] = "INTERNATIONAL", ["INT"] = "INTERNATIONAL", ["MFG"] = "MANUFACTURING", ["IMP"] = "IMPORT", ["EXP"] = "EXPORT",
        ["TRADG"] = "TRADING", ["IND"] = "INDUSTRIAL", ["TECH"] = "TECHNOLOGY", ["ELEC"] = "ELECTRONICS", ["PVT"] = "PRIVATE",
        ["LLC"] = "LLC", ["INC"] = "INCORPORATED", ["HK"] = "HONGKONG", ["&"] = "AND",
    };

    /// <summary>"Guangzhou Xenlong Trading Co.,Ltd" → "GUANGZHOU XENLONG TRADING COMPANY LIMITED".</summary>
    public static string NormalizeName(string? name)
    {
        // a label left in front of the name ("Customer: NINGBO ...", "Beneficiary Name: ...")
        var n = System.Text.RegularExpressions.Regex.Replace(name ?? "",
            @"^\s*(?:beneficiary\s+)?(?:customer|name|company|beneficiary|account\s*name|payee)\s*[:：]\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // a SWIFT field code read in front of the name ("59A) ...", OCR "590A)", "S594)", "(收款人 59A)")
        n = System.Text.RegularExpressions.Regex.Replace(n,
            @"^\s*(?:[\u3400-\u9fff\s]{1,20}?\s*)?(?:\(\s*[^\s()]{0,8}\s+)?[S5]?(?:5[0-9]|7[0-2])[0O]?[A-D4]?\s*\)\s*", "");
        var s = n.ToUpperInvariant().Replace("&", " AND ").Replace("HONG KONG", "HONGKONG");
        var sb = new StringBuilder();
        foreach (var ch in s) sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => Words.TryGetValue(w, out var full) ? full : w)
            .Where(w => w != "THE")
            .ToList();
        return string.Join(' ', words);
    }

    private static string NormalizeLocal(string? name) =>
        new string((name ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && !char.IsSymbol(c)).ToArray());

    /// <summary>0..1: the best of the character similarity and the word overlap of two cleaned names.</summary>
    public static double Similarity(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 1;
        var wa = a.Split(' ').ToHashSet();
        var wb = b.Split(' ').ToHashSet();
        // numbers in the names must agree: "SUPPLIER 001 TRADING" is not "SUPPLIER 002 TRADING"
        var na = wa.Where(w => w.Any(char.IsDigit)).ToHashSet();
        var nb = wb.Where(w => w.Any(char.IsDigit)).ToHashSet();
        if ((na.Count > 0 || nb.Count > 0) && !na.SetEquals(nb)) return 0;
        // the legal form and trade words ("IMPORT AND EXPORT COMPANY LIMITED") do not make two companies the same:
        // they are compared on their own words
        var ka = wa.Except(Generic).ToHashSet();
        var kb = wb.Except(Generic).ToHashSet();
        if (ka.Count == 0 || kb.Count == 0)
            return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
        var ca = string.Join(' ', wa.Where(ka.Contains));
        var cb = string.Join(' ', wb.Where(kb.Contains));
        double chars = 1.0 - (double)Levenshtein(ca, cb) / Math.Max(ca.Length, cb.Length);
        double words = (double)ka.Intersect(kb).Count() / Math.Max(ka.Count, kb.Count);
        // the distinctive words all equal and the rest only differs by the legal form
        double sameCore = ka.SetEquals(kb) ? 0.97 : 0;
        return Math.Max(chars, Math.Max(sameCore, words * 0.95));
    }

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "COMPANY", "LIMITED", "CORPORATION", "AND", "INCORPORATED", "LLC", "LLP", "PLC", "TRADING", "TRADE", "INTERNATIONAL",
        "GROUP", "IMPORT", "EXPORT", "IMP", "EXP", "INDUSTRIAL", "INDUSTRY", "HONGKONG", "HK", "CHINA", "OF",
    };

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>"5000", "70,000", "12500.50", "USD 5000", "5000 (2)" → the amount; null when the name is not an amount.</summary>
    public static decimal? AmountFromName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName ?? "");
        var m = Regex.Match(stem, @"^\s*(?:(?:USD|US\$|SAR|AED|EUR|GBP|CNY|RMB|EGP|INR|HKD|\$|€|£|¥)\s*)?(\d{1,3}(?:[,\s]\d{3})+|\d+)(?:\.(\d{1,2}))?(?:\s*(?:USD|US\$|SAR|AED|EUR|GBP|CNY|RMB|EGP|INR|HKD|\$|€|£|¥))?\s*(?:[\(\[\-_ ]\s*\d+\s*[\)\]]?)?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var text = Regex.Replace(m.Groups[1].Value, @"[,\s]", "") + (m.Groups[2].Success ? "." + m.Groups[2].Value : "");
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
    }
}
