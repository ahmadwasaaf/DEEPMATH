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

usage: python make_templates.py <output.xlsx> [existing Templates.xlsx to extend]
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


def font(size=11, bold=False, color=INK, italic=False, name=None):
    return Font(name=name or FONT, size=size, bold=bold, color=color, italic=italic)


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

    def band(self, rows, color=None, c1="A", c2="G"):
        """Edge-to-edge colour band (every cell filled, so text cells can be merged on top of it)."""
        from openpyxl.utils import column_index_from_string, get_column_letter
        for r in rows:
            for ci in range(column_index_from_string(c1), column_index_from_string(c2) + 1):
                self.ws[f"{get_column_letter(ci)}{r}"].fill = fill(color or self.theme)

    def items(self, header_row, first, header_font, header_bg, zebra, line_color=LINE, grid=None, desc_bold=False):
        ws = self.ws
        heads = [("B", "Code", "center"), ("C", "DESCRIPTION", "left"), ("D", "QTY", "center"),
                 ("E", "Price", "center"), ("F", "TOTAL AMOUNT", "center")]
        for col, text, h in heads:
            self.put(f"{col}{header_row}", text, header_font,
                     Alignment(horizontal=h, vertical="center", indent=1 if h == "left" else 0), bg=header_bg)
        hair = grid or Border(bottom=Side(style="thin", color=line_color))
        if grid:
            for col, _, _ in heads:
                ws[f"{col}{header_row}"].border = grid
        for i, (code, desc, qty, price) in enumerate(ITEMS):
            r = first + i
            self.h(r, 30)
            bg = zebra[i % 2]
            self.put(f"B{r}", code, font(10.5), Alignment(horizontal="center", vertical="center"), bg=bg, border=hair)
            self.put(f"C{r}", desc, font(10.5, desc_bold), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), bg=bg, border=hair)
            self.put(f"D{r}", qty, font(10.5), Alignment(horizontal="center", vertical="center"), bg=bg, fmt="0", border=hair)
            self.put(f"E{r}", price, font(10.5), Alignment(horizontal="center", vertical="center"), bg=bg, fmt=MONEY, border=hair)
            self.put(f"F{r}", f"=D{r}*E{r}", font(10.5, True), Alignment(horizontal="center", vertical="center"), bg=bg, fmt=MONEY, border=hair)
        return first + len(ITEMS) - 1

    def print_area(self, last_row, c1="B", c2="F"):
        self.ws.print_area = f"{c1}1:{c2}{last_row}"


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


WIDTHS = {"A": 3.5, "B": 21, "C": 56, "D": 13, "E": 22, "F": 30, "G": 3.5}
WHITE = "FFFFFF"
MUTED_ON_DARK = "D9DCE3"
CHARCOAL = "262626"


def names(s, mapping):
    for n, ref in mapping:
        s.define_name(n, ref)


