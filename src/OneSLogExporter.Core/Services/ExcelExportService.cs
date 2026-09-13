using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;
using OneSLogExporter.Core.Models;

namespace OneSLogExporter.Core.Services;

/// <summary>
/// Высокоскоростной генератор документов Microsoft Excel (.xlsx / OpenXML Spreadsheet)
/// без внешних зависимостей на базе потокового System.IO.Compression.ZipArchive.
/// </summary>
public static class ExcelExportService
{
    /// <summary>
    /// Экспорт коллекции записей Технологического Журнала (ТЖ) в файл Microsoft Excel (.xlsx).
    /// </summary>
    public static void ExportTechLogToExcel(string filePath, IReadOnlyList<TechLogDoc> docs)
    {
        string[] headers = ["Дата и Время", "Событие", "Статус", "Длительность (мс)", "Длительность", "Процесс", "PID", "SPID", "Поток (OSThread)", "Пользователь", "Приложение", "Контекст / SQL"];

        List<string[]> rows = new(docs.Count);
        foreach (var doc in docs)
        {
            rows.Add([
                doc.DateFormatted,
                doc.Event ?? "",
                doc.ExecutionStatus,
                doc.DurationMs.ToString(),
                doc.DurationFormatted,
                doc.ProcessName ?? "",
                doc.ProcessId ?? "",
                doc.Spid ?? "",
                doc.OSThread ?? "",
                doc.User ?? "",
                doc.App ?? "",
                doc.Context ?? doc.Sql ?? doc.Descr ?? ""
            ]);
        }

        GenerateXlsx(filePath, "Технологический Журнал 1С", headers, rows);
    }

    /// <summary>
    /// Экспорт коллекции записей Журнала Регистрации (ЖР) в файл Microsoft Excel (.xlsx).
    /// </summary>
    public static void ExportEventLogToExcel(string filePath, IReadOnlyList<EventLogDoc> docs)
    {
        string[] headers = ["Дата и Время", "Событие", "Важность", "Пользователь", "Приложение", "Метаданные", "Файл", "Размер файла", "Сеанс", "Транзакция", "Комментарий"];

        List<string[]> rows = new(docs.Count);
        foreach (var doc in docs)
        {
            rows.Add([
                doc.DateFormatted,
                doc.Event ?? "",
                doc.Importance ?? "",
                doc.User ?? "",
                doc.App ?? "",
                doc.Meta ?? "",
                doc.FileName ?? "",
                doc.FileSizeFormatted ?? "",
                doc.Session ?? "",
                doc.Tran ?? "",
                doc.Comment ?? ""
            ]);
        }

        GenerateXlsx(filePath, "Журнал Регистрации 1С", headers, rows);
    }

