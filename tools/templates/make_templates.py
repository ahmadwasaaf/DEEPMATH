"""Builds Templates.xlsx: extra invoice templates for DEEPMATH (INV2, INV3 ...).

Every template follows the rules of the INVOICE / INV1 sheets so the program fills it the same way:
  * B1 is the theme marker: its fill colour is the template's theme colour; every fill / font in exactly that colour
    takes the supplier's theme colour (CLIENT THEME MEMORY).
  * sheet-level names point at the variable cells (SupplierEnglishTarget, SupplierChineseTarget, SupplierAddressTarget,
    SupplierBankTarget, InvoiceNumberTarget, InvoiceDateTarget, InvoiceTarget).
  * items table: header Code | DESCRIPTION | QTY | Price | TOTAL AMOUNT, sample lines with =QTY*Price, two alternating
    row colours (the program copies them when an invoice has more lines).
  * the bank and address cells are one-row merged cells (their row height follows the text).
  * the BUYER / CONSIGNEE block and the word INVOICE are fixed text.

usage: python make_templates.py <output.xlsx>
"""
import sys
from datetime import datetime

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.workbook.defined_name import DefinedName

FONT = "Alexandria"
INK = "1F2937"        # main text
SOFT = "6B7280"       # captions
LINE = "E5E7EB"       # hairlines
BUYER = ("BUYER / CONSIGNEE\nDemo Trading Company Ltd.\n"
         "City of the Establishment: Jeddah\nDemo District, Sample Street 1")
BUYER_BODY = BUYER.split("\n", 1)[1]
CATEGORY = "Office & Stationery Supplies | المستلزمات المكتبية والقرطاسية"
WORDS = "(مائة وعشرون ألف دولار أمريكي فقط) Total Invoice Amount\n(One Hundred Twenty Thousand US Dollars Only)"
ADDRESS = "Company Address:\nRoom 1203, 12/F, Sample Building, 88 Example Road, Kowloon, Hong Kong"
BANK = ("PAYMENT & BANKING INSTRUCTIONS\nBENEFICIARY NAME: SAMPLE TRADING CO., LIMITED\n"
        "BENEFICIARY BANK: SAMPLE COMMERCIAL BANK CO., LTD\nSWIFT BIC: SMPLHKHHXXX\n"
        "BANK ADDRESS: 1 SAMPLE ROAD, CENTRAL, HONG KONG\nACCOUNT NO.: 012-345678-901")
ITEMS = [
    ("OFF-424132", "1- Inkjet Ink Cartridge - Compact", 165, 126.21),
    ("OFF-320493", "2- Thermal Receipt Paper Roll - Economy", 700, 29.53),
    ("OFF-278822", "3- Printer Toner Cartridge - Compact", 85, 245.26),
    ("OFF-969877", "4- Whiteboard - Standard", 154, 98.78),
    ("OFF-845231", "5- Printer Toner Cartridge - Economy", 81, 136.69),
    ("OFF-418302", "6- Desk Organizer - Large", 188, 73.41),
    ("OFF-549894", "7- Office Document Folder - Bulk Pack", 712, 24.68),
]
MONEY = '#,##0.00" USD"'


def fill(c):
    return PatternFill("solid", fgColor=c)


def font(size=11, bold=False, color=INK, italic=False):
    return Font(name=FONT, size=size, bold=bold, color=color, italic=italic)


