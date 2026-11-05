using System.IO.Compression;
using System.Text;

namespace Nexus.Tests;

/// <summary>
/// Builds a macro-enabled workbook (.xlsm) by writing its package parts directly, with the features
/// spreadsheet libraries are known to drop when they save: a VBA project, a form button (ctrlProps +
/// VML), a cell comment, a custom XML part, x14 data validation (a dropdown from another sheet), x14
/// conditional formatting (data bars), classic conditional formatting, a defined name, a table, a
/// date cell, formulas, and pre-built blank rows whose formulas are already in place.
/// </summary>
internal static class TestWorkbooks
{
    public const string Sheet = "Index";

    /// <summary>Rows 2-4 hold sheets A-101, A-102, C-201; rows 5-7 are blank rows with formulas ready.</summary>
    public static void WriteFeatureRich(string path)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, content) in Parts()) Add(zip, name, content);
        var vba = zip.CreateEntry("xl/vbaProject.bin");
        using (var s = vba.Open()) s.Write(Encoding.ASCII.GetBytes("NEXUS-TEST-VBA-PROJECT\0Attribute VB_Name = \"Module1\"\0Sub UpdateIndex()\0End Sub"));
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private const string Decl = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n";
    private const string Ns = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"";
    private const string R = "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    private static IEnumerable<(string, string)> Parts()
    {
        yield return ("[Content_Types].xml", Decl +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"bin\" ContentType=\"application/vnd.ms-office.vbaProject\"/>" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"vml\" ContentType=\"application/vnd.openxmlformats-officedocument.vmlDrawing\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.ms-excel.sheet.macroEnabled.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
            "<Override PartName=\"/xl/comments1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.comments+xml\"/>" +
            "<Override PartName=\"/xl/ctrlProps/ctrlProp1.xml\" ContentType=\"application/vnd.ms-excel.controlproperties+xml\"/>" +
            "<Override PartName=\"/xl/tables/table1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.table+xml\"/>" +
            "<Override PartName=\"/customXml/itemProps1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.customXmlProperties+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
            "</Types>");

        yield return ("_rels/.rels", Decl +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
            "</Relationships>");

        yield return ("docProps/core.xml", Decl +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            "<dc:title>Nexus fidelity test</dc:title><dc:creator>Nexus tests</dc:creator></cp:coreProperties>");
        yield return ("docProps/app.xml", Decl +
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Microsoft Excel</Application></Properties>");

        yield return ("customXml/item1.xml", Decl + "<nexusTemplate xmlns=\"urn:nexus:test\"><version>3</version><owner>Project Controls</owner></nexusTemplate>");
        yield return ("customXml/itemProps1.xml", Decl +
            "<ds:datastoreItem ds:itemID=\"{6F1C2A9E-3B54-4C8D-9E21-7A0B5D3C4E1F}\" xmlns:ds=\"http://schemas.openxmlformats.org/officeDocument/2006/customXml\">" +
            "<ds:schemaRefs><ds:schemaRef ds:uri=\"urn:nexus:test\"/></ds:schemaRefs></ds:datastoreItem>");
        yield return ("customXml/_rels/item1.xml.rels", Decl +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXmlProps\" Target=\"itemProps1.xml\"/>" +
            "</Relationships>");

        yield return ("xl/_rels/workbook.xml.rels", Decl +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet2.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
            "<Relationship Id=\"rId5\" Type=\"http://schemas.microsoft.com/office/2006/relationships/vbaProject\" Target=\"vbaProject.bin\"/>" +
            "<Relationship Id=\"rId6\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/customXml\" Target=\"../customXml/item1.xml\"/>" +
            "</Relationships>");

        yield return ("xl/workbook.xml", Decl +
            $"<workbook {Ns} {R}>" +
            "<workbookPr codeName=\"ThisWorkbook\" defaultThemeVersion=\"166925\"/>" +
            "<bookViews><workbookView xWindow=\"0\" yWindow=\"0\" windowWidth=\"28800\" windowHeight=\"12300\"/></bookViews>" +
            "<sheets><sheet name=\"Index\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Lists\" sheetId=\"2\" r:id=\"rId2\"/></sheets>" +
            "<definedNames><definedName name=\"StatusList\">Lists!$A$1:$A$3</definedName></definedNames>" +
            "<calcPr calcId=\"191029\"/>" +
            "</workbook>");

        yield return ("xl/styles.xml", Decl +
            $"<styleSheet {Ns}>" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDE6F2\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"4\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
            "<xf numFmtId=\"14\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "<xf numFmtId=\"49\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "</cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "<dxfs count=\"1\"><dxf><font><color rgb=\"FF9C0006\"/></font><fill><patternFill><bgColor rgb=\"FFFFC7CE\"/></patternFill></fill></dxf></dxfs>" +
            "</styleSheet>");

        // 0 Sheet No. 1 Sheet Title 2 Rev 3 Status 4 Rev x2 5 Issued 6 A-101 7 Cover 8 A-102 9 Plan 10 C-201 11 Grading
        // 12 Issued 13 Draft 14 Superseded 15 Revision clouds pending
        string[] strings =
        {
            "Sheet No.", "Sheet Title", "Rev", "Status", "Rev x2", "Issued", "A-101", "Cover", "A-102", "Plan", "C-201", "Grading",
            "Issued", "Draft", "Superseded", "Revision clouds pending",
        };
        yield return ("xl/sharedStrings.xml", Decl +
            $"<sst {Ns} count=\"{strings.Length}\" uniqueCount=\"{strings.Length}\">" +
            string.Concat(strings.Select(s => $"<si><t>{s}</t></si>")) + "</sst>");

        string Text(string r, int s, int style = 3) => $"<c r=\"{r}\" s=\"{style}\" t=\"s\"><v>{s}</v></c>";
        string Num(string r, string v, int style = 0) => $"<c r=\"{r}\"{(style == 0 ? "" : $" s=\"{style}\"")}><v>{v}</v></c>";
        string Formula(string r, string f, string v) => $"<c r=\"{r}\"><f>{f}</f><v>{v}</v></c>";

        yield return ("xl/worksheets/sheet1.xml", Decl +
            $"<worksheet {Ns} {R} xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
            "xmlns:x14ac=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/ac\" mc:Ignorable=\"x14ac\">" +
            "<sheetPr codeName=\"Sheet1\"/>" +
            "<dimension ref=\"A1:F7\"/>" +
            "<sheetViews><sheetView tabSelected=\"1\" workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>" +
            "<sheetFormatPr defaultRowHeight=\"15\" x14ac:dyDescent=\"0.25\"/>" +
            "<cols><col min=\"1\" max=\"1\" width=\"12\" customWidth=\"1\"/><col min=\"2\" max=\"2\" width=\"30\" customWidth=\"1\"/></cols>" +
            "<sheetData>" +
            "<row r=\"1\">" + Text("A1", 0, 1) + Text("B1", 1, 1) + Text("C1", 2, 1) + Text("D1", 3, 1) + Text("E1", 4, 1) + Text("F1", 5, 1) + "</row>" +
            "<row r=\"2\">" + Text("A2", 6) + Text("B2", 7) + Num("C2", "1") + Text("D2", 12) + Formula("E2", "IF(A2=\"\",\"\",C2*2)", "2") + Num("F2", "46000", 2) + "</row>" +
            "<row r=\"3\">" + Text("A3", 8) + Text("B3", 9) + Num("C3", "2") + Text("D3", 13) + Formula("E3", "IF(A3=\"\",\"\",C3*2)", "4") + Num("F3", "46010", 2) + "</row>" +
            "<row r=\"4\">" + Text("A4", 10) + Text("B4", 11) + Num("C4", "0") + Text("D4", 13) + Formula("E4", "IF(A4=\"\",\"\",C4*2)", "0") + Num("F4", "46020", 2) + "</row>" +
            // Pre-built rows: formatted, formula in place, waiting for data.
            "<row r=\"5\"><c r=\"A5\" s=\"3\"/><c r=\"B5\" s=\"3\"/><c r=\"C5\"/>" + Formula("E5", "IF(A5=\"\",\"\",C5*2)", "") + "<c r=\"F5\" s=\"2\"/></row>" +
            "<row r=\"6\"><c r=\"A6\" s=\"3\"/><c r=\"B6\" s=\"3\"/><c r=\"C6\"/>" + Formula("E6", "IF(A6=\"\",\"\",C6*2)", "") + "<c r=\"F6\" s=\"2\"/></row>" +
            "<row r=\"7\"><c r=\"A7\" s=\"3\"/><c r=\"B7\" s=\"3\"/><c r=\"C7\"/>" + Formula("E7", "IF(A7=\"\",\"\",C7*2)", "") + "<c r=\"F7\" s=\"2\"/></row>" +
            "</sheetData>" +
            "<conditionalFormatting sqref=\"D2:D100\"><cfRule type=\"cellIs\" dxfId=\"0\" priority=\"2\" operator=\"equal\"><formula>\"Superseded\"</formula></cfRule></conditionalFormatting>" +
            "<conditionalFormatting sqref=\"C2:C100\"><cfRule type=\"dataBar\" priority=\"1\"><dataBar><cfvo type=\"min\"/><cfvo type=\"max\"/><color rgb=\"FF638EC6\"/></dataBar>" +
            "<extLst><ext uri=\"{B025F937-C7B1-47D3-B67F-A62EFF666E3E}\" xmlns:x14=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\"><x14:id>{1B6D2E3F-0A4C-4B5D-8E6F-7A8B9C0D1E2F}</x14:id></ext></extLst>" +
            "</cfRule></conditionalFormatting>" +
            "<pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>" +
            "<pageSetup orientation=\"landscape\"/>" +
            "<legacyDrawing r:id=\"rId2\"/>" +
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"x14\"><controls>" +
            "<mc:AlternateContent xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"><mc:Choice Requires=\"x14\">" +
            "<control shapeId=\"1026\" r:id=\"rId3\" name=\"Button 2\"><controlPr defaultSize=\"0\" print=\"0\" autoFill=\"0\" autoPict=\"0\" macro=\"[0]!UpdateIndex\">" +
            "<anchor moveWithCells=\"1\" sizeWithCells=\"1\"><from><xdr:col xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">7</xdr:col>" +
            "<xdr:colOff xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">0</xdr:colOff>" +
            "<xdr:row xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">1</xdr:row>" +
            "<xdr:rowOff xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">0</xdr:rowOff></from>" +
            "<to><xdr:col xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">9</xdr:col>" +
            "<xdr:colOff xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">0</xdr:colOff>" +
            "<xdr:row xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">3</xdr:row>" +
            "<xdr:rowOff xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\">0</xdr:rowOff></to></anchor>" +
            "</controlPr></control></mc:Choice></mc:AlternateContent></controls></mc:Choice></mc:AlternateContent>" +
            "<tableParts count=\"1\"><tablePart r:id=\"rId4\"/></tableParts>" +
            "<extLst>" +
            "<ext uri=\"{78C0D931-6437-407d-A8EE-F0AAD7539E65}\" xmlns:x14=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\">" +
            "<x14:conditionalFormattings><x14:conditionalFormatting xmlns:xm=\"http://schemas.microsoft.com/office/excel/2006/main\">" +
            "<x14:cfRule type=\"dataBar\" id=\"{1B6D2E3F-0A4C-4B5D-8E6F-7A8B9C0D1E2F}\"><x14:dataBar minLength=\"0\" maxLength=\"100\" gradient=\"0\">" +
            "<x14:cfvo type=\"autoMin\"/><x14:cfvo type=\"autoMax\"/><x14:negativeFillColor rgb=\"FFFF0000\"/><x14:axisColor rgb=\"FF000000\"/></x14:dataBar></x14:cfRule>" +
            "<xm:sqref>C2:C100</xm:sqref></x14:conditionalFormatting></x14:conditionalFormattings></ext>" +
            "<ext uri=\"{CCE6A557-97BC-4b89-ADB6-D9C93CAAB3DF}\" xmlns:x14=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\">" +
            "<x14:dataValidations count=\"1\" xmlns:xm=\"http://schemas.microsoft.com/office/excel/2006/main\">" +
            "<x14:dataValidation type=\"list\" allowBlank=\"1\" showInputMessage=\"1\" showErrorMessage=\"1\">" +
            "<x14:formula1><xm:f>Lists!$A$1:$A$3</xm:f></x14:formula1><xm:sqref>D2:D100</xm:sqref></x14:dataValidation></x14:dataValidations></ext>" +
            "</extLst>" +
            "</worksheet>");

        yield return ("xl/worksheets/_rels/sheet1.xml.rels", Decl +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/comments\" Target=\"../comments1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/vmlDrawing\" Target=\"../drawings/vmlDrawing1.vml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/ctrlProp\" Target=\"../ctrlProps/ctrlProp1.xml\"/>" +
            "<Relationship Id=\"rId4\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/table\" Target=\"../tables/table1.xml\"/>" +
            "</Relationships>");

        yield return ("xl/worksheets/sheet2.xml", Decl +
            $"<worksheet {Ns} {R}><dimension ref=\"A1:A3\"/><sheetData>" +
            "<row r=\"1\">" + Text("A1", 12, 0) + "</row><row r=\"2\">" + Text("A2", 13, 0) + "</row><row r=\"3\">" + Text("A3", 14, 0) + "</row>" +
            "</sheetData><pageMargins left=\"0.7\" right=\"0.7\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/></worksheet>");

        yield return ("xl/comments1.xml", Decl +
            $"<comments {Ns}><authors><author>Project Controls</author></authors><commentList>" +
            "<comment ref=\"B2\" authorId=\"0\"><text><r><t>Revision clouds pending</t></r></text></comment></commentList></comments>");

        yield return ("xl/drawings/vmlDrawing1.vml",
            "<xml xmlns:v=\"urn:schemas-microsoft-com:vml\" xmlns:o=\"urn:schemas-microsoft-com:office:office\" xmlns:x=\"urn:schemas-microsoft-com:office:excel\">" +
            "<o:shapelayout v:ext=\"edit\"><o:idmap v:ext=\"edit\" data=\"1\"/></o:shapelayout>" +
            "<v:shapetype id=\"_x0000_t202\" coordsize=\"21600,21600\" o:spt=\"202\" path=\"m,l,21600r21600,l21600,xe\"><v:stroke joinstyle=\"miter\"/><v:path gradientshapeok=\"t\" o:connecttype=\"rect\"/></v:shapetype>" +
            "<v:shape id=\"_x0000_s1025\" type=\"#_x0000_t202\" style=\"position:absolute;margin-left:200pt;margin-top:10pt;width:108pt;height:60pt;z-index:1;visibility:hidden\" fillcolor=\"#ffffe1\" o:insetmode=\"auto\">" +
            "<v:fill color2=\"#ffffe1\"/><v:shadow on=\"t\" color=\"black\" obscured=\"t\"/><v:path o:connecttype=\"none\"/><v:textbox style=\"mso-direction-alt:auto\"><div style=\"text-align:left\"></div></v:textbox>" +
            "<x:ClientData ObjectType=\"Note\"><x:MoveWithCells/><x:SizeWithCells/><x:Anchor>2, 15, 0, 10, 4, 15, 4, 4</x:Anchor><x:AutoFill>False</x:AutoFill><x:Row>1</x:Row><x:Column>1</x:Column></x:ClientData></v:shape>" +
            "<v:shapetype id=\"_x0000_t201\" coordsize=\"21600,21600\" o:spt=\"201\" path=\"m,l,21600r21600,l21600,xe\"><v:stroke joinstyle=\"miter\"/><v:path shadowok=\"f\" o:extrusionok=\"f\" strokeok=\"f\" fillok=\"f\" o:connecttype=\"rect\"/><o:lock v:ext=\"edit\" shapetype=\"t\"/></v:shapetype>" +
            "<v:shape id=\"_x0000_s1026\" type=\"#_x0000_t201\" style=\"position:absolute;margin-left:400pt;margin-top:15pt;width:96pt;height:30pt;z-index:2\" o:button=\"t\" fillcolor=\"buttonFace [67]\" strokecolor=\"windowText [64]\" o:insetmode=\"auto\">" +
            "<v:fill color2=\"buttonFace [67]\" o:detectmouseclick=\"t\"/><o:lock v:ext=\"edit\" rotation=\"t\"/><v:textbox style=\"mso-direction-alt:auto\" o:singleclick=\"f\"><div style=\"text-align:center\"><font face=\"Calibri\" size=\"220\" color=\"#000000\">Update index</font></div></v:textbox>" +
            "<x:ClientData ObjectType=\"Button\"><x:Anchor>7, 0, 1, 0, 9, 0, 3, 0</x:Anchor><x:PrintObject>False</x:PrintObject><x:AutoFill>False</x:AutoFill><x:FmlaMacro>[0]!UpdateIndex</x:FmlaMacro><x:TextHAlign>Center</x:TextHAlign><x:TextVAlign>Center</x:TextVAlign></x:ClientData></v:shape>" +
            "</xml>");

        yield return ("xl/ctrlProps/ctrlProp1.xml", Decl +
            "<formControlPr xmlns=\"http://schemas.microsoft.com/office/spreadsheetml/2009/9/main\" objectType=\"Button\" lockText=\"1\"/>");

        yield return ("xl/tables/table1.xml", Decl +
            $"<table {Ns} id=\"1\" name=\"SheetIndex\" displayName=\"SheetIndex\" ref=\"A1:F7\" totalsRowShown=\"0\">" +
            "<autoFilter ref=\"A1:F7\"/><tableColumns count=\"6\">" +
            "<tableColumn id=\"1\" name=\"Sheet No.\"/><tableColumn id=\"2\" name=\"Sheet Title\"/><tableColumn id=\"3\" name=\"Rev\"/>" +
            "<tableColumn id=\"4\" name=\"Status\"/><tableColumn id=\"5\" name=\"Rev x2\"><calculatedColumnFormula>IF(SheetIndex[[#This Row],[Sheet No.]]=\"\",\"\",SheetIndex[[#This Row],[Rev]]*2)</calculatedColumnFormula></tableColumn>" +
            "<tableColumn id=\"6\" name=\"Issued\"/></tableColumns>" +
            "<tableStyleInfo name=\"TableStyleMedium2\" showFirstColumn=\"0\" showLastColumn=\"0\" showRowStripes=\"1\" showColumnStripes=\"0\"/></table>");
    }
}