    /// <summary>
    /// Создание валидного OpenXML .xlsx архива с форматированием стилей и таблицы.
    /// </summary>
    public static void GenerateXlsx(string filePath, string sheetName, string[] headers, IReadOnlyList<string[]> rows)
    {
        var tempFile = filePath + ".tmp";
        if (File.Exists(tempFile)) File.Delete(tempFile);

        using (var zipStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
        {
            // 1. [Content_Types].xml
            CreateEntry(archive, "[Content_Types].xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                  <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
                </Types>
                """);

            // 2. _rels/.rels
            CreateEntry(archive, "_rels/.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """);

            // 3. xl/_rels/workbook.xml.rels
            CreateEntry(archive, "xl/_rels/workbook.xml.rels", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
                </Relationships>
                """);

            // 4. xl/workbook.xml
            var safeSheetName = SecurityElement.Escape(sheetName) ?? "Sheet1";
            CreateEntry(archive, "xl/workbook.xml", $"""
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>
                    <sheet name="{safeSheetName}" sheetId="1" r:id="rId1"/>
                  </sheets>
                </workbook>
                """);

            // 5. xl/styles.xml (Темно-синяя брендовая шапка, рамки и форматы)
            CreateEntry(archive, "xl/styles.xml", """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <fonts count="2">
                    <font><sz val="10"/><name val="Segoe UI"/></font>
                    <font><b/><sz val="10.5"/><color rgb="FFFFFFFF"/><name val="Segoe UI"/></font>
                  </fonts>
                  <fills count="3">
                    <fill><patternFill patternType="none"/></fill>
                    <fill><patternFill patternType="gray125"/></fill>
                    <fill><patternFill patternType="solid"><fgColor rgb="FF1E3A8A"/></patternFill></fill>
                  </fills>
                  <borders count="2">
                    <border><left/><right/><top/><bottom/><diagonal/></border>
                    <border>
                      <left style="thin"><color rgb="FFCBD5E1"/></left>
                      <right style="thin"><color rgb="FFCBD5E1"/></right>
                      <top style="thin"><color rgb="FFCBD5E1"/></top>
                      <bottom style="thin"><color rgb="FFCBD5E1"/></bottom>
                    </border>
                  </borders>
                  <cellStyleXfs count="1">
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
                  </cellStyleXfs>
                  <cellXfs count="2">
                    <xf numFmtId="0" fontId="0" fillId="0" borderId="1" xfId="0" applyBorder="1"/>
                    <xf numFmtId="0" fontId="1" fillId="2" borderId="1" xfId="0" applyFont="1" applyFill="1" applyBorder="1" applyAlignment="1">
                      <alignment horizontal="center" vertical="center" wrapText="1"/>
                    </xf>
                  </cellXfs>
                </styleSheet>
                """);

            // 6. xl/worksheets/sheet1.xml
            var sheetEntry = archive.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Fastest);
            using (var sheetStream = sheetEntry.Open())
            using (var writer = XmlWriter.Create(sheetStream, new XmlWriterSettings { Encoding = Encoding.UTF8, Indent = false }))
            {
                writer.WriteStartDocument(true);
                writer.WriteStartElement("worksheet", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
                writer.WriteAttributeString("xmlns", "r", null, "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

                // Определение колонок
                writer.WriteStartElement("cols");
                for (int colIdx = 0; colIdx < headers.Length; colIdx++)
                {
                    writer.WriteStartElement("col");
                    writer.WriteAttributeString("min", (colIdx + 1).ToString());
                    writer.WriteAttributeString("max", (colIdx + 1).ToString());
                    var width = colIdx switch
                    {
                        0 => "22", // Дата
                        1 => "16", // Событие
                        2 => "14", // Статус
                        3 => "16", // Длительность мс
                        4 => "16", // Длительность
                        11 => "60", // Контекст / SQL
                        _ => "20"
                    };
                    writer.WriteAttributeString("width", width);
                    writer.WriteAttributeString("customWidth", "1");
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); // cols

                // Данные строк
                writer.WriteStartElement("sheetData");

                // 1. Строка заголовков (row 1, style 1)
                writer.WriteStartElement("row");
                writer.WriteAttributeString("r", "1");
                writer.WriteAttributeString("ht", "26");
                writer.WriteAttributeString("customHeight", "1");

                for (int colIdx = 0; colIdx < headers.Length; colIdx++)
                {
                    var cellRef = GetCellRef(colIdx, 1);
                    writer.WriteStartElement("c");
                    writer.WriteAttributeString("r", cellRef);
                    writer.WriteAttributeString("t", "inlineStr");
                    writer.WriteAttributeString("s", "1"); // Заголовочный стиль с синей заливкой

                    writer.WriteStartElement("is");
                    writer.WriteElementString("t", headers[colIdx]);
                    writer.WriteEndElement();

                    writer.WriteEndElement(); // c
                }
                writer.WriteEndElement(); // row 1

                // 2. Строки данных
                for (int rowIdx = 0; rowIdx < rows.Count; rowIdx++)
                {
                    int excelRowNum = rowIdx + 2;
                    var rowData = rows[rowIdx];

                    writer.WriteStartElement("row");
                    writer.WriteAttributeString("r", excelRowNum.ToString());

                    for (int colIdx = 0; colIdx < rowData.Length; colIdx++)
                    {
                        var cellRef = GetCellRef(colIdx, excelRowNum);
                        var val = rowData[colIdx] ?? string.Empty;

                        writer.WriteStartElement("c");
                        writer.WriteAttributeString("r", cellRef);
                        writer.WriteAttributeString("t", "inlineStr");
                        writer.WriteAttributeString("s", "0");

                        writer.WriteStartElement("is");
                        writer.WriteElementString("t", SanitizeXml(val));
                        writer.WriteEndElement();

                        writer.WriteEndElement(); // c
                    }

                    writer.WriteEndElement(); // row
                }

                writer.WriteEndElement(); // sheetData
                writer.WriteEndElement(); // worksheet
                writer.WriteEndDocument();
            }
        }

        if (File.Exists(filePath)) File.Delete(filePath);
        File.Move(tempFile, filePath);
    }

    private static void CreateEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Encoding.UTF8);
        writer.Write(content.Trim());
    }

    private static string GetCellRef(int colIndex, int rowIndex)
    {
        var colName = "";
        int dividend = colIndex + 1;
        while (dividend > 0)
        {
            int modulo = (dividend - 1) % 26;
            colName = Convert.ToChar(65 + modulo) + colName;
            dividend = (dividend - modulo) / 26;
        }
        return $"{colName}{rowIndex}";
    }

    private static string SanitizeXml(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            if (XmlConvert.IsXmlChar(c))
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
