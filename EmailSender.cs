using Amazon.SimpleEmailV2;
using Amazon.SimpleEmailV2.Model;
using EmailPlatform.Email.Configuration;
using EmailPlatform.Shared;
using Microsoft.Extensions.Options;

namespace EmailPlatform.Email;

/// <summary>
/// Thin wrapper around SES V2. Keeps the worker class focused on the queue
/// loop and makes SES calls easy to mock in tests.
/// </summary>
public sealed class EmailSender
{
    private readonly IAmazonSimpleEmailServiceV2 _ses;
    private readonly EmailOptions _options;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(
        IAmazonSimpleEmailServiceV2 ses,
        IOptions<EmailOptions> options,
        ILogger<EmailSender> logger)
    {
        _ses = ses;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Send a single email to all recipients. Real corporate announcements should
    /// use BCC for recipient privacy; we use ToAddresses here for simplicity.
    /// </summary>
    public async Task SendAsync(Announcement announcement, CancellationToken ct)
    {
        var request = new SendEmailRequest
        {
            FromEmailAddress = _options.Sender,
            Destination = new Destination
            {
                ToAddresses = announcement.Recipients.ToList()
            },
            Content = new EmailContent
            {
                Simple = new Message
                {
                    Subject = new Content { Data = announcement.Subject, Charset = "UTF-8" },
                    Body = new Body
                    {
                        Text = new Content { Data = announcement.Body, Charset = "UTF-8" }
                    }
                }
            }
        };

        var response = await _ses.SendEmailAsync(request, ct);
        _logger.LogInformation(
            "Sent announcement {AnnouncementId} to {RecipientCount} recipients; SES MessageId={MessageId}",
            announcement.AnnouncementId, announcement.Recipients.Count, response.MessageId);
    }
}
