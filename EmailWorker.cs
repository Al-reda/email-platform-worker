using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SQS;
using Amazon.SQS.Model;
using EmailPlatform.Email.Configuration;
using EmailPlatform.Shared;
using EmailPlatform.Shared.Clients;
using EmailPlatform.Shared.Contracts;
using Microsoft.Extensions.Options;

namespace EmailPlatform.Email;

/// <summary>
/// Long-running worker that drains the email queue.
///
/// Loop:
///   1. Long-poll SQS (up to 20s)
///   2. For each received message:
///        a. Fetch announcement from Storage (source of truth, not the queue body)
///        b. Send email via SES
///        c. PATCH status = Sent in Storage
///        d. Delete message from SQS
///   3. On failure: DON'T delete the message. SQS will redrive it after the
///      visibility timeout; after maxReceiveCount it lands in the DLQ. This is
///      how we get at-least-once with automatic backoff for free.
///
/// Factor 6: Stateless. Nothing kept across messages.
/// Factor 8: Scale horizontally — multiple workers, same queue, no coordination.
/// Factor 9: Respects stoppingToken. On SIGTERM we stop polling, let in-flight
///           messages finish. Un-acked messages return to queue automatically.
/// </summary>
public sealed class EmailWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IAmazonSQS _sqs;
    private readonly EmailSender _sender;
    private readonly IStorageClient _storage;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailWorker> _logger;

    public EmailWorker(
        IAmazonSQS sqs,
        EmailSender sender,
        IStorageClient storage,
        IOptions<EmailOptions> options,
        ILogger<EmailWorker> logger)
    {
        _sqs = sqs;
        _sender = sender;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_options.QueueUrl))
        {
            _logger.LogError("EMAIL__QUEUEURL is not set. Worker cannot start.");
            return;
        }

        _logger.LogInformation("Email worker started. Polling {QueueUrl}", _options.QueueUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var received = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = _options.QueueUrl,
                    WaitTimeSeconds = _options.ReceiveWaitSeconds,
                    MaxNumberOfMessages = _options.MaxMessagesPerReceive
                }, stoppingToken);

                if (received.Messages.Count == 0) continue;

                _logger.LogDebug("Received {Count} messages", received.Messages.Count);

                foreach (var msg in received.Messages)
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ProcessAsync(msg, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker loop iteration failed. Backing off 5s.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }

        _logger.LogInformation("Email worker loop exiting.");
    }

    private async Task ProcessAsync(Amazon.SQS.Model.Message message, CancellationToken ct)
    {
        EmailJobMessage? job;
        try
        {
            job = JsonSerializer.Deserialize<EmailJobMessage>(message.Body, JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Unparseable SQS message, deleting so it doesn't block the queue: {Body}",
                message.Body);
            await DeleteAsync(message.ReceiptHandle, ct);
            return;
        }

        if (job is null || string.IsNullOrWhiteSpace(job.AnnouncementId))
        {
            _logger.LogError("Message missing announcementId — deleting. Body={Body}", message.Body);
            await DeleteAsync(message.ReceiptHandle, ct);
            return;
        }

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["AnnouncementId"] = job.AnnouncementId,
            ["Attempt"] = job.Attempt
        });

        // Fetch fresh from Storage. Don't trust the queue body — the announcement
        // may have been edited (if still Pending when Scheduler enqueued, unlikely
        // but possible) or the content may have been changed by an admin.
        var announcement = await _storage.GetAsync(job.AnnouncementId, ct);
        if (announcement is null)
        {
            _logger.LogWarning("Announcement not found — deleting message (can't retry what doesn't exist).");
            await DeleteAsync(message.ReceiptHandle, ct);
            return;
        }

        if (announcement.Status == AnnouncementStatus.Sent)
        {
            _logger.LogInformation("Announcement already Sent — skipping (duplicate message).");
            await DeleteAsync(message.ReceiptHandle, ct);
            return;
        }

        try
        {
            await _sender.SendAsync(announcement, ct);
            await _storage.UpdateStatusAsync(
                announcement.AnnouncementId, AnnouncementStatus.Sent, null, ct);
            await DeleteAsync(message.ReceiptHandle, ct);
        }
        catch (Exception ex) when (IsUnrecoverable(ex))
        {
            // Hard failure (bad address, permanent SES reject). Mark as Failed
            // and delete — no point retrying.
            _logger.LogError(ex, "Unrecoverable send failure. Marking Failed and dropping message.");
            await _storage.UpdateStatusAsync(
                announcement.AnnouncementId, AnnouncementStatus.Failed, ex.Message, ct);
            await DeleteAsync(message.ReceiptHandle, ct);
        }
        catch (Exception ex)
        {
            // Transient failure. Do NOT delete. SQS will re-deliver after the
            // visibility timeout; after maxReceiveCount the message goes to DLQ.
            _logger.LogWarning(ex, "Transient send failure — message will be redelivered.");
        }
    }

    private async Task DeleteAsync(string receiptHandle, CancellationToken ct)
    {
        try
        {
            await _sqs.DeleteMessageAsync(_options.QueueUrl, receiptHandle, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete SQS message — it will be redelivered.");
        }
    }

    private static bool IsUnrecoverable(Exception ex)
    {
        // Conservative heuristic — expand in Task 13 after observing real SES error codes.
        return ex is FormatException
            || ex is ArgumentException
            || (ex is Amazon.SimpleEmailV2.AmazonSimpleEmailServiceV2Exception sesEx
                && sesEx.StatusCode == System.Net.HttpStatusCode.BadRequest);
    }
}
