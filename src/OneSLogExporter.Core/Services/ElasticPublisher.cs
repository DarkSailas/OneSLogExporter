using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using Microsoft.Extensions.Logging;
using OneSLogExporter.Core.Models;

namespace OneSLogExporter.Core.Services;

/// <summary>
/// Сервис массовой асинхронной публикации документов в Elasticsearch / OpenSearch с поддержкой маппингов и авторизации.
/// </summary>
public sealed class ElasticPublisher
{
    private readonly ElasticsearchClient _client;
    private readonly ElasticSettings _settings;
    private readonly ILogger<ElasticPublisher> _logger;

    public ElasticPublisher(ElasticSettings settings, ILogger<ElasticPublisher> logger)
    {
        _settings = settings;
        _logger = logger;

        var clientSettings = new ElasticsearchClientSettings(new Uri(settings.ServerUrl));

        if (!string.IsNullOrEmpty(settings.Username) && !string.IsNullOrEmpty(settings.Password))
        {
            clientSettings.Authentication(new BasicAuthentication(settings.Username, settings.Password));
        }
        else if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            clientSettings.Authentication(new ApiKey(settings.ApiKey));
        }

        _client = new ElasticsearchClient(clientSettings);
    }

    /// <summary>
    /// Автоматическое создание индекса Журнала Регистрации с типами полей при его отсутствии.
    /// </summary>
    public async ValueTask EnsureEventLogIndexAsync(string indexName, CancellationToken ct = default)
    {
        try
        {
            var existsResponse = await _client.Indices.ExistsAsync(indexName, ct).ConfigureAwait(false);
            if (!existsResponse.Exists)
            {
                _logger.LogInformation("Создание индекса Elasticsearch: {IndexName}", indexName);
                var createResponse = await _client.Indices.CreateAsync(indexName, c => c
                    .Mappings(m => m
                        .Properties<EventLogDoc>(p => p
                            .Keyword(d => d.Id!)
                            .Date(d => d.Date)
                            .Keyword(d => d.DateFormatted!)
                            .Keyword(d => d.Event!)
                            .Keyword(d => d.User!)
                            .Keyword(d => d.Meta!)
                            .Text(d => d.Tran!)
                            .Keyword(d => d.App!)
                            .Text(d => d.Comment!)
                            .Keyword(d => d.Importance!)
                            .Text(d => d.Session!)
                            .Text(d => d.Data!)
                        )
                    ), ct).ConfigureAwait(false);

                if (!createResponse.IsValidResponse)
                {
                    _logger.LogWarning("Предупреждение при создании индекса {IndexName}: {Debug}", indexName, createResponse.DebugInformation);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при проверке/создании индекса {IndexName}", indexName);
        }
    }

    /// <summary>
    /// Автоматическое создание индекса Технологического Журнала с типами полей при его отсутствии.
    /// </summary>
    public async ValueTask EnsureTechLogIndexAsync(string indexName, CancellationToken ct = default)
    {
        try
        {
            var existsResponse = await _client.Indices.ExistsAsync(indexName, ct).ConfigureAwait(false);
            if (!existsResponse.Exists)
            {
                _logger.LogInformation("Создание индекса Elasticsearch: {IndexName}", indexName);
                var createResponse = await _client.Indices.CreateAsync(indexName, c => c
                    .Mappings(m => m
                        .Properties<TechLogDoc>(p => p
                            .Keyword(d => d.Id!)
                            .Date(d => d.Date)
                            .Keyword(d => d.DateFormatted!)
                            .LongNumber(d => d.Duration!)
                            .FloatNumber(d => d.DurationMs!)
                            .FloatNumber(d => d.DurationSec!)
                            .Keyword(d => d.DurationFormatted!)
                            .Keyword(d => d.Event!)
                            .IntegerNumber(d => d.Level!)
                            .Keyword(d => d.ProcessName!)
                            .Keyword(d => d.ProcessId!)
                            .Keyword(d => d.User!)
                            .Keyword(d => d.App!)
                            .Keyword(d => d.ConnectId!)
                            .Keyword(d => d.ClientId!)
                            .Text(d => d.Context!)
                            .Text(d => d.Sql!)
                            .Text(d => d.Locks!)
                            .Text(d => d.WaitConnections!)
                            .Keyword(d => d.LkSrc!)
                            .Text(d => d.Descr!)
                            .LongNumber(d => d.Rows!)
                            .LongNumber(d => d.InBytes!)
                            .LongNumber(d => d.OutBytes!)
                            .Keyword(d => d.Method!)
                            .Keyword(d => d.Url!)
                            .Object(d => d.Properties)
                        )
                    ), ct).ConfigureAwait(false);

                if (!createResponse.IsValidResponse)
                {
                    _logger.LogWarning("Предупреждение при создании индекса {IndexName}: {Debug}", indexName, createResponse.DebugInformation);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при проверке/создании индекса {IndexName}", indexName);
        }
    }

    /// <summary>
    /// Массовая отправка документов Журнала Регистрации в индекс.
    /// </summary>
    public async ValueTask<(int Success, int Failed)> BulkIndexEventLogAsync(string indexName, IEnumerable<EventLogDoc> docs, CancellationToken ct = default)
    {
        var docList = docs.ToList();
        if (docList.Count == 0) return (0, 0);

        await EnsureEventLogIndexAsync(indexName, ct).ConfigureAwait(false);

        var bulkResponse = await _client.BulkAsync(b => b
            .Index(indexName)
            .IndexMany(docList, (descriptor, doc) => descriptor.Id(doc.Id)), ct).ConfigureAwait(false);

        if (!bulkResponse.IsValidResponse)
        {
            _logger.LogError("Ошибка Bulk индексации в {IndexName}: {Debug}", indexName, bulkResponse.DebugInformation);
            return (0, docList.Count);
        }

        var failed = bulkResponse.Items.Count(i => i.Error != null);
        var success = docList.Count - failed;
        return (success, failed);
    }

    /// <summary>
    /// Массовая отправка документов Технологического Журнала в индекс.
    /// </summary>
    public async ValueTask<(int Success, int Failed)> BulkIndexTechLogAsync(string indexName, IEnumerable<TechLogDoc> docs, CancellationToken ct = default)
    {
        var docList = docs.ToList();
        if (docList.Count == 0) return (0, 0);

        await EnsureTechLogIndexAsync(indexName, ct).ConfigureAwait(false);

        var bulkResponse = await _client.BulkAsync(b => b
            .Index(indexName)
            .IndexMany(docList, (descriptor, doc) => descriptor.Id(doc.Id)), ct).ConfigureAwait(false);

        if (!bulkResponse.IsValidResponse)
        {
            _logger.LogError("Ошибка Bulk индексации в {IndexName}: {Debug}", indexName, bulkResponse.DebugInformation);
            return (0, docList.Count);
        }

        var failed = bulkResponse.Items.Count(i => i.Error != null);
        var success = docList.Count - failed;
        return (success, failed);
    }
}