class Sheet:
    def __init__(self, wb, name, theme, widths):
        self.ws = wb.create_sheet(name)
        self.name = name
        self.theme = theme
        for col, w in widths.items():
            self.ws.column_dimensions[col].width = w
        ws = self.ws
        ws.sheet_view.showGridLines = False
        ws.sheet_view.zoomScale = 85
        ws.page_setup.paperSize = 9          # A4
        ws.page_setup.orientation = "portrait"
        ws.page_setup.fitToWidth = 1
        ws.page_setup.fitToHeight = 0
        ws.sheet_properties.pageSetUpPr.fitToPage = True
        ws.print_options.horizontalCentered = True
        ws.page_margins.left = ws.page_margins.right = 0.4
        ws.page_margins.top = ws.page_margins.bottom = 0.5

    def h(self, row, height):
        self.ws.row_dimensions[row].height = height

    def put(self, ref, value=None, f=None, al=None, bg=None, fmt=None, merge=None, border=None):
        ws = self.ws
        if merge:
            ws.merge_cells(merge)
        c = ws[ref]
        if value is not None:
            c.value = value
        if f:
            c.font = f
        c.alignment = al or Alignment(vertical="center")
        if fmt:
            c.number_format = fmt
        if bg or border:
            cells = ws[merge] if merge else [[c]]
            for row in cells:
                for x in row:
                    if bg:
                        x.fill = fill(bg)
                    if border:
                        x.border = border
        return c

    def bar(self, row, height, c1="B", c2="F", color=None):
        """A thin full-width rule in the theme colour (cell fills follow the theme, borders would not)."""
        self.h(row, height)
        self.put(f"{c1}{row}", merge=f"{c1}{row}:{c2}{row}", bg=color or self.theme)

    def define_name(self, defined, ref):
        col = "".join(ch for ch in ref if ch.isalpha())
        row = "".join(ch for ch in ref if ch.isdigit())
        self.ws.defined_names.add(DefinedName(defined, attr_text=f"'{self.name}'!${col}${row}"))

    def items(self, header_row, first, header_font, header_bg, zebra, line_color=LINE):
        ws = self.ws
        heads = [("B", "Code", "center"), ("C", "DESCRIPTION", "left"), ("D", "QTY", "center"),
                 ("E", "Price", "center"), ("F", "TOTAL AMOUNT", "center")]
        for col, text, h in heads:
            self.put(f"{col}{header_row}", text, header_font,
                     Alignment(horizontal=h, vertical="center", indent=1 if h == "left" else 0), bg=header_bg)
        hair = Border(bottom=Side(style="thin", color=line_color))
        for i, (code, desc, qty, price) in enumerate(ITEMS):
            r = first + i
            self.h(r, 30)
            bg = zebra[i % 2]
            self.put(f"B{r}", code, font(10.5), Alignment(horizontal="center", vertical="center"), bg=bg, border=hair)
            self.put(f"C{r}", desc, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), bg=bg, border=hair)
            self.put(f"D{r}", qty, font(10.5), Alignment(horizontal="center", vertical="center"), bg=bg, fmt="0", border=hair)
            self.put(f"E{r}", price, font(10.5), Alignment(horizontal="center", vertical="center"), bg=bg, fmt=MONEY, border=hair)
            self.put(f"F{r}", f"=D{r}*E{r}", font(10.5, True), Alignment(horizontal="center", vertical="center"), bg=bg, fmt=MONEY, border=hair)
        return first + len(ITEMS) - 1

    def print_area(self, last_row):
        self.ws.print_area = f"B1:F{last_row}"


