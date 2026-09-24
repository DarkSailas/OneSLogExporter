using FluentAssertions;
using OneSLogExporter.Core.Models;
using OneSLogExporter.Core.Parsers;
using System.Text;
using Xunit;

namespace OneSLogExporter.Tests;

/// <summary>
/// Автономные юнит-тесты потокового парсинга Журнала Регистрации 1С.
/// </summary>
public sealed class EventLogParserTests
{
    private const string SampleLgfContent = @"{1,
{1,0,""Администратор"",1},
{2,""WS-TEST-01"",10},
{3,""HTTPServiceConnection"",1},
{4,""_$Session$_.Start"",1},
{5,""Справочник.Номенклатура"",1},
{6,""SRV-1C-01"",1},
{7,1560,3}
}";

    private const string SampleLgpEntry = @"{20260817123045,N,
{0,0},1,1,1,1,
I,
""Успешный вход в систему""""1С:Предприятие"""" с клиента"",
1,
""ДанныеСеанса"",
1,
1,
1,
1,
1,
""12345""
},";

    [Fact]
    public async Task ParseDictionaryAsync_WithTempFile_ShouldLoadEntries()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLgfContent);

            var dict = await EventLogParser.ParseDictionaryAsync(tempFile);

            dict.Should().NotBeNull();
            dict.Users.Should().NotBeEmpty();
            dict.Computers.Should().NotBeEmpty();
            dict.Apps.Should().NotBeEmpty();
            dict.Events.Should().NotBeEmpty();
            dict.Metas.Should().NotBeEmpty();
            dict.Servers.Should().NotBeEmpty();
            dict.Ports.Should().NotBeEmpty();

            dict.Users.Values.Should().Contain("Администратор");
            dict.Computers["10"].Should().Be("WS-TEST-01");
            dict.Apps.Values.Should().Contain("HTTPServiceConnection");
            dict.Events.Values.Should().Contain("_$Session$_.Start");
            dict.Metas.Values.Should().Contain("Справочник.Номенклатура");
            dict.Servers["1"].Should().Be("SRV-1C-01");
            dict.Ports["3"].Should().Be("1560");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseDictionaryAsync_WithMultilineEntries_ShouldLoadAllUsersAndMetadata()
    {
        var lgf = """
        1CV8LOG(ver 2.0)
        a1b2c3d4-e311-4ee2-8582-7a2a46a59363

        {1,071523a4-516f-4fce-ba4b-0d11ab7a1893,"",1},
        {1,
        4777e112-749e-4b6c-8e42-1e967a55c276,
        "Робот Обмена",
        8
        },
        {1,"e1b6d196-857c-47fc-8f78-b11c38521c7d","Кассир 1",15},
        {5,
        11111111-1111-1111-1111-111111111111,
        "Документ.ЗаказПокупателя",
        42},
        {2,"POS-TERMINAL-01",4}
        """;

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, lgf);

            var dict = await EventLogParser.ParseDictionaryAsync(tempFile);

            dict.Users["1"].Should().Be("<Не указан>");
            dict.Users["8"].Should().Be("Робот Обмена");
            dict.Users["15"].Should().Be("Кассир 1");
            dict.Metas["42"].Should().Be("Документ.ЗаказПокупателя");
            dict.Computers["4"].Should().Be("POS-TERMINAL-01");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseDictionaryAsync_WithZeroPaddedEntries_ShouldResolveAll()
    {
        var lgf = """
        1CV8LOG(ver 2.0)
        {1,0,"ИвановИИ",2},
        {2,0,"server-node-01",7},
        {3,0,"1CV8C",3},
        {3,"RAS",1},
        {4,0,"_$Session$_.Start",17},
        {4,"_$Transaction$_.Begin",3},
        {4,"_$User$_.AuthenticationLock",25},
        {5,0,"РегистрСведений.СведенияОПользователях",1}
        """;

        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, lgf);
            var dict = await EventLogParser.ParseDictionaryAsync(tempFile);

            dict.Users["2"].Should().Be("ИвановИИ");
            dict.Computers["7"].Should().Be("server-node-01");
            dict.Apps["3"].Should().Be("1CV8C");
            dict.Apps["1"].Should().Be("RAS");
            dict.Events["17"].Should().Be("_$Session$_.Start");
            dict.Events["3"].Should().Be("_$Transaction$_.Begin");
            dict.Events["25"].Should().Be("_$User$_.AuthenticationLock");
            dict.Metas["1"].Should().Be("РегистрСведений.СведенияОПользователях");

            // Test ParseEntry using this dictionary
            var sampleLog = """
            {20260914103559,C,{0,0},2,7,3,1,17,I,"",1,"",""
            },
            """;
            var doc = EventLogParser.ParseEntry(sampleLog, dict);
            doc.Should().NotBeNull();
            doc!.User.Should().Be("ИвановИИ");
            doc.Computer.Should().Be("server-node-01");
            doc.App.Should().Be("1CV8C");
            doc.AppTypeName.Should().Be("Тонкий клиент");
            doc.Event.Should().Be("Сеанс. Начало");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseEntry_WithCompositeMetadata_ShouldResolveMultipleNames()
    {
        var dict = new LgfDictionary();
        dict.Metas["42"] = "Документ.ЗаказПокупателя";
        dict.Metas["105"] = "Справочник.Номенклатура";

        var entry = """
        {20260913123253,N,
        {0,0},1,1,1,1,
        I,
        "Комментарий",
        {42,105},
        "",
        ""
        },
        """;

        var doc = EventLogParser.ParseEntry(entry, dict);
        doc.Should().NotBeNull();
        doc!.Meta.Should().Be("Документ.ЗаказПокупателя, Справочник.Номенклатура");
    }

    [Fact]
    public void ParseEntry_ShouldExtractLgpFieldsCorrectly()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "Администратор";
        dict.Apps["1"] = "HTTPServiceConnection";
        dict.Events["1"] = "_$Session$_.Start";
        dict.Metas["1"] = "Справочник.Номенклатура";

        var doc = EventLogParser.ParseEntry(SampleLgpEntry, dict);

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("Сеанс. Начало");
        doc.User.Should().Be("Администратор");
        doc.App.Should().Be("HTTPServiceConnection");
        doc.Importance.Should().Be("Информация");
        doc.Meta.Should().Be("Справочник.Номенклатура");
        doc.Comment.Should().Be("Успешный вход в систему\"1С:Предприятие\" с клиента");
        doc.Data.Should().Be("ДанныеСеанса");
        doc.Session.Should().Be("12345");
        doc.DateFormatted.Should().Be("2026-08-17 12:30:45");
    }

    [Fact]
    public void ParseEntry_WithOneC83Format_ShouldExtractAllMetadataAndCommentCorrectly()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "ТестовыйПользователь";
        dict.Computers["2"] = "WS-TEST-01";
        dict.Servers["1"] = "SRV-1C-01";
        dict.Ports["1"] = "1541";
        dict.Apps["1"] = "1CV8C";
        dict.Events["3"] = "_$Data$_.Post";
        dict.Metas["45"] = "Документ.РеализацияТоваровУслуг";

        // {Date, TranStatus, TranID, User, Computer, App, Connection, Event, Importance, Comment, Metadata, Data, DataPres, Server, MainPort, AuxPort, Session}
        var rawEntry = @"{20260831000441,C,{2456209139090,9568c},1,2,1,40499,3,I,""Выполнено, Получение данных"",45,0,""Реализация №123"",1,1,1,19571}";

        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.User.Should().Be("ТестовыйПользователь");
        doc.Computer.Should().Be("WS-TEST-01");
        doc.Server.Should().Be("SRV-1C-01");
        doc.Port.Should().Be("1541");
        doc.Connection.Should().Be("40499");
        doc.App.Should().Be("1CV8C");
        doc.AppTypeName.Should().Be("Тонкий клиент");
        doc.Tran.Should().Be("C(2456209139090,9568c)");
        doc.TranStatusText.Should().Be("Зафиксирована");
        doc.Event.Should().Be("Данные. Проведение");
        doc.Importance.Should().Be("Информация");
        doc.Comment.Should().Be("Выполнено, Получение данных");
        doc.Meta.Should().Be("Документ.РеализацияТоваровУслуг");
        doc.DataPresentation.Should().Be("Реализация №123");
        doc.Session.Should().Be("19571");
    }

    [Fact]
    public void ParseEntry_WithUncommittedTransactionAndUndefinedData_ShouldDecodeCorrectly()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "ТестовыйПользователь";
        dict.Computers["10"] = "WS-TERMINAL-10";
        dict.Servers["1"] = "APP-SRV-01";
        dict.Ports["3"] = "1560";
        dict.Apps["1"] = "1CV8C";
        dict.Events["3"] = "_$Data$_.Update";
        dict.Metas["45"] = "РегистрСведений.ДополнительныеСведения";

        var rawEntry = @"{20260817000203,U,{2455f2d13bfe0,14e4cecd69},1,10,1,20741,3,I,"""",45,{""U""},"""",1,3,1,30612}";

        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.User.Should().Be("ТестовыйПользователь");
        doc.Computer.Should().Be("WS-TERMINAL-10");
        doc.Server.Should().Be("APP-SRV-01");
        doc.Port.Should().Be("1560");
        doc.Connection.Should().Be("20741");
        doc.App.Should().Be("1CV8C");
        doc.AppTypeName.Should().Be("Тонкий клиент");
        doc.Tran.Should().Be("U(2455f2d13bfe0,14e4cecd69)");
        doc.TranStatusText.Should().Be("В процессе");
        doc.Event.Should().Be("Данные. Изменение");
        doc.Importance.Should().Be("Информация");
        doc.Meta.Should().Be("РегистрСведений.ДополнительныеСведения");
        doc.Data.Should().Be("(без объектной ссылки)");
        doc.DataPresentation.Should().Be("Запись регистра: РегистрСведений.ДополнительныеСведения");
        doc.Session.Should().Be("30612");
    }

    [Fact]
    public void ParseEntry_TransactionBeginWithoutMetadata_ShouldHaveEmptyData()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "ТестовыйПользователь";
        dict.Computers["1"] = "WS-TEST";
        dict.Apps["1"] = "1CV8C";
        dict.Events["1"] = "_$Transaction$_.Begin";

        var rawEntry = @"{20260810000134,C,{2455dcf7f8fe0,6f33c},1,1,1,39470,1,I,"""",0,{""U""},"""",1,1,1,44055}";

        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("Транзакция. Начало");
        doc.Meta.Should().BeEmpty();
        doc.Data.Should().BeEmpty();
        doc.DataPresentation.Should().BeEmpty();
    }

    [Fact]
    public void ParseEntry_AuthenticationError_ShouldDecodeAccountAndAlias()
    {
        var dict = new LgfDictionary();
        dict.Computers["1"] = "TEST-SRV-01";
        dict.Apps["2"] = "RAS";
        dict.Events["1"] = "_$Session$_.AuthenticationError";
        dict.Servers["1"] = "TEST-APP-01";
        dict.Ports["1"] = "1545";

        var rawEntry = @"{20260831000326,N,
{0,0},0,1,2,42183,1,I,"""",0,
{""P"", {1, {""S"",""TESTDOMAIN\test-account$""}}}, """", 1, 1, 1, 0}";

        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("Сеанс. Ошибка аутентификации");
        doc.User.Should().Be("TESTDOMAIN\\test-account$");
        doc.App.Should().Be("RAS");
        doc.AppTypeName.Should().Be("Сервер администрирования (RAS)");
        doc.Data.Should().Be("TESTDOMAIN\\test-account$");
        doc.DataPresentation.Should().Be("Пользователь ОС: TESTDOMAIN\\test-account$");
        doc.Computer.Should().Be("TEST-SRV-01");
        doc.Server.Should().Be("TEST-APP-01");
        doc.Port.Should().Be("1545");
        doc.Connection.Should().Be("42183");
    }

    [Fact]
    public async Task ParseLogAsync_WithProgressReporting_ShouldStreamAllDocuments()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "EventLogTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "20260817000000.lgp");

        var dict = new LgfDictionary();
        dict.Users["1"] = "Администратор";
        dict.Events["1"] = "_$Session$_.Start";

        try
        {
            await File.WriteAllTextAsync(tempFile, SampleLgpEntry + "\n" + SampleLgpEntry + "\n");

            var progressReports = 0;
            var progress = new Progress<EventLogParser.LogReadProgress>(_ => Interlocked.Increment(ref progressReports));

            var docs = new List<EventLogDoc>();
            await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, progress))
            {
                docs.Add(doc);
            }

            docs.Should().HaveCount(2);
            docs[0].Event.Should().Be("Сеанс. Начало");
            docs[0].User.Should().Be("Администратор");
            docs[0].FileName.Should().Be("20260817000000.lgp");
            docs[0].FileSize.Should().BeGreaterThan(0);
            docs[0].FileSizeFormatted.Should().NotBeNullOrEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(10485760, "10.00 MB")]
    [InlineData(10737418240, "10.00 GB")]
    public void FormatFileSize_ShouldFormatCorrectly(long bytes, string expected)
    {
        EventLogParser.FormatFileSize(bytes).Should().Be(expected);
    }

    [Fact]
    public void ParseEntry_WithReferenceDataAndMissingCode_ShouldCleanDataAndPresentation()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "ТестовыйПользователь";
        dict.Computers["1"] = "ws-term-01";
        dict.Apps["1"] = "1CV8C";
        dict.Events["1"] = "_$Data$_.Update";
        dict.Metas["9345"] = "Справочник.Номенклатура";
        dict.Servers["1"] = "app-srv-01";
        dict.Ports["1"] = "1570";

        var rawEntry = @"{20260817000034,U,{2455f2d13bfe0,14e4cecd69},1,1,1,20741,1,I,"""",9345,{""R"",9345:ab7a005056bbe0b411f161f9d05bdec1},""<?>; Товар №12345; Код-001; "",1,1,1,30612}";

        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("Данные. Изменение");
        doc.Meta.Should().Be("Справочник.Номенклатура");
        doc.Data.Should().Be("Ссылка: ab7a0050-56bb-e0b4-11f1-61f9d05bdec1");
        doc.DataPresentation.Should().Be("Товар №12345; Код-001");
        doc.Tran.Should().Be("U(2455f2d13bfe0,14e4cecd69)");
        doc.TranStatusText.Should().Be("В процессе");
    }

    [Fact]
    public void ParseEntry_WithReferenceAndArtifactsInPresentation_ShouldFormatCanonicalGuidAndCleanText()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "ТестовыйПользователь";
        dict.Computers["1"] = "ws-term-01";
        dict.Apps["1"] = "1CV8C";
        dict.Events["1"] = "_$Data$_.Update";
        dict.Metas["9345"] = "Справочник.Номенклатура";
        dict.Servers["1"] = "app-srv-01";
        dict.Ports["1"] = "1570";

        var rawEntry = @"{20260817000245,U,{2455f2d13bfe0,14e4cecd69},1,1,1,20741,1,I,"""",9345,{""R"",9345:ab7a005056bbe0b411f15039d5c3f43d},""<?>; Товар №67890; Код-002; "",1,1,1,30612}";

        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.Event.Should().Be("Данные. Изменение");
        doc.Meta.Should().Be("Справочник.Номенклатура");
        doc.Data.Should().Be("Ссылка: ab7a0050-56bb-e0b4-11f1-5039d5c3f43d");
        doc.DataPresentation.Should().Be("Товар №67890; Код-002");
    }

    [Theory]
    [InlineData(@"{""B"",1}", "Истина")]
    [InlineData(@"{""B"",0}", "Ложь")]
    [InlineData(@"{""N"",42.5}", "42.5")]
    [InlineData(@"{""S"",""ТестоваяСтрока""}", "ТестоваяСтрока")]
    [InlineData(@"{""D"",20260817143000}", "2026-08-17 14:30:00")]
    public void ParseEntry_WithPrimitiveDataTypes_ShouldDecodeProperly(string rawData, string expectedData)
    {
        var dict = new LgfDictionary();
        dict.Events["1"] = "_$Data$_.Update";
        dict.Metas["10"] = "Справочник.Контрагенты";

        var rawEntry = $@"{{20260817000000,N,{{0,0}},0,0,1,0,1,I,"""",10,{rawData},"""",0,0,0,0}}";
        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.Data.Should().Be(expectedData);
    }

    [Fact]
    public void TryFindEntryDateInBuffer_ShouldFindEntryDateAndOffset()
    {
        var text = "Some noise before entry...\r\n{20260903154520,N,\r\n{0,0},1,1,1,1,I,\"Comment\",1,\"Data\",1,1,1,1,1,\"12345\"},";
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);

        var success = EventLogParser.TryFindEntryDateInBuffer(bytes, out var date, out var offset);

        success.Should().BeTrue();
        offset.Should().Be(28); // index of '{' after \r\n
        date.Should().Be(new DateTime(2026, 9, 3, 15, 45, 20, DateTimeKind.Utc));
    }

    [Fact]
    public async Task FindFastStartOffsetAsync_WithMultiGigabyteLogSimulation_ShouldFindOffsetNearTarget()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"fast_seek_test_{Guid.NewGuid():N}.lgp");
        try
        {
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Формируем 12 МБ записей 2026-08-31
                var entryDay1 = System.Text.Encoding.UTF8.GetBytes("{20260831120000,N,{0,0},0,0,1,0,1,I,\"aug31\",0,\"\",0,0,0,0}\r\n");
                var chunkDay1 = new byte[entryDay1.Length * 500];
                for (var i = 0; i < 500; i++)
                    Buffer.BlockCopy(entryDay1, 0, chunkDay1, i * entryDay1.Length, entryDay1.Length);

                for (var c = 0; c < 400; c++) // ~12 MB
                {
                    await fs.WriteAsync(chunkDay1);
                }

                // Формируем 10 МБ записей 2026-09-02
                var entryDay2 = System.Text.Encoding.UTF8.GetBytes("{20260902120000,N,{0,0},0,0,1,0,1,I,\"sep02\",0,\"\",0,0,0,0}\r\n");
                var chunkDay2 = new byte[entryDay2.Length * 500];
                for (var i = 0; i < 500; i++)
                    Buffer.BlockCopy(entryDay2, 0, chunkDay2, i * entryDay2.Length, entryDay2.Length);

                for (var c = 0; c < 350; c++) // ~10.5 MB
                {
                    await fs.WriteAsync(chunkDay2);
                }

                // Формируем 3 МБ записей 2026-09-03
                var entryDay3 = System.Text.Encoding.UTF8.GetBytes("{20260903080000,N,{0,0},0,0,1,0,1,I,\"sep03\",0,\"\",0,0,0,0}\r\n");
                var chunkDay3 = new byte[entryDay3.Length * 500];
                for (var i = 0; i < 500; i++)
                    Buffer.BlockCopy(entryDay3, 0, chunkDay3, i * entryDay3.Length, entryDay3.Length);

                for (var c = 0; c < 100; c++) // ~3 MB
                {
                    await fs.WriteAsync(chunkDay3);
                }
            }

            await using var readFs = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            var target = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);

            var foundOffset = await EventLogParser.FindFastStartOffsetAsync(readFs, target);

            // Смещение должно перепрыгнуть день 1 (12 МБ) и указать на область записей перед 2026-09-03
            foundOffset.Should().BeGreaterThan(12 * 1024 * 1024);
            foundOffset.Should().BeLessThan(readFs.Length);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseLogAsync_WithSingleDayDateFilter_ShouldReturnOnlyTargetDateRecords()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"date_filter_test_{Guid.NewGuid():N}.lgp");
        var dict = new LgfDictionary();
        dict.Events["1"] = "_$Data$_.Update";

        try
        {
            var content = "{20260902235000,N,{0,0},0,0,1,0,1,I,\"day2\",0,\"\",0,0,0,0}\r\n" +
                          "{20260903083000,N,{0,0},0,0,1,0,1,I,\"day3_morning\",0,\"\",0,0,0,0}\r\n" +
                          "{20260903184500,N,{0,0},0,0,1,0,1,I,\"day3_evening\",0,\"\",0,0,0,0}\r\n" +
                          "{20260904001000,N,{0,0},0,0,1,0,1,I,\"day4\",0,\"\",0,0,0,0}\r\n";

            await File.WriteAllTextAsync(tempFile, content);

            var filterDate = new DateTime(2026, 9, 3);
            var docs = new List<EventLogDoc>();
            await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, null, default, filterDate, filterDate))
            {
                docs.Add(doc);
            }

            docs.Should().HaveCount(2);
            docs[0].Comment.Should().Be("day3_morning");
            docs[1].Comment.Should().Be("day3_evening");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseLogAsync_WithFastSeek_ShouldReportSkippedBytesInLogReadProgress()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"fast_seek_progress_test_{Guid.NewGuid():N}.lgp");
        var dict = new LgfDictionary();
        dict.Events["1"] = "_$Data$_.Update";

        try
        {
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                // Создаем 21 МБ записей 2026-08-31
                var entryDay1 = System.Text.Encoding.UTF8.GetBytes("{20260831120000,N,{0,0},0,0,1,0,1,I,\"aug31\",0,\"\",0,0,0,0}\r\n");
                var chunkDay1 = new byte[entryDay1.Length * 500];
                for (var i = 0; i < 500; i++)
                    Buffer.BlockCopy(entryDay1, 0, chunkDay1, i * entryDay1.Length, entryDay1.Length);

                for (var c = 0; c < 800; c++) // ~23.6 MB
                {
                    await fs.WriteAsync(chunkDay1);
                }

                // Записи 2026-09-03
                var entryDay2 = System.Text.Encoding.UTF8.GetBytes("{20260903100000,N,{0,0},0,0,1,0,1,I,\"sep03_target\",0,\"\",0,0,0,0}\r\n");
                await fs.WriteAsync(entryDay2);
            }

            var filterDate = new DateTime(2026, 9, 3);
            var lastSkipped = 0L;
            var progress = new SynchronousProgress<EventLogParser.LogReadProgress>(p =>
            {
                if (p.SkippedBytes > 0)
                    Interlocked.Exchange(ref lastSkipped, p.SkippedBytes);
            });

            var docs = new List<EventLogDoc>();
            await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, progress, default, filterDate, filterDate))
            {
                docs.Add(doc);
            }

            docs.Should().HaveCount(1);
            docs[0].Comment.Should().Be("sep03_target");
            lastSkipped.Should().BeGreaterThan(20 * 1024 * 1024); // Перепрыгнуло 20+ МБ
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseLogAsync_ReportsProgressMultipleTimes_WhenReadingLargeData()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"progress_test_{Guid.NewGuid():N}.lgp");
        var dict = new LgfDictionary();

        try
        {
            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var entry = System.Text.Encoding.UTF8.GetBytes("{20260903120000,N,{0,0},0,0,1,0,1,I,\"line\",0,\"\",0,0,0,0}\r\n");
                var chunk = new byte[entry.Length * 500];
                for (var i = 0; i < 500; i++)
                    Buffer.BlockCopy(entry, 0, chunk, i * entry.Length, entry.Length);

                for (var c = 0; c < 500; c++) // ~15 MB
                {
                    await fs.WriteAsync(chunk);
                }
            }

            var reports = new List<EventLogParser.LogReadProgress>();
            var progress = new SynchronousProgress<EventLogParser.LogReadProgress>(reports.Add);

            await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, progress, default))
            {
            }

            reports.Count.Should().BeGreaterThan(2);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }
    [Fact]
    public void ParseEntry_WithBracesInTokens_ShouldCleanBracesAndExtractCorrectUser()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "<Не указан>";
        dict.Users["15"] = "Кассир 1";

        // Запись с артефактом закрывающей скобки в токенах: userKey="1}"
        var rawEntry = @"{20260913130834,C,{24564c296f320,2301d2dea4},1},0,1,21726,1,I,"""",0,"""","""",1,1,1,31679}";
        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.User.Should().Be("<Не указан>");
        doc.Tran.Should().Be("C(24564c296f320,2301d2dea4)");
        doc.Session.Should().Be("31679");
    }

    [Fact]
    public void ParseEntry_SessionUserPropagation_ShouldEnrichSubsequentEventsInSameSession()
    {
        var dict = new LgfDictionary();
        dict.Users["1"] = "<Не указан>";
        dict.Events["1"] = "_$Transaction$_.Begin";
        dict.Events["2"] = "_$Session$_.Authentication";
        dict.Events["3"] = "_$Session$_.Finish";

        // 1. Событие аутентификации пользователя в сессии 32677
        var authEntry = @"{20260913130855,N,{0,0},1,1,1,23257,2,I,"""",0,{""P"",{1,{""S"",""CORP\Ivanov.II""}},{2,{""S"",""Иванов И.И.""}}},"""",1,1,1,32677}";
        var authDoc = EventLogParser.ParseEntry(authEntry, dict);

        authDoc.Should().NotBeNull();
        authDoc!.User.Should().Be("Иванов И.И.");
        authDoc.DataPresentation.Should().Contain("CORP\\Ivanov.II");

        // 2. Последующее событие транзакции в том же сеансе 32677 без явного пользователя (UserIndex = 1)
        var tranEntry = @"{20260913130855,C,{24564c29a2770,230247838b},1,1,1,23257,1,I,"""",0,"""","""",1,1,1,32677}";
        var tranDoc = EventLogParser.ParseEntry(tranEntry, dict);

        tranDoc.Should().NotBeNull();
        tranDoc!.User.Should().Be("Иванов И.И."); // Автоматически обогащено из контекста сессии!

        // 3. Завершение сеанса
        var finishEntry = @"{20260913131000,N,{0,0},1,1,1,23257,3,I,"""",0,"""","""",1,1,1,32677}";
        EventLogParser.ParseEntry(finishEntry, dict);

        // Сессионный кэш очищен
        dict.SessionUsers.Should().NotContainKey("32677");
    }

    [Fact]
    public void ParseEntry_AuthError_ShouldNotProduceRogueBraces()
    {
        var dict = new LgfDictionary();
        // Ошибка аутентификации с пустыми параметрами
        var rawEntry = @"{20260913130925,N,{0,0},1,1,1,20924,4,I,"""",0,{""P"",{1,{""S"",""""}},{2,{""S"",""""}}},"""",1,1,1,32699}";
        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.User.Should().NotBe("}, {");
        doc.Data.Should().NotBe("}, {");
    }

    [Fact]
    public void ParseDictionaryBlock_SubsequentEmptyUserBlock_ShouldNotOverwriteValidUserName()
    {
        var dict = new LgfDictionary();
        // 1. Первый блок словаря с именем пользователя
        EventLogParser.ParseDictionaryBlock("{1,b7b05a76-0000-0000-0000-000000000000,\"ИвановаТЮ\",12}", dict);
        dict.Users["12"].Should().Be("ИвановаТЮ");
        dict.UserUuids["12"].Should().Be("b7b05a76-0000-0000-0000-000000000000");

        // 2. Последующий служебный блок без имени для того же кода
        EventLogParser.ParseDictionaryBlock("{1,00000000-0000-0000-0000-000000000000,\"\",12}", dict);
        // Не должен быть перезаписан в User #12!
        dict.Users["12"].Should().Be("ИвановаТЮ");
    }

    [Fact]
    public void ParseEntry_BackgroundJobWithoutUser_ShouldSetUserToBackgroundJob()
    {
        var dict = new LgfDictionary();
        dict.Apps["3"] = "BackgroundJob";
        dict.Events["1"] = "Фоновое задание. Запуск";

        // Запись фонового задания с кодом пользователя 8 (у которого нет имени)
        var rawEntry = @"{20260913134000,N,{0,0},8,1,3,100,1,I,"""",0,"""","""",1,1,1,33845}";
        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.AppTypeName.Should().Be("Фоновое задание");
        doc.User.Should().Be("Фоновое задание");
    }

    [Fact]
    public void ParseEntry_WithLargeBase64Comment_ShouldTruncateBase64()
    {
        var dict = new LgfDictionary();
        dict.Events["1"] = "Тест";

        var fakeBase64 = new string('A', 500);
        var rawEntry = $$"""{20260914100000,N,{0,0},0,0,0,0,1,I,"JSON data: {\"image\": \"{{fakeBase64}}\"}",0,"","",1,1,1,100}""";

        var doc = EventLogParser.ParseEntry(rawEntry, dict);

        doc.Should().NotBeNull();
        doc!.Comment.Should().Contain("Двоичные данные Base64: 500 симв.");
        doc.Comment.Length.Should().BeLessThan(200);
    }

    [Fact]
    public async Task ParseLogAsync_WithChunks_ShouldParseAllEntriesWithoutDuplicatesOrLoss()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"chunk_test_{Guid.NewGuid():N}.lgp");
        try
        {
            var dict = new LgfDictionary();
            dict.Events["1"] = "TestEvent";

            var sb = new StringBuilder();
            for (int i = 0; i < 20; i++)
            {
                sb.AppendLine($$"""{202609141200{{i:D2}},N,{0,0},0,0,0,0,1,I,"Event number {{i}}",0,"","",1,1,1,100}""");
            }
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            var fileLength = new FileInfo(tempFile).Length;
            var middle = fileLength / 2;

            var chunk1Docs = new List<EventLogDoc>();
            await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, startOffset: 0, endOffset: middle))
            {
                chunk1Docs.Add(doc);
            }

            var chunk2Docs = new List<EventLogDoc>();
            await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, startOffset: middle, endOffset: long.MaxValue))
            {
                chunk2Docs.Add(doc);
            }

            var allDocs = chunk1Docs.Concat(chunk2Docs).ToList();
            allDocs.Should().HaveCount(20);

            // Проверяем непрерывность и отсутствие дублей
            var ids = allDocs.Select(d => d.Comment).ToHashSet();
            ids.Should().HaveCount(20);
            for (int i = 0; i < 20; i++)
            {
                ids.Should().Contain($"Event number {i}");
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task ParseLogAsync_WithMultiLineEntriesAndFourChunks_ShouldParseAllEntriesCorrectly()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"multiline_chunk_test_{Guid.NewGuid():N}.lgp");
        try
        {
            var dict = new LgfDictionary();
            dict.Events["1"] = "TestEvent";

            var sb = new StringBuilder();
            for (int i = 0; i < 40; i++)
            {
                sb.AppendLine($$"""
                    {202609141300{{i:D2}},N,{0,0},0,0,0,0,1,I,"Multi-line description
                    line 2 for event {{i}}
                    line 3 for event {{i}}",0,"","",1,1,1,100}
                    """);
            }
            await File.WriteAllTextAsync(tempFile, sb.ToString(), Encoding.UTF8);

            var fileLength = new FileInfo(tempFile).Length;
            int numChunks = 4;
            long chunkSize = fileLength / numChunks;

            var allDocs = new List<EventLogDoc>();
            for (int c = 0; c < numChunks; c++)
            {
                long start = c * chunkSize;
                long end = (c == numChunks - 1) ? long.MaxValue : (c + 1) * chunkSize;

                await foreach (var doc in EventLogParser.ParseLogAsync(tempFile, dict, startOffset: start, endOffset: end))
                {
                    allDocs.Add(doc);
                }
            }

            allDocs.Should().HaveCount(40);
            var comments = allDocs.Select(d => d.Comment).ToList();
            for (int i = 0; i < 40; i++)
            {
                comments.Should().Contain(c => c.Contains($"event {i}"));
            }
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ParseEntry_WithFilterEmptyTransactions_ShouldFilterBeginAndCommit()
    {
        var dict = new LgfDictionary();
        dict.Events["1"] = "_$Transaction$_.Begin";
        dict.Events["2"] = "_$Transaction$_.Commit";
        dict.Events["3"] = "_$Transaction$_.Rollback";
        dict.Events["4"] = "Login";

        var beginEntry = """{20260924100000,C,{1,100},1,1,1,1,I,"",0,"","",1,1,1,100}""";
        var commitEntry = """{20260924100001,C,{1,100},1,1,1,2,I,"",0,"","",1,1,1,100}""";
        var rollbackEntry = """{20260924100002,R,{1,100},1,1,1,3,I,"",0,"","",1,1,1,100}""";
        var loginEntry = """{20260924100003,N,{0,0},1,1,1,4,I,"",0,"","",1,1,1,100}""";

        // When filter is enabled (true)
        EventLogParser.ParseEntry(beginEntry, dict, filterEmptyTransactions: true).Should().BeNull();
        EventLogParser.ParseEntry(commitEntry, dict, filterEmptyTransactions: true).Should().BeNull();
        EventLogParser.ParseEntry(rollbackEntry, dict, filterEmptyTransactions: true).Should().NotBeNull();
        EventLogParser.ParseEntry(loginEntry, dict, filterEmptyTransactions: true).Should().NotBeNull();

        // When filter is disabled (false)
        EventLogParser.ParseEntry(beginEntry, dict, filterEmptyTransactions: false).Should().NotBeNull();
        EventLogParser.ParseEntry(commitEntry, dict, filterEmptyTransactions: false).Should().NotBeNull();
    }
}

file sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}