# ---------------------------------------------------------------- INV4: "Bold Header"
def inv4(wb):
    """A tall theme-coloured header across the whole page: supplier, the word INVOICE and the total due in white."""
    s = Sheet(wb, "INV4", "3D348B", WIDTHS)
    T = s.theme
    s.h(1, 1.5)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker
    s.band(range(2, 13))
    s.h(2, 22)
    s.h(3, 32)
    s.put("B3", "SAMPLE TRADING CO., LIMITED", font(17, True, WHITE), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B3:C3")
    s.put("E3", "ISSUE DATE", font(9, True, MUTED_ON_DARK), Alignment(horizontal="left", vertical="bottom"))
    s.put("F3", datetime(2026, 11, 11), font(12, True, WHITE), Alignment(horizontal="left", vertical="bottom"), fmt="dd/mm/yyyy")
    s.h(4, 22)
    s.put("B4", "示例贸易有限公司", font(12, False, MUTED_ON_DARK), merge="B4:C4")
    s.h(5, 42)
    s.put("B5", ADDRESS, font(9.5, False, MUTED_ON_DARK), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B5:C5")
    s.put("E5", "BUYER / CONSIGNEE", font(9, True, MUTED_ON_DARK), Alignment(horizontal="left", vertical="bottom"), merge="E5:F5")
    s.h(6, 20)
    s.put("E6", BUYER_BODY, font(10, False, WHITE), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="E6:F8")
    s.h(7, 62)
    s.put("B7", "INVOICE", font(54, True, WHITE), Alignment(horizontal="left", vertical="center"), merge="B7:C7")
    s.h(8, 26)
    s.put("B8", "Mad-1234567", font(13, True, WHITE), Alignment(horizontal="left", vertical="center"), merge="B8:C8")
    s.h(9, 14)
    s.h(10, 18)
    s.put("E10", "TOTAL DUE", font(9, True, MUTED_ON_DARK), Alignment(horizontal="left", vertical="center"))
    s.h(11, 30)
    s.h(12, 22)
    s.h(13, 22)
    s.h(14, 28)
    s.put("B14", CATEGORY, font(11, True, T), Alignment(horizontal="left", vertical="center"), merge="B14:F14")
    s.h(15, 38)
    last = s.items(15, 16, font(11, True, WHITE), CHARCOAL, ("FFFFFF", "F1F1F4"))
    r = last + 1
    s.h(r, 14)
    r += 1
    s.h(r, 44); s.h(r + 1, 20)
    s.put(f"B{r}", WORDS, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:C{r + 1}")
    s.put(f"D{r}", "TOTAL VALUE", font(12, True, WHITE), Alignment(horizontal="center", vertical="center"), merge=f"D{r}:E{r}", bg=T)
    s.put(f"F{r}", f"=SUM(F16:F{last})", font(15, True, WHITE), Alignment(horizontal="center", vertical="center"), bg=T, fmt=MONEY)
    total = f"F{r}"
    # the header repeats the total (a formula: it follows the filled total)
    s.put("E11", f"={total}", font(20, True, WHITE), Alignment(horizontal="left", vertical="center"), merge="E11:F11", fmt=MONEY)
    r += 2
    s.h(r, 22)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(12, True, T), Alignment(horizontal="left", vertical="center"), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:F{r}")
    bank = f"B{r}"
    r += 1
    s.h(r, 20)
    r += 1
    s.band([r, r + 1])
    s.h(r, 30)
    s.put(f"B{r}", "THANK YOU FOR YOUR BUSINESS", font(10, True, WHITE), Alignment(horizontal="left", vertical="center"), merge=f"B{r}:F{r}")
    s.h(r + 1, 8)
    s.print_area(r + 1, "A", "G")
    names(s, [("SupplierEnglishTarget", "B3"), ("SupplierChineseTarget", "B4"), ("SupplierAddressTarget", "B5"),
              ("InvoiceNumberTarget", "B8"), ("InvoiceDateTarget", "F3"), ("InvoiceTarget", total), ("SupplierBankTarget", bank)])


# ---------------------------------------------------------------- INV5: "Corporate Grid"
def inv5(wb):
    """Corporate: theme rules top and bottom, a header of separate tiles, items in a grid of boxes, a bold total tile."""
    s = Sheet(wb, "INV5", "1565C0", WIDTHS)
    T = s.theme
    gap = Side(style="medium", color=WHITE)
    grid = Border(left=gap, right=gap, top=gap, bottom=gap)
    s.h(1, 12)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker + top rule
    s.put("A1", bg=T)
    s.put("G1", bg=T)
    s.h(2, 6)
    s.put("A2", bg=T)                             # a corner block
    s.put("B2", bg=T)
    s.h(3, 26)
    s.put("A3", bg=T)
    s.h(4, 30)
    s.put("B4", "FROM", font(9, True, SOFT), Alignment(horizontal="left", vertical="bottom"), merge="B4:C4")
    s.put("E4", "INVOICE", font(40, True, CHARCOAL), Alignment(horizontal="right", vertical="center"), merge="E4:F5")
    s.h(5, 34)
    s.put("B5", "SAMPLE TRADING CO., LIMITED", font(18, True, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B5:C5")
    s.h(6, 22)
    s.put("B6", "示例贸易有限公司", font(12, False, SOFT), merge="B6:C6")
    s.put("E6", "INVOICE NO", font(9.5, True, SOFT), Alignment(horizontal="right", vertical="center"))
    s.put("F6", "Mad-1234567", font(11.5, True, T), Alignment(horizontal="right", vertical="center"))
    s.h(7, 42)
    s.put("B7", ADDRESS, font(9.5, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B7:C7")
    s.put("E7", "ISSUE DATE", font(9.5, True, SOFT), Alignment(horizontal="right", vertical="top"))
    s.put("F7", datetime(2026, 11, 11), font(11.5, False, CHARCOAL), Alignment(horizontal="right", vertical="top"), fmt="dd/mm/yyyy")
    s.h(8, 14)
    s.h(9, 22)
    s.put("B9", "BUYER / CONSIGNEE", font(9, True, T), Alignment(horizontal="left", vertical="center"), merge="B9:C9")
    s.h(10, 50)
    s.put("B10", BUYER_BODY, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B10:C10")
    s.h(11, 14)
    s.h(12, 26)
    s.put("B12", CATEGORY, font(10.5, False, SOFT), Alignment(horizontal="left", vertical="center"), merge="B12:F12")
    s.h(13, 40)
    last = s.items(13, 14, font(11, True, WHITE), T, ("FFFFFF", "F2F5F9"), grid=grid, desc_bold=True)
    r = last + 1
    s.h(r, 16)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "Thank You For Your Business", font(14, True, CHARCOAL), Alignment(horizontal="left", vertical="center"), merge=f"B{r}:C{r}")
    s.h(r + 1, 44)
    s.put(f"B{r + 1}", WORDS, font(10, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge=f"B{r + 1}:C{r + 1}")
    s.put(f"D{r}", "TOTAL VALUE", font(12, True, WHITE), Alignment(horizontal="center", vertical="center"), merge=f"D{r}:E{r + 1}", bg=T)
    s.put(f"F{r}", f"=SUM(F14:F{last})", font(15, True, WHITE), Alignment(horizontal="center", vertical="center"), merge=f"F{r}:F{r + 1}", bg=T, fmt=MONEY)
    total = f"F{r}"
    r += 2
    s.h(r, 22)
    r += 1
    s.h(r, 24)
    s.put(f"B{r}", "Payment Method:", font(11, True, CHARCOAL), Alignment(horizontal="left", vertical="center"), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:F{r}")
    bank = f"B{r}"
    r += 1
    s.h(r, 26)
    s.put(f"G{r}", bg=T)                          # the opposite corner block
    r += 1
    s.h(r, 6)
    s.put(f"F{r}", bg=T)
    s.put(f"G{r}", bg=T)
    r += 1
    s.h(r, 12)
    s.band([r])
    s.print_area(r, "A", "G")
    names(s, [("SupplierEnglishTarget", "B5"), ("SupplierChineseTarget", "B6"), ("SupplierAddressTarget", "B7"),
              ("InvoiceNumberTarget", "F6"), ("InvoiceDateTarget", "F7"), ("InvoiceTarget", total), ("SupplierBankTarget", bank)])


# ---------------------------------------------------------------- INV6: "Dark Band"
def inv6(wb):
    """Statement look: white top, a charcoal band with the buyer and a bright theme tile holding the grand total."""
    s = Sheet(wb, "INV6", "E85D04", WIDTHS)
    T = s.theme
    s.h(1, 1.5)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker
    s.h(2, 22)
    s.h(3, 34)
    s.put("B3", "SAMPLE TRADING CO., LIMITED", font(17, True, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B3:C3")
    s.put("E3", "INVOICE", font(42, False, "A3A3A3"), Alignment(horizontal="right", vertical="center"), merge="E3:F4")
    s.h(4, 22)
    s.put("B4", "示例贸易有限公司", font(12, False, SOFT), merge="B4:C4")
    s.h(5, 42)
    s.put("B5", ADDRESS, font(9.5, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B5:C5")
    s.h(6, 12)
    s.band(range(7, 12), CHARCOAL)
    s.h(7, 22)
    s.put("B7", "BUYER / CONSIGNEE", font(9, True, T), Alignment(horizontal="left", vertical="bottom"), merge="B7:C7")
    s.put("E7", "GRAND TOTAL", font(9, True, WHITE), Alignment(horizontal="center", vertical="center"), merge="E7:F7", bg=T)
    s.h(8, 50)
    s.put("B8", BUYER_BODY, font(10, False, WHITE), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B8:C8")
    s.h(9, 10)
    s.h(10, 18)
    s.put("B10", "INVOICE NO", font(9, True, MUTED_ON_DARK), Alignment(horizontal="left", vertical="bottom"))
    s.put("C10", "ISSUE DATE", font(9, True, MUTED_ON_DARK), Alignment(horizontal="left", vertical="bottom"))
    s.h(11, 30)
    s.put("B11", "Mad-1234567", font(12, True, WHITE), Alignment(horizontal="left", vertical="center"))
    s.put("C11", datetime(2026, 11, 11), font(12, True, WHITE), Alignment(horizontal="left", vertical="center"), fmt="dd/mm/yyyy")
    s.h(12, 14)
    s.h(13, 28)
    s.put("B13", CATEGORY, font(10.5, True, CHARCOAL), Alignment(horizontal="left", vertical="center"), merge="B13:F13")
    s.h(14, 36)
    last = s.items(14, 15, font(11, True, WHITE), T, ("FFFFFF", "FFF7F0"))
    r = last + 1
    s.h(r, 14)
    r += 1
    s.h(r, 44); s.h(r + 1, 20)
    s.put(f"B{r}", WORDS, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:C{r + 1}")
    s.put(f"D{r}", "TOTAL VALUE", font(12, True, CHARCOAL), Alignment(horizontal="right", vertical="center"), merge=f"D{r}:E{r}")
    s.put(f"F{r}", f"=SUM(F15:F{last})", font(15, True, WHITE), Alignment(horizontal="center", vertical="center"), bg=CHARCOAL, fmt=MONEY)
    total = f"F{r}"
    # the big grand-total tile in the dark band (a formula: it follows the filled total)
    s.put("E8", f"={total}", font(24, True, WHITE), Alignment(horizontal="center", vertical="center"), merge="E8:F11", bg=T, fmt=MONEY)
    r += 2
    s.h(r, 22)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(11, True, T), Alignment(horizontal="left", vertical="center"), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:F{r}")
    bank = f"B{r}"
    r += 1
    s.h(r, 20)
    r += 1
    s.band([r, r + 1, r + 2], CHARCOAL)
    s.h(r, 8)
    s.h(r + 1, 26)
    s.put(f"B{r + 1}", "THANK YOU!", font(11, True, T), Alignment(horizontal="left", vertical="center"), merge=f"B{r + 1}:C{r + 1}")
    s.put(f"E{r + 1}", "Demo Trading Company Ltd.", font(9, False, MUTED_ON_DARK), Alignment(horizontal="right", vertical="center"), merge=f"E{r + 1}:F{r + 1}")
    s.h(r + 2, 8)
    s.print_area(r + 2, "A", "G")
    names(s, [("SupplierEnglishTarget", "B3"), ("SupplierChineseTarget", "B4"), ("SupplierAddressTarget", "B5"),
              ("InvoiceNumberTarget", "B11"), ("InvoiceDateTarget", "C11"), ("InvoiceTarget", total), ("SupplierBankTarget", bank)])


GREY_BG = "F3F4F6"
PAPER = "EEF1F5"
NAVY = "1B2A41"
SERIF = "Cambria"


def _std_names(s, eng, chi, addr, no, date, total, bank):
    names(s, [("SupplierEnglishTarget", eng), ("SupplierChineseTarget", chi), ("SupplierAddressTarget", addr),
              ("InvoiceNumberTarget", no), ("InvoiceDateTarget", date), ("InvoiceTarget", total), ("SupplierBankTarget", bank)])


def _words_total(s, r, first, last, label_font, label_bg, value_font, value_bg, words_font=None, label_align="center"):
    """Amount in words (left) + TOTAL VALUE label and the SUM (right) on rows r, r+1. Returns the total cell."""
    s.h(r, 44); s.h(r + 1, 20)
    s.put(f"B{r}", WORDS, words_font or font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:C{r + 1}")
    s.put(f"D{r}", "TOTAL VALUE", label_font, Alignment(horizontal=label_align, vertical="center"), merge=f"D{r}:E{r}", bg=label_bg)
    s.put(f"F{r}", f"=SUM(F{first}:F{last})", value_font, Alignment(horizontal="center", vertical="center"), bg=value_bg, fmt=MONEY)
    return f"F{r}"


# ---------------------------------------------------------------- INV7: "Sidebar"
def inv7(wb):
    """A theme-coloured spine down the left edge of the page; calm white body, theme accents on titles and the total."""
    s = Sheet(wb, "INV7", "2E7D32", {"A": 5, "B": 21, "C": 56, "D": 13, "E": 22, "F": 30, "G": 3})
    T = s.theme
    s.h(1, 4)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker + hairline top
    s.h(2, 22)
    s.h(3, 40)
    s.put("B3", "INVOICE", font(38, True, T), Alignment(horizontal="left", vertical="center"), merge="B3:C3")
    s.put("E3", "INVOICE NO", font(9, True, SOFT), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F3", "Mad-1234567", font(13, True, INK), Alignment(horizontal="left", vertical="center", indent=1), bg=GREY_BG)
    s.h(4, 8)
    s.h(5, 26)
    s.put("E5", "ISSUE DATE", font(9, True, SOFT), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F5", datetime(2026, 11, 11), font(12, False, INK), Alignment(horizontal="left", vertical="center", indent=1), bg=GREY_BG, fmt="dd/mm/yyyy")
    s.h(6, 18)
    s.h(7, 2)
    s.put("B7", merge="B7:F7", bg=LINE)
    s.h(8, 16)
    s.h(9, 20)
    s.put("B9", "FROM", font(9, True, T), merge="B9:C9")
    s.put("E9", "BUYER / CONSIGNEE", font(9, True, T), merge="E9:F9")
    s.h(10, 34)
    s.put("B10", "SAMPLE TRADING CO., LIMITED", font(16, True), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B10:C10")
    s.put("E10", BUYER_BODY, font(10.5), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="E10:F12")
    s.h(11, 24)
    s.put("B11", "示例贸易有限公司", font(12.5, False, SOFT), merge="B11:C11")
    s.h(12, 46)
    s.put("B12", ADDRESS, font(10, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B12:C12")
    s.h(13, 16)
    s.h(14, 28)
    s.put("B14", CATEGORY, font(11, True, INK), Alignment(horizontal="left", vertical="center"), merge="B14:F14")
    s.h(15, 36)
    last = s.items(15, 16, font(11, True, WHITE), CHARCOAL, ("FFFFFF", "F5F6F7"))
    r = last + 1
    s.h(r, 3)
    s.put(f"B{r}", merge=f"B{r}:F{r}", bg=T)
    r += 1
    s.h(r, 10)
    r += 1
    total = _words_total(s, r, 16, last, font(11, True, SOFT), None, font(15, True, WHITE), T, label_align="right")
    r += 2
    s.h(r, 22)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(11, True, T), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:F{r}", bg="F8F9FA")
    bank = f"B{r}"
    r += 1
    s.h(r, 18)
    r += 1
    s.h(r, 22)
    s.put(f"B{r}", "Thank you for your business", font(9.5, False, SOFT, italic=True), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 10)
    s.band(range(1, r + 1), c1="A", c2="A")       # the spine
    s.print_area(r, "A", "G")
    _std_names(s, "B10", "B11", "B12", "F3", "F5", total, bank)


# ---------------------------------------------------------------- INV8: "Minimal"
def inv8(wb):
    """Swiss minimal: no blocks, generous white space, hairlines, the theme colour only on the title and the total."""
    s = Sheet(wb, "INV8", "9A3412", WIDTHS)
    T = s.theme
    hair = Border(bottom=Side(style="thin", color="D1D5DB"))
    strong = Border(top=Side(style="medium", color=CHARCOAL))
    s.h(1, 3)
    s.put("B1", merge="B1:C1", bg=T)              # theme marker: a short rule top-left
    s.h(2, 30)
    s.h(3, 46)
    s.put("B3", "INVOICE", font(34, False, T), Alignment(horizontal="left", vertical="center"), merge="B3:C3")
    s.put("E3", "SAMPLE TRADING CO., LIMITED", font(13, True, CHARCOAL), Alignment(horizontal="right", vertical="bottom", wrap_text=True), merge="E3:F3")
    s.h(4, 22)
    s.put("E4", "示例贸易有限公司", font(11, False, SOFT), Alignment(horizontal="right", vertical="center"), merge="E4:F4")
    s.h(5, 44)
    s.put("E5", ADDRESS, font(9.5, False, SOFT), Alignment(horizontal="right", vertical="top", wrap_text=True), merge="E5:F5")
    s.h(6, 26)
    s.h(7, 18)
    for col, text in (("B", "INVOICE NO"), ("C", "ISSUE DATE"), ("E", "BUYER / CONSIGNEE")):
        s.put(f"{col}7", text, font(8.5, True, SOFT), Alignment(horizontal="left", vertical="bottom"), border=hair)
    s.put("D7", border=hair); s.put("F7", border=hair)
    s.h(8, 30)
    s.put("B8", "Mad-1234567", font(12, True, CHARCOAL), Alignment(horizontal="left", vertical="center"))
    s.put("C8", datetime(2026, 11, 11), font(12, False, CHARCOAL), Alignment(horizontal="left", vertical="center"), fmt="dd mmm yyyy")
    s.put("E8", BUYER_BODY, font(10, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="E8:F10")
    s.h(9, 22); s.h(10, 30)
    s.h(11, 22)
    s.h(12, 26)
    s.put("B12", CATEGORY, font(10, False, SOFT), merge="B12:F12")
    s.h(13, 30)
    header = Border(bottom=Side(style="medium", color=CHARCOAL))
    last = s.items(13, 14, font(9.5, True, CHARCOAL), None, ("FFFFFF", "FAFAFA"), grid=None, line_color="E5E7EB")
    for col in "BCDEF":
        s.ws[f"{col}13"].border = header
    r = last + 1
    s.h(r, 18)
    r += 1
    s.h(r, 44); s.h(r + 1, 20)
    s.put(f"B{r}", WORDS, font(10, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge=f"B{r}:C{r + 1}")
    s.put(f"D{r}", "TOTAL VALUE", font(10, True, CHARCOAL), Alignment(horizontal="right", vertical="center"), merge=f"D{r}:E{r}", border=strong)
    s.put(f"F{r}", f"=SUM(F14:F{last})", font(18, True, T), Alignment(horizontal="right", vertical="center"), fmt=MONEY, border=strong)
    total = f"F{r}"
    r += 2
    s.h(r, 26)
    r += 1
    s.h(r, 20)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(8.5, True, SOFT), Alignment(horizontal="left", vertical="bottom"), merge=f"B{r}:F{r}", border=hair)
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10, False, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:F{r}")
    bank = f"B{r}"
    r += 1
    s.h(r, 26)
    r += 1
    s.h(r, 3)
    s.put(f"E{r}", merge=f"E{r}:F{r}", bg=T)      # closing rule bottom-right
    s.print_area(r)
    _std_names(s, "E3", "E4", "E5", "B8", "C8", total, bank)


# ---------------------------------------------------------------- INV9: "Split Header"
def inv9(wb):
    """The header splits in two: a white half with the big INVOICE title and a theme half carrying the supplier."""
    s = Sheet(wb, "INV9", "6D28D9", WIDTHS)
    T = s.theme
    s.h(1, 1.5)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker
    s.band(range(1, 10), c1="D", c2="G")
    s.h(2, 24)
    s.h(3, 34)
    s.put("B3", "INVOICE", font(48, True, CHARCOAL), Alignment(horizontal="left", vertical="center"), merge="B3:C4")
    s.put("D3", "SAMPLE TRADING CO., LIMITED", font(15, True, WHITE), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge="D3:F3")
    s.h(4, 24)
    s.put("D4", "示例贸易有限公司", font(11.5, False, MUTED_ON_DARK), Alignment(horizontal="left", vertical="center", indent=1), merge="D4:F4")
    s.h(5, 46)
    s.put("D5", ADDRESS, font(9.5, False, MUTED_ON_DARK), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="D5:F5")
    s.h(6, 16)
    s.h(7, 18)
    s.put("B7", "INVOICE NO", font(9, True, SOFT), Alignment(horizontal="left", vertical="bottom"))
    s.put("C7", "ISSUE DATE", font(9, True, SOFT), Alignment(horizontal="left", vertical="bottom", indent=2))
    s.h(8, 28)
    s.put("B8", "Mad-1234567", font(13, True, T), Alignment(horizontal="left", vertical="center"))
    s.put("C8", datetime(2026, 11, 11), font(13, False, CHARCOAL), Alignment(horizontal="left", vertical="center", indent=2), fmt="dd/mm/yyyy")
    s.h(9, 24)
    s.h(10, 20)
    s.h(11, 22)
    s.put("B11", "BUYER / CONSIGNEE", font(9, True, T), merge="B11:C11")
    s.put("E11", "AMOUNT DUE", font(9, True, SOFT), Alignment(horizontal="right", vertical="center"), merge="E11:F11")
    s.h(12, 76)
    s.put("B12", BUYER_BODY, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="B12:C12", bg=GREY_BG)
    s.h(13, 18)
    s.h(14, 28)
    s.put("B14", CATEGORY, font(10.5, True, CHARCOAL), merge="B14:F14")
    s.h(15, 36)
    last = s.items(15, 17, font(11, True, WHITE), CHARCOAL, ("FFFFFF", "F4F4F6"))
    s.bar(16, 4)                                   # theme rule under the header
    r = last + 1
    s.h(r, 14)
    r += 1
    total = _words_total(s, r, 17, last, font(12, True, WHITE), CHARCOAL, font(15, True, WHITE), T)
    s.put("E12", f"={total}", font(26, True, T), Alignment(horizontal="right", vertical="top"), merge="E12:F12", fmt=MONEY)
    r += 2
    s.h(r, 22)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(11, True, CHARCOAL), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:F{r}", bg=GREY_BG)
    bank = f"B{r}"
    r += 1
    s.h(r, 18)
    r += 1
    s.h(r, 26)
    s.band([r], c1="D", c2="G")
    s.put(f"D{r}", "THANK YOU FOR YOUR BUSINESS", font(9.5, True, WHITE), Alignment(horizontal="left", vertical="center", indent=1), merge=f"D{r}:F{r}")
    s.print_area(r, "A", "G")
    _std_names(s, "D3", "D4", "D5", "B8", "C8", total, bank)


# ---------------------------------------------------------------- INV10: "Ribbon"
def inv10(wb):
    """A stepped theme ribbon hangs from the top-right corner with the invoice number and date; it echoes at the bottom-left."""
    s = Sheet(wb, "INV10", "C2185B", WIDTHS)
    T = s.theme
    s.h(1, 8)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker + top edge
    s.put("A1", bg=T); s.put("G1", bg=T)
    s.band(range(2, 8), c1="E", c2="G")
    s.band([8], c1="F", c2="G")
    s.h(2, 16)
    s.h(3, 40)
    s.put("B3", "SAMPLE TRADING CO., LIMITED", font(18, True, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B3:C3")
    s.put("E3", "INVOICE", font(30, True, WHITE), Alignment(horizontal="center", vertical="center"), merge="E3:F3")
    s.h(4, 22)
    s.put("B4", "示例贸易有限公司", font(12, False, SOFT), merge="B4:C4")
    s.put("E4", "INVOICE NO", font(9, True, MUTED_ON_DARK), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F4", "Mad-1234567", font(12, True, WHITE), Alignment(horizontal="left", vertical="center", indent=1))
    s.h(5, 44)
    s.put("B5", ADDRESS, font(9.5, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B5:C5")
    s.put("E5", "ISSUE DATE", font(9, True, MUTED_ON_DARK), Alignment(horizontal="right", vertical="top", indent=1))
    s.put("F5", datetime(2026, 11, 11), font(12, True, WHITE), Alignment(horizontal="left", vertical="top", indent=1), fmt="dd/mm/yyyy")
    s.h(6, 10); s.h(7, 10); s.h(8, 14)
    s.h(9, 16)
    s.h(10, 24)
    s.put("B10", "BUYER / CONSIGNEE", font(9.5, True, WHITE), Alignment(horizontal="left", vertical="center", indent=1), bg=CHARCOAL)
    s.h(11, 76)
    s.put("B11", BUYER_BODY, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="B11:C11",
          border=Border(left=Side(style="thick", color=CHARCOAL)))
    s.h(12, 16)
    s.h(13, 28)
    s.put("B13", CATEGORY, font(10.5, False, SOFT), merge="B13:F13")
    s.h(14, 36)
    last = s.items(14, 15, font(11, True, WHITE), T, ("FFFFFF", "F7F7F7"))
    r = last + 1
    s.h(r, 14)
    r += 1
    total = _words_total(s, r, 15, last, font(12, True, WHITE), CHARCOAL, font(15, True, WHITE), T)
    r += 2
    s.h(r, 22)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(11, True, T), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:F{r}")
    bank = f"B{r}"
    r += 1
    s.h(r, 14)
    s.band([r], c1="A", c2="B")
    r += 1
    s.h(r, 28)
    s.band([r], c1="A", c2="C")
    s.put(f"B{r}", "THANK YOU FOR YOUR BUSINESS", font(10, True, WHITE), Alignment(horizontal="left", vertical="center"), merge=f"B{r}:C{r}")
    r += 1
    s.h(r, 8)
    s.band([r], c1="A", c2="G")
    s.print_area(r, "A", "G")
    _std_names(s, "B3", "B4", "B5", "F4", "F5", total, bank)


# ---------------------------------------------------------------- INV11: "Cards"
def inv11(wb):
    """Soft grey paper with white cards: header card with a theme cap, parties card, items card, payment card."""
    s = Sheet(wb, "INV11", "0277BD", WIDTHS)
    T = s.theme

    def gap(r, h=12):
        s.h(r, h)
        s.band([r], PAPER)

    def card(rows):
        s.band(rows, PAPER, "A", "A")
        s.band(rows, PAPER, "G", "G")
        s.band(rows, WHITE, "B", "F")

    s.h(1, 14)
    s.band([1], PAPER, "A", "A"); s.band([1], PAPER, "G", "G")
    s.put("B1", merge="B1:F1", bg=T)              # theme marker: the cap of the header card
    card(range(2, 9))
    s.h(2, 14)
    s.h(3, 46)
    s.put("B3", "INVOICE", font(34, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge="B3:C3")
    s.put("E3", "INVOICE NO", font(9, True, SOFT), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F3", "Mad-1234567", font(13, True, CHARCOAL), Alignment(horizontal="left", vertical="center", indent=1))
    s.h(4, 26)
    s.put("B4", "SAMPLE TRADING CO., LIMITED", font(15, True, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge="B4:C4")
    s.put("E4", "ISSUE DATE", font(9, True, SOFT), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F4", datetime(2026, 11, 11), font(12, False, CHARCOAL), Alignment(horizontal="left", vertical="center", indent=1), fmt="dd/mm/yyyy")
    s.h(5, 22)
    s.put("B5", "示例贸易有限公司", font(12, False, SOFT), Alignment(horizontal="left", vertical="center", indent=1), merge="B5:C5")
    s.h(6, 44)
    s.put("B6", ADDRESS, font(9.5, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="B6:C6")
    s.h(7, 6); s.h(8, 8)
    gap(9)
    card(range(10, 13))
    s.h(10, 26)
    s.put("B10", "BUYER / CONSIGNEE", font(9.5, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge="B10:C10")
    s.put("E10", "CURRENCY", font(9.5, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge="E10:F10")
    s.h(11, 76)
    s.put("B11", BUYER_BODY, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="B11:C11")
    s.put("E11", "US Dollar (USD)", font(12, True, CHARCOAL), Alignment(horizontal="left", vertical="top", indent=1), merge="E11:F11")
    s.h(12, 8)
    gap(13)
    card(range(14, 16 + len(ITEMS)))
    s.h(14, 28)
    s.put("B14", CATEGORY, font(10.5, True, CHARCOAL), Alignment(horizontal="left", vertical="center", indent=1), merge="B14:F14")
    s.h(15, 36)
    last = s.items(15, 16, font(11, True, WHITE), T, ("FFFFFF", "F6F8FA"))
    for i in range(len(ITEMS)):
        s.ws[f"A{16 + i}"].fill = fill(PAPER); s.ws[f"G{16 + i}"].fill = fill(PAPER)
    r = last + 1
    card([r, r + 1, r + 2, r + 3])
    s.h(r, 12)
    r += 1
    total = _words_total(s, r, 16, last, font(11, True, SOFT), None, font(15, True, WHITE), T, label_align="right")
    s.ws[f"B{r}"].alignment = Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1)
    r += 2
    s.h(r, 12)
    r += 1
    gap(r)
    r += 1
    card([r, r + 1, r + 2])
    s.h(r, 28)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(11, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:F{r}")
    bank = f"B{r}"
    r += 1
    s.h(r, 10)
    r += 1
    gap(r, 16)
    s.print_area(r, "A", "G")
    _std_names(s, "B4", "B5", "B6", "F3", "F4", total, bank)


# ---------------------------------------------------------------- INV12: "Executive"
def inv12(wb):
    """Classic executive letterhead: serif type, centred masthead, navy table head, theme (gold) rules and title."""
    s = Sheet(wb, "INV12", "B08D57", WIDTHS)
    T = s.theme

    def f(size=11, bold=False, color=NAVY, italic=False):
        return font(size, bold, color, italic, SERIF)

    s.h(1, 6)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker: gold top rule
    s.h(2, 24)
    s.h(3, 34)
    s.put("B3", "SAMPLE TRADING CO., LIMITED", f(20, True), Alignment(horizontal="center", vertical="center", wrap_text=True), merge="B3:F3")
    s.h(4, 22)
    s.put("B4", "示例贸易有限公司", f(12, False, SOFT), Alignment(horizontal="center", vertical="center"), merge="B4:F4")
    s.h(5, 40)
    s.put("B5", ADDRESS, f(10, False, SOFT, True), Alignment(horizontal="center", vertical="top", wrap_text=True), merge="B5:F5")
    s.h(6, 10)
    s.h(7, 2)
    s.put("C7", merge="C7:E7", bg=T)
    s.h(8, 54)
    s.put("B8", "INVOICE", f(30, True, T), Alignment(horizontal="center", vertical="center"), merge="B8:F8")
    s.h(9, 2)
    s.put("C9", merge="C9:E9", bg=T)
    s.h(10, 20)
    s.h(11, 20)
    s.put("B11", "BUYER / CONSIGNEE", f(9.5, True, T), merge="B11:C11")
    s.put("E11", "INVOICE NO", f(9.5, True, SOFT), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F11", "Mad-1234567", f(13, True), Alignment(horizontal="left", vertical="center", indent=1))
    s.h(12, 26)
    s.put("B12", BUYER_BODY, f(11), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B12:C14")
    s.put("E12", "ISSUE DATE", f(9.5, True, SOFT), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F12", datetime(2026, 11, 11), f(12), Alignment(horizontal="left", vertical="center", indent=1), fmt="d mmmm yyyy")
    s.h(13, 26)
    s.put("E13", "CURRENCY", f(9.5, True, SOFT), Alignment(horizontal="right", vertical="center", indent=1))
    s.put("F13", "US Dollar (USD)", f(12), Alignment(horizontal="left", vertical="center", indent=1))
    s.h(14, 24)
    s.h(15, 14)
    s.h(16, 28)
    s.put("B16", CATEGORY, f(11, False, NAVY, True), Alignment(horizontal="center", vertical="center"), merge="B16:F16")
    s.h(17, 36)
    last = s.items(17, 18, f(11, True, T), NAVY, ("FFFFFF", "F8F6F1"), line_color="E7E1D3")
    for r in range(18, last + 1):
        for col in "BCDEF":
            c = s.ws[f"{col}{r}"]
            c.font = Font(name=SERIF, size=11, bold=c.font.b, color=NAVY)
    r = last + 1
    s.h(r, 3)
    s.put(f"B{r}", merge=f"B{r}:F{r}", bg=T)
    r += 1
    s.h(r, 10)
    r += 1
    total = _words_total(s, r, 18, last, f(12, True, NAVY), None, f(15, True, T), NAVY, f(10.5, False, NAVY, True), label_align="right")
    r += 2
    s.h(r, 24)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", f(11, True, T), Alignment(horizontal="center", vertical="center"), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, f(11), Alignment(horizontal="center", vertical="center", wrap_text=True), merge=f"B{r}:F{r}",
          border=Border(top=Side(style="thin", color="D6CCB4"), bottom=Side(style="thin", color="D6CCB4")))
    bank = f"B{r}"
    r += 1
    s.h(r, 20)
    r += 1
    s.h(r, 22)
    s.put(f"B{r}", "With our compliments", f(10, False, SOFT, True), Alignment(horizontal="center", vertical="center"), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 6)
    s.put(f"B{r}", merge=f"B{r}:F{r}", bg=T)
    s.print_area(r)
    _std_names(s, "B3", "B4", "B5", "F11", "F12", total, bank)


# ---------------------------------------------------------------- INV13: "Total Footer"
def inv13(wb):
    """Light top, and the whole weight at the bottom: a full-width theme block with the grand total in big type."""
    s = Sheet(wb, "INV13", "D32F2F", WIDTHS)
    T = s.theme
    s.h(1, 6)
    s.put("B1", bg=T)                              # theme marker: a square tab top-left
    s.h(2, 20)
    s.h(3, 50)
    s.put("B3", "INVOICE", font(44, True, CHARCOAL), Alignment(horizontal="left", vertical="center"), merge="B3:C3")
    s.put("E3", "Mad-1234567", font(15, True, T), Alignment(horizontal="right", vertical="center"), merge="E3:F3")
    s.h(4, 20)
    s.put("B4", "No. / Date", font(9, False, SOFT), merge="B4:C4")
    s.put("E4", datetime(2026, 11, 11), font(11, False, SOFT), Alignment(horizontal="right", vertical="center"), merge="E4:F4", fmt="dd/mm/yyyy")
    s.h(5, 18)
    s.h(6, 20)
    s.put("B6", "SUPPLIER", font(9, True, T), merge="B6:C6")
    s.put("E6", "BUYER / CONSIGNEE", font(9, True, T), merge="E6:F6")
    s.h(7, 32)
    s.put("B7", "SAMPLE TRADING CO., LIMITED", font(15, True, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B7:C7")
    s.put("E7", BUYER_BODY, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="E7:F9")
    s.h(8, 22)
    s.put("B8", "示例贸易有限公司", font(12, False, SOFT), merge="B8:C8")
    s.h(9, 44)
    s.put("B9", ADDRESS, font(9.5, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B9:C9")
    s.h(10, 18)
    s.h(11, 28)
    s.put("B11", CATEGORY, font(10.5, False, SOFT), merge="B11:F11")
    s.h(12, 34)
    last = s.items(12, 13, font(10.5, True, CHARCOAL), "ECECEC", ("FFFFFF", "F7F7F7"))
    r = last + 1
    s.h(r, 22)
    r += 1
    s.band(range(r, r + 4))
    s.h(r, 14)
    s.h(r + 1, 24)
    s.put(f"B{r + 1}", "TOTAL VALUE", font(10, True, WHITE), Alignment(horizontal="left", vertical="bottom"), merge=f"B{r + 1}:C{r + 1}")
    s.h(r + 2, 48)
    s.put(f"D{r + 1}", f"=SUM(F13:F{last})", font(30, True, WHITE), Alignment(horizontal="right", vertical="center"), merge=f"D{r + 1}:F{r + 2}", fmt=MONEY)
    s.put(f"B{r + 2}", WORDS, font(10, False, WHITE), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r + 2}:C{r + 2}")
    total = f"D{r + 1}"
    s.h(r + 3, 14)
    r += 4
    s.h(r, 22)
    r += 1
    s.h(r, 26)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(11, True, CHARCOAL), merge=f"B{r}:F{r}")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:F{r}", bg="F7F7F7")
    bank = f"B{r}"
    r += 1
    s.h(r, 22)
    r += 1
    s.h(r, 6)
    s.put(f"F{r}", bg=T)
    s.print_area(r, "A", "G")
    _std_names(s, "B7", "B8", "B9", "E3", "E4", total, bank)


# ---------------------------------------------------------------- INV14: "Tabs"
def inv14(wb):
    """Every section opens with a theme tab and a matching rule, like folder dividers."""
    s = Sheet(wb, "INV14", "00897B", WIDTHS)
    T = s.theme

    def tab(r, text, c2="F", col="B"):
        s.h(r, 24)
        s.put(f"{col}{r}", text, font(9.5, True, WHITE), Alignment(horizontal="center", vertical="center"), bg=T)
        s.h(r + 1, 2)
        s.put(f"{col}{r + 1}", merge=f"{col}{r + 1}:{c2}{r + 1}", bg=T)

    s.h(1, 3)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker
    s.h(2, 22)
    s.h(3, 48)
    s.put("B3", "INVOICE", font(40, True, CHARCOAL), Alignment(horizontal="left", vertical="center"), merge="B3:C3")
    s.put("E3", "INVOICE NO", font(9, True, WHITE), Alignment(horizontal="center", vertical="center"), bg=T)
    s.put("F3", "Mad-1234567", font(13, True, CHARCOAL), Alignment(horizontal="center", vertical="center"),
          border=Border(*(Side(style="thin", color="CFD8DC"),) * 4))
    s.h(4, 6)
    s.h(5, 30)
    s.put("E5", "ISSUE DATE", font(9, True, WHITE), Alignment(horizontal="center", vertical="center"), bg=CHARCOAL)
    s.put("F5", datetime(2026, 11, 11), font(12, False, CHARCOAL), Alignment(horizontal="center", vertical="center"), fmt="dd/mm/yyyy",
          border=Border(*(Side(style="thin", color="CFD8DC"),) * 4))
    s.h(6, 18)
    tab(7, "SUPPLIER", "C")
    s.put("E7", "BUYER / CONSIGNEE", font(9.5, True, WHITE), Alignment(horizontal="center", vertical="center"), bg=T)
    s.put("E8", merge="E8:F8", bg=T)
    s.h(9, 34)
    s.put("B9", "SAMPLE TRADING CO., LIMITED", font(16, True, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True), merge="B9:C9")
    s.put("E9", BUYER_BODY, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="E9:F11")
    s.h(10, 22)
    s.put("B10", "示例贸易有限公司", font(12, False, SOFT), merge="B10:C10")
    s.h(11, 46)
    s.put("B11", ADDRESS, font(9.5, False, SOFT), Alignment(horizontal="left", vertical="top", wrap_text=True), merge="B11:C11")
    s.h(12, 20)
    tab(13, "ITEMS")
    s.h(15, 28)
    s.put("B15", CATEGORY, font(10.5, False, SOFT), merge="B15:F15")
    s.h(16, 34)
    last = s.items(16, 17, font(10.5, True, CHARCOAL), "EEF1F1", ("FFFFFF", "F6F8F8"))
    r = last + 1
    s.h(r, 14)
    r += 1
    total = _words_total(s, r, 17, last, font(11.5, True, WHITE), CHARCOAL, font(15, True, WHITE), T)
    r += 2
    s.h(r, 22)
    r += 1
    tab(r, "PAYMENT")
    r += 2
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5), Alignment(horizontal="left", vertical="center", wrap_text=True), merge=f"B{r}:F{r}")
    bank = f"B{r}"
    r += 1
    s.h(r, 18)
    r += 1
    s.h(r, 24)
    s.put(f"F{r}", "THANK YOU", font(9.5, True, WHITE), Alignment(horizontal="center", vertical="center"), bg=T)
    s.print_area(r)
    _std_names(s, "B9", "B10", "B11", "F3", "F5", total, bank)


# ---------------------------------------------------------------- INV15: "Blueprint"
def inv15(wb):
    """Form style: a solid theme title bar, then every field in its own labelled box, the items in a full grid."""
    s = Sheet(wb, "INV15", "1E3A8A", WIDTHS)
    T = s.theme
    edge = Side(style="thin", color="C7CED9")
    box = Border(left=edge, right=edge, top=edge, bottom=edge)
    lab = Border(left=edge, right=edge, top=edge)
    val = Border(left=edge, right=edge, bottom=edge)

    def field(r, c1, c2, label, value, f, fmt=None, h_val=None):
        rng = f"{c1}{r}" if c1 == c2 else f"{c1}{r}:{c2}{r}"
        s.put(f"{c1}{r}", label, font(8, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge=None if c1 == c2 else rng, border=lab, bg="F8FAFC")
        rng2 = f"{c1}{r + 1}" if c1 == c2 else f"{c1}{r + 1}:{c2}{r + 1}"
        s.put(f"{c1}{r + 1}", value, f, Alignment(horizontal="left", vertical="top" if h_val else "center", wrap_text=True, indent=1),
              merge=None if c1 == c2 else rng2, border=val, fmt=fmt)

    s.h(1, 2)
    s.put("B1", merge="B1:F1", bg=T)              # theme marker
    s.band(range(2, 5), c1="B", c2="F")
    s.h(2, 10)
    s.h(3, 46)
    s.put("B3", "INVOICE", font(32, True, WHITE), Alignment(horizontal="left", vertical="center", indent=1), merge="B3:C3")
    s.put("E3", "COMMERCIAL INVOICE", font(10, True, MUTED_ON_DARK), Alignment(horizontal="right", vertical="center", indent=1), merge="E3:F3")
    s.h(4, 10)
    s.h(5, 12)
    s.h(6, 18); s.h(7, 30)
    field(6, "B", "C", "SUPPLIER (ENGLISH)", "SAMPLE TRADING CO., LIMITED", font(14, True, CHARCOAL))
    field(6, "D", "E", "INVOICE NO", "Mad-1234567", font(12, True, T))
    field(6, "F", "F", "ISSUE DATE", datetime(2026, 11, 11), font(12, False, CHARCOAL), fmt="dd/mm/yyyy")
    s.h(8, 18); s.h(9, 26)
    field(8, "B", "C", "SUPPLIER (NATIVE)", "示例贸易有限公司", font(12, False, CHARCOAL))
    field(8, "D", "F", "BUYER / CONSIGNEE", BUYER_BODY, font(10, False, CHARCOAL), h_val=True)
    s.ws.merge_cells("D9:F11")
    for rr in (10, 11):
        for col in "DEF":
            s.ws[f"{col}{rr}"].border = val if rr == 11 else Border(left=edge, right=edge)
    s.h(10, 18); s.h(11, 44)
    s.put("B10", "SUPPLIER ADDRESS", font(8, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge="B10:C10", border=lab, bg="F8FAFC")
    s.put("B11", ADDRESS, font(9.5, False, CHARCOAL), Alignment(horizontal="left", vertical="top", wrap_text=True, indent=1), merge="B11:C11", border=val)
    s.h(12, 14)
    s.h(13, 28)
    s.put("B13", CATEGORY, font(10.5, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge="B13:F13", border=box, bg="F8FAFC")
    s.h(14, 34)
    last = s.items(14, 15, font(10.5, True, WHITE), T, ("FFFFFF", "F5F7FA"), grid=box)
    r = last + 1
    s.h(r, 12)
    r += 1
    total = _words_total(s, r, 15, last, font(11.5, True, T), "F8FAFC", font(15, True, WHITE), T)
    for col in "BCDE":
        s.ws[f"{col}{r}"].border = box
    for col in "BC":
        s.ws[f"{col}{r + 1}"].border = box
    s.ws[f"B{r}"].alignment = Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1)
    r += 2
    s.h(r, 16)
    r += 1
    s.h(r, 18)
    s.put(f"B{r}", "PAYMENT & BANKING INSTRUCTIONS", font(8, True, T), Alignment(horizontal="left", vertical="center", indent=1), merge=f"B{r}:F{r}", border=lab, bg="F8FAFC")
    r += 1
    s.h(r, 150)
    s.put(f"B{r}", BANK, font(10.5, False, CHARCOAL), Alignment(horizontal="left", vertical="center", wrap_text=True, indent=1), merge=f"B{r}:F{r}", border=val)
    bank = f"B{r}"
    r += 1
    s.h(r, 16)
    r += 1
    s.h(r, 8)
    s.band([r], c1="B", c2="F")
    s.print_area(r)
    _std_names(s, "B7", "B9", "B11", "D7", "F7", total, bank)


DESIGNS = [inv2, inv3, inv4, inv5, inv6, inv7, inv8, inv9, inv10, inv11, inv12, inv13, inv14, inv15]


def main(out, base=None):
    """Builds every design; with <base> (an existing Templates.xlsx) only the sheets it lacks are added, the rest stay as edited."""
    if base:
        from openpyxl import load_workbook
        wb = load_workbook(base)
    else:
        wb = Workbook()
        wb.remove(wb.active)
    for make in DESIGNS:
        if make.__name__.upper() not in [n.upper() for n in wb.sheetnames]:
            make(wb)
    wb.active = 0
    wb.save(out)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "Templates.xlsx", sys.argv[2] if len(sys.argv) > 2 else None)