# ---------------------------------------------------------------- INV2: "Band"
def inv2(wb):
    s = Sheet(wb, "INV2", "0E5E6F", {"A": 2.5, "B": 21, "C": 56, "D": 13, "E": 22, "F": 30, "G": 2.5})
    T = s.theme
    s.bar(1, 10)                                   # B1 = theme marker + top accent bar
    s.h(2, 14)
    s.h(3, 34); s.h(4, 34)
    s.put("B3", "INVOICE", font(40, True, T), Alignment(horizontal="left", vertical="center"), merge="B3:C4")
    s.put("D3", "INVOICE NO", font(9, True, SOFT), Alignment(horizontal="right", vertical="center"), merge="D3:E3")
    s.put("F3", "Mad-1234567", font(13, True, T), Alignment(horizontal="center", vertical="center"),
          border=Border(bottom=Side(style="thin", color=LINE)))
    s.put("D4", "ISSUE DATE", font(9, True, SOFT), Alignment(horizontal="right", vertical="center"), merge="D4:E4")
    s.put("F4", datetime(2026, 11, 11), font(12, False, INK), Alignment(horizontal="center", vertical="center"), fmt="dd/mm/yyyy")
    s.h(5, 10)
    s.bar(6, 3)
    s.h(7, 16)
    s.h(8, 20)
    s.put("B8", "SUPPLIER", font(9, True, T), merge="B8:C8")
    s.put("E8", "BUYER / CONSIGNEE", font(9, True, T), merge="E8:F8")
    s.h(9, 38)
    s.put("B9", "SAMPLE TRADING CO., LIMITED", font(17, True), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B9:C9")
    s.put("E9", BUYER_BODY, font(10.5), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="E9:F11", bg="F3F6F8")
    s.h(10, 24)
    s.put("B10", "示例贸易有限公司", font(13, False, SOFT), merge="B10:C10")
    s.h(11, 46)
    s.put("B11", ADDRESS, font(10, False, SOFT), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B11:C11")
    s.h(12, 18)
    s.h(13, 28)
    s.put("B13", CATEGORY, font(11, False, INK), Alignment(horizontal="left", vertical="center", indent=1), merge="B13:F13")
    s.h(14, 36)
    last = s.items(14, 15, font(11, True, "FFFFFF"), T, ("FFFFFF", "F3F6F8"))
    r = last + 1
    s.h(r, 12)
    r += 1
    s.h(r, 40); s.h(r + 1, 22)
    s.put(f"B{r}", WORDS, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:C{r + 1}")
    s.put(f"D{r}", "TOTAL VALUE", font(11, True, "FFFFFF"), Alignment(horizontal="center", vertical="center"), merge=f"D{r}:E{r}", bg=T)
    s.put(f"F{r}", f"=SUM(F15:F{last})", font(13, True, "FFFFFF"), Alignment(horizontal="center", vertical="center"), bg=T, fmt=MONEY)
    total = f"F{r}"
    r += 2
    s.h(r, 18)
    r += 1
    s.h(r, 28)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(12, True, T), Alignment(horizontal="left", vertical="center"), merge=f"B{r}:F{r}")
    r += 1
    s.bar(r, 3)
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:F{r}", bg="F8FAFB")
    bank = f"B{r}"
    r += 1
    s.h(r, 16)
    r += 1
    s.bar(r, 6)
    s.print_area(r)
    for n, ref in [("SupplierEnglishTarget", "B9"), ("SupplierChineseTarget", "B10"), ("SupplierAddressTarget", "B11"),
                   ("InvoiceNumberTarget", "F3"), ("InvoiceDateTarget", "F4"), ("InvoiceTarget", total),
                   ("SupplierBankTarget", bank)]:
        s.define_name(n, ref)


# ---------------------------------------------------------------- INV3: "Ledger"
def inv3(wb):
    s = Sheet(wb, "INV3", "6B2D5C", {"A": 2.5, "B": 21, "C": 56, "D": 13, "E": 22, "F": 30, "G": 2.5})
    T = s.theme
    box = Border(*(Side(style="thin", color="D1D5DB"),) * 4)
    s.bar(1, 6)                                    # B1 = theme marker
    s.h(2, 18)
    s.h(3, 72)
    s.put("B3", "INVOICE", font(46, True, T), Alignment(horizontal="center", vertical="center"), merge="B3:F3")
    s.h(4, 3)
    s.put("C4", bg=T)                              # short rule under the title (theme)
    s.h(5, 16)
    s.h(6, 20)
    s.put("B6", "INVOICE NO", font(9, True, SOFT), Alignment(horizontal="center", vertical="center"), merge="B6:C6")
    s.put("D6", "ISSUE DATE", font(9, True, SOFT), Alignment(horizontal="center", vertical="center"), merge="D6:E6")
    s.put("F6", "CURRENCY", font(9, True, SOFT), Alignment(horizontal="center", vertical="center"))
    s.h(7, 34)
    s.put("B7", "Mad-1234567", font(14, True, T), Alignment(horizontal="center", vertical="center"), merge="B7:C7", border=box)
    s.put("D7", datetime(2026, 11, 11), font(13, False, INK), Alignment(horizontal="center", vertical="center"), merge="D7:E7", border=box, fmt="dd/mm/yyyy")
    s.put("F7", "USD", font(13, False, INK), Alignment(horizontal="center", vertical="center"), border=box)
    s.h(8, 20)
    s.h(9, 24)
    s.put("B9", "SUPPLIER", font(10, True, "FFFFFF"), Alignment(horizontal="left", vertical="center", indent=1), merge="B9:C9", bg=T)
    s.put("E9", "BUYER / CONSIGNEE", font(10, True, "FFFFFF"), Alignment(horizontal="left", vertical="center", indent=1), merge="E9:F9", bg=T)
    s.h(10, 36)
    s.put("B10", "SAMPLE TRADING CO., LIMITED", font(16, True), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge="B10:C10")
    s.put("E10", BUYER_BODY, font(10.5), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="E10:F12", border=box)
    s.h(11, 24)
    s.put("B11", "示例贸易有限公司", font(12.5, False, SOFT), Alignment(horizontal="left", vertical="center", indent=1), merge="B11:C11")
    s.h(12, 46)
    s.put("B12", ADDRESS, font(10, False, SOFT), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge="B12:C12")
    s.h(13, 20)
    s.h(14, 28)
    s.put("B14", CATEGORY, font(11, False, T, italic=True), Alignment(horizontal="center", vertical="center"), merge="B14:F14")
    s.h(15, 34)
    last = s.items(15, 17, font(11, True, T), "F3F4F6", ("FFFFFF", "F9FAFB"))
    s.bar(16, 3)                                   # theme rule under the header (not an item row)
    r = last + 1
    s.bar(r, 3)
    r += 1
    s.h(r, 12)
    r += 1
    s.h(r, 40); s.h(r + 1, 22)
    s.put(f"B{r}", WORDS, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:C{r + 1}")
    s.put(f"D{r}", "TOTAL VALUE", font(11, True, T), Alignment(horizontal="right", vertical="center"), merge=f"D{r}:E{r}")
    s.put(f"F{r}", f"=SUM(F17:F{last})", font(13, True, "FFFFFF"), Alignment(horizontal="center", vertical="center"), bg=T, fmt=MONEY)
    total = f"F{r}"
    r += 2
    s.h(r, 20)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(11, True, "FFFFFF"), Alignment(horizontal="left", vertical="center", indent=1),
          merge=f"B{r}:F{r}", bg=T)
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:F{r}", border=box)
    bank = f"B{r}"
    r += 1
    s.h(r, 18)
    r += 1
    s.h(r, 18)
    s.put(f"B{r}", "Thank you for your business", font(9, False, SOFT, italic=True), Alignment(horizontal="center", vertical="center"), merge=f"B{r}:F{r}")
    r += 1
    s.bar(r, 6)
    s.print_area(r)
    for n, ref in [("SupplierEnglishTarget", "B10"), ("SupplierChineseTarget", "B11"), ("SupplierAddressTarget", "B12"),
                   ("InvoiceNumberTarget", "B7"), ("InvoiceDateTarget", "D7"), ("InvoiceTarget", total),
                   ("SupplierBankTarget", bank)]:
        s.define_name(n, ref)


def main(out):
    wb = Workbook()
    wb.remove(wb.active)
    inv2(wb)
    inv3(wb)
    wb.active = 0
    wb.save(out)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "Templates.xlsx")
