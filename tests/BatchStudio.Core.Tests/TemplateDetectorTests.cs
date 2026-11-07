using BatchStudio.Core.Templates;

namespace BatchStudio.Core.Tests;

/// <summary>Cell layouts copied from the BATCH STUDIO V28 templates (INVOICE, INV1, Madar).</summary>
public class TemplateDetectorTests
{
    private static SheetModel InvoiceLike(bool withNames)
    {
        var s = new SheetModel { Name = withNames ? "INVOICE" : "INV1", Row1 = 1, Col1 = 2, Row2 = 35, Col2 = 6 };
        s.Add(3, 2, "ARVIND ENTERPRISES", fontSize: 20);
        s.Add(3, 5, "INVOICE", fontSize: 52);
        s.Add(4, 2, "अरविंद एंटरप्राइजेज", fontSize: 14);
        s.Add(5, 2, "BUYER / CONSIGNEE\nDemo Trading Company Ltd.\nJeddah");
        s.Add(5, 5, "INVOICE NO: ", fontSize: 12);
        s.Add(5, 6, "Mad-5212411", fontSize: 12);
        s.Add(7, 5, "ISSUE DATE: ", fontSize: 12);
        s.Add(7, 6, "2026-11-07", fontSize: 16);
        s.Add(11, 2, "Code"); s.Add(11, 3, "DESCRIPTION"); s.Add(11, 4, "QTY"); s.Add(11, 5, "Price"); s.Add(11, 6, "TOTAL AMOUNT");
        for (int r = 13; r <= 26; r++) s.Add(r, 6, $"=D{r}*E{r}", formula: $"=D{r}*E{r}");
        s.Add(28, 2, "(ثلاثون ألف دولار أمريكي فقط) Total Invoice Amount\n(Thirty Thousand US Dollars Only)");
        s.Add(28, 5, "TOTAL VALUE");
        s.Add(28, 6, "30000", isNumber: true);
        s.Add(31, 2, "PAYMENT & BANKING INSTRUCTIONS");
        s.Add(33, 2, "PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY BANK: TEST BANK\nBRANCH: TEST");
        s.Add(35, 2, "Company Address:\nTest street 1, Kolkata");
        s.Merged.Add(new MergedArea(5, 2, 7, 4));
        s.Merged.Add(new MergedArea(31, 2, 31, 6));
        s.Merged.Add(new MergedArea(33, 2, 33, 6));
        s.Merged.Add(new MergedArea(35, 2, 35, 6));
        if (withNames)
        {
            s.Names["InvoiceTarget"] = (28, 6);
            s.Names["SupplierBankTarget"] = (33, 2);
            s.Names["SupplierAddressTarget"] = (35, 2);
            s.Names["SupplierEnglishTarget"] = (3, 2);
            s.Names["SupplierChineseTarget"] = (4, 2);
        }
        return s;
    }

    [Fact]
    public void Invoice_with_defined_names()
    {
        var m = TemplateDetector.Detect(InvoiceLike(true));
        Assert.Equal("B3", m.Fields[TemplateFields.PartyName].Cell);
        Assert.Equal("B4", m.Fields[TemplateFields.PartyNativeName].Cell);
        Assert.Equal("F5", m.Fields[TemplateFields.InvoiceNo].Cell);
        Assert.Equal("F7", m.Fields[TemplateFields.IssueDate].Cell);
        Assert.Equal("F28", m.Fields[TemplateFields.Total].Cell);
        Assert.Equal("B33", m.Fields[TemplateFields.Bank].Cell);
        Assert.Equal("B35", m.Fields[TemplateFields.Address].Cell);
        Assert.Equal(13, m.Items!.FirstRow);
        Assert.Equal(26, m.Items.LastRow);
        Assert.Equal("F", m.Items.Columns["amount"]);
    }

    [Fact]
    public void Invoice_without_names_uses_labels_and_layout()
    {
        var m = TemplateDetector.Detect(InvoiceLike(false));
        Assert.Equal("B3", m.Fields[TemplateFields.PartyName].Cell);
        Assert.Equal("B4", m.Fields[TemplateFields.PartyNativeName].Cell);
        Assert.Equal("F28", m.Fields[TemplateFields.Total].Cell);
        Assert.Equal("B33", m.Fields[TemplateFields.Bank].Cell);   // content block, not the heading in B31
        Assert.Equal("B35", m.Fields[TemplateFields.Address].Cell);
        Assert.Equal("B28", m.Fields[TemplateFields.AmountWords].Cell);
    }

    [Fact]
    public void Inv1_words_formula_pointing_to_other_sheet_is_reported()
    {
        var s = InvoiceLike(false);
        s.Cells.Remove((28, 2));
        s.Add(28, 2, "=[1]!SAPAmountWords_USD(InvoiceTarget)", formula: "=[1]!SAPAmountWords_USD(InvoiceTarget)");
        s.ForeignNames["InvoiceTarget"] = "INVOICE";
        var m = TemplateDetector.Detect(s);
        Assert.Equal("B28", m.Fields[TemplateFields.AmountWords].Cell);
        Assert.Equal("F28", m.Fields[TemplateFields.Total].Cell);
        Assert.Contains(m.Warnings, w => w.Contains("points to sheet \"INVOICE\""));
        Assert.Contains(m.Warnings, w => w.Contains("external workbook"));
    }

