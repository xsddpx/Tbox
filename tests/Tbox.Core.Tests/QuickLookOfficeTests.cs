using System.IO.Compression;
using Tbox.Core;
using Xunit;

namespace Tbox.Core.Tests;

public sealed class QuickLookOfficeTests
{
    private const string Main = "http://schemas.openxmlformats.org/";

    [Fact]
    public void ClassifiesOfficeAfterBuiltInsAndHandlers()
    {
        Assert.Equal(QuickLookKind.Office, QuickLookPolicy.Classify("a.docx", false));
        Assert.Equal(QuickLookKind.Office, QuickLookPolicy.Classify("a.XLSX", false));
        Assert.Equal(QuickLookKind.Unsupported, QuickLookPolicy.Classify("a.doc", false));
        // A registered handler wins over Tbox's own reader, and also covers formats Tbox cannot read.
        Assert.Equal(QuickLookKind.PreviewHandler, QuickLookPolicy.Classify("a.docx", false, null, e => e == ".docx"));
        Assert.Equal(QuickLookKind.PreviewHandler, QuickLookPolicy.Classify("a.msg", false, null, e => e == ".msg"));
        // Built-in previews come first, as in Peek.
        Assert.Equal(QuickLookKind.Pdf, QuickLookPolicy.Classify("a.pdf", false, null, _ => true));
    }

    [Fact]
    public void WordReadsParagraphsTabsBreaksAndTheThumbnail()
    {
        var path = Package(".docx", zip =>
        {
            Write(zip, "word/document.xml", $"<w:document xmlns:w='{Main}wordprocessingml/2006/main'><w:body>" +
                "<w:p/><w:p><w:r><w:t>标题</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>a</w:t><w:tab/><w:t>b</w:t><w:br/><w:t>c</w:t></w:r></w:p><w:p/></w:body></w:document>");
            using var thumb = zip.CreateEntry("docProps/thumbnail.jpeg").Open();
            thumb.Write([0xFF, 0xD8, 0xFF]);
        });
        try
        {
            var office = QuickLookOffice.Read(path, CancellationToken.None);
            Assert.Equal(QuickLookOfficeKind.Word, office.Kind);
            Assert.Equal(["标题", "a\tb\nc"], office.Paragraphs);
            Assert.Equal([0xFF, 0xD8, 0xFF], office.Thumbnail);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ExcelReadsTheFirstVisibleSheetPlacingCellsByReference()
    {
        var path = Package(".xlsx", zip =>
        {
            Write(zip, "xl/workbook.xml", $"<workbook xmlns='{Main}spreadsheetml/2006/main' xmlns:r='{Main}officeDocument/2006/relationships'><sheets>" +
                "<sheet name='隐藏' sheetId='1' state='hidden' r:id='rId1'/><sheet name='数据' sheetId='2' r:id='rId2'/></sheets></workbook>");
            Write(zip, "xl/_rels/workbook.xml.rels", $"<Relationships xmlns='{Main}package/2006/relationships'>" +
                "<Relationship Id='rId1' Target='worksheets/sheet1.xml'/><Relationship Id='rId2' Target='worksheets/sheet2.xml'/></Relationships>");
            Write(zip, "xl/sharedStrings.xml", $"<sst xmlns='{Main}spreadsheetml/2006/main'><si><t>名称</t></si><si><r><t>值</t></r><r><t>A</t></r></si></sst>");
            Write(zip, "xl/worksheets/sheet2.xml", $"<worksheet xmlns='{Main}spreadsheetml/2006/main'><sheetData>" +
                "<row r='1'><c r='A1' t='s'><v>0</v></c><c r='C1' t='s'><v>1</v></c></row>" +
                "<row r='3'><c r='A3'><v>42</v></c><c r='B3' t='b'><v>1</v></c><c r='C3' t='inlineStr'><is><t>行内</t></is></c></row></sheetData></worksheet>");
        });
        try
        {
            var office = QuickLookOffice.Read(path, CancellationToken.None);
            Assert.Equal("数据", office.SheetName);
            Assert.Equal(3, office.Rows.Count);
            Assert.Equal(["名称", "", "值A"], office.Rows[0]);
            Assert.Empty(office.Rows[1]);
            Assert.Equal(["42", "TRUE", "行内"], office.Rows[2]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PowerPointReadsSlidesInPresentationOrder()
    {
        var path = Package(".pptx", zip =>
        {
            Write(zip, "ppt/presentation.xml", $"<p:presentation xmlns:p='{Main}presentationml/2006/main' xmlns:r='{Main}officeDocument/2006/relationships'>" +
                "<p:sldIdLst><p:sldId id='257' r:id='rId3'/><p:sldId id='256' r:id='rId2'/></p:sldIdLst></p:presentation>");
            Write(zip, "ppt/_rels/presentation.xml.rels", $"<Relationships xmlns='{Main}package/2006/relationships'>" +
                "<Relationship Id='rId2' Target='slides/slide1.xml'/><Relationship Id='rId3' Target='/ppt/slides/slide2.xml'/></Relationships>");
            string Slide(string text) => $"<p:sld xmlns:p='{Main}presentationml/2006/main' xmlns:a='{Main}drawingml/2006/main'><a:p><a:r><a:t>{text}</a:t></a:r></a:p><a:p/></p:sld>";
            Write(zip, "ppt/slides/slide1.xml", Slide("一"));
            Write(zip, "ppt/slides/slide2.xml", Slide("二"));
        });
        try
        {
            var office = QuickLookOffice.Read(path, CancellationToken.None);
            Assert.Equal(["二", "一"], office.Slides.Select(slide => string.Join("|", slide)));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RefusesDocumentTypeDefinitions()
    {
        var path = Package(".docx", zip => Write(zip, "word/document.xml",
            "<!DOCTYPE d [<!ENTITY a 'aaaa'>]><w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'>&a;</w:document>"));
        try { Assert.Throws<System.Xml.XmlException>(() => QuickLookOffice.Read(path, CancellationToken.None)); }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("Z9", 25)]
    [InlineData("AA10", 26)]
    [InlineData("xfd1", 16383)]
    [InlineData("12", null)]
    public void ColumnFromReference(string reference, int? expected) => Assert.Equal(expected, QuickLookOffice.Column(reference));

    private static string Package(string extension, Action<ZipArchive> fill)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tbox-office-{Guid.NewGuid():N}{extension}");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        fill(zip);
        return path;
    }

    private static void Write(ZipArchive zip, string name, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(xml);
    }
}