    [Fact]
    public void Madar_arabic_labels_vat_and_words_argument()
    {
        var s = new SheetModel { Name = "Madar", Row1 = 1, Col1 = 2, Row2 = 43, Col2 = 8 };
        s.Add(8, 3, "Invoice NO: رقم الفــاتورة");
        s.Add(8, 4, "Mad-4218627");
        s.Add(8, 5, "VAT NO/ الرقم الضريبي :  300000000000003");
        s.Add(10, 2, "Invoice ", fontSize: 26);
        s.Add(10, 3, "    ISSUE DATE: ");
        s.Add(10, 5, "2026-11-01", fontSize: 14);
        s.Add(10, 8, "    التاريخ");
        s.Add(11, 3, "بتروليك للسمكرة والدهان | Petrolex Body & Paint");
        s.Add(11, 8, "    السيد/السادة");
        s.Add(14, 2, "Item Code"); s.Add(14, 3, "Descriptions"); s.Add(14, 4, "QTY"); s.Add(14, 5, "UNIT");
        s.Add(14, 6, "Unit Price "); s.Add(14, 7, "TOTAL AMOUNT");
        for (int r = 16; r <= 26; r++) s.Add(r, 7, $"=D{r}*F{r}", formula: $"=D{r}*F{r}");
        s.Add(28, 5, "Subtotal (Excluding VAT)");
        s.Add(28, 7, "=ROUND(SUM(G16:G26),2)", formula: "=ROUND(SUM(G16:G26),2)");
        s.Add(29, 7, "=SUMPRODUCT(ROUND(G16:G26*15%,2))", formula: "=SUMPRODUCT(ROUND(G16:G26*15%,2))");
        s.Add(30, 2, "=[2]!SAPAmountWords(G30)", formula: "=[2]!SAPAmountWords(G30)");
        s.Add(30, 7, "=SUM(G28,G29)", formula: "=SUM(G28,G29)");
        s.Merged.Add(new MergedArea(10, 5, 10, 6));
        s.Merged.Add(new MergedArea(11, 3, 11, 7)); // client name spans C11:G11

        var m = TemplateDetector.Detect(s);
        Assert.Equal("D8", m.Fields[TemplateFields.InvoiceNo].Cell);
        Assert.Equal("E10", m.Fields[TemplateFields.IssueDate].Cell);
        Assert.Equal("C11", m.Fields[TemplateFields.PartyName].Cell);
        Assert.Equal("G30", m.Fields[TemplateFields.Total].Cell);
        Assert.Equal("G29", m.Fields[TemplateFields.VatAmount].Cell);
        Assert.Equal(0.15, m.VatRate!.Value, 3);
        Assert.Null(m.Fields[TemplateFields.Bank].Cell);
        Assert.Equal(16, m.Items!.FirstRow);
        Assert.Equal("E", m.Items.Columns["unit"]);
    }

    [Fact]
    public void Madar_total_found_from_vat_sum_when_words_are_plain_text()
    {
        var s = new SheetModel { Name = "Madar", Row1 = 1, Col1 = 2, Row2 = 43, Col2 = 8 };
        s.Add(14, 2, "Item Code"); s.Add(14, 3, "Descriptions"); s.Add(14, 4, "QTY"); s.Add(14, 7, "TOTAL AMOUNT");
        for (int r = 16; r <= 26; r++) s.Add(r, 7, $"=D{r}*F{r}", formula: $"=D{r}*F{r}");
        s.Add(28, 7, "=ROUND(SUM(G16:G26),2)", formula: "=ROUND(SUM(G16:G26),2)");
        s.Add(29, 7, "=SUMPRODUCT(ROUND(G16:G26*15%,2))", formula: "=SUMPRODUCT(ROUND(G16:G26*15%,2))");
        s.Add(30, 2, "(مائتان وخمسة وخمسون ألف ريال سعودي فقط) Total Amount");
        s.Add(30, 7, "=SUM(G28,G29)", formula: "=SUM(G28,G29)");
        var m = TemplateDetector.Detect(s);
        Assert.Equal("G30", m.Fields[TemplateFields.Total].Cell);
        Assert.Equal("B30", m.Fields[TemplateFields.AmountWords].Cell);
    }

    [Theory]
    [InlineData("अरविंद एंटरप्राइजेज", "Hindi")]
    [InlineData("查司進出口有限公司", "Chinese")]
    [InlineData("ARVIND ENTERPRISES", null)]
    public void Script_detection(string text, string? expected) =>
        Assert.Equal(expected, TemplateDetector.Script(text));
}
