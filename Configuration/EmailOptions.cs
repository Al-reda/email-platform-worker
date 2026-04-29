namespace EmailPlatform.Email.Configuration;

/// <summary>
/// Config for the Email Worker.
///
/// Factor 3: all values populated from env vars (EMAIL__*).
/// Factor 4: SQS queue + SES are attached resources — swap URLs via config.
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Full URL of the SQS queue feeding this worker.</summary>
    public string QueueUrl { get; set; } = "";

    /// <summary>Verified SES "From" address. Production must be a verified identity.</summary>
    public string Sender { get; set; } = "noreply@example.com";

    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Override endpoint for local dev (e.g. http://localhost:8000 for Moto).
    /// Leave null in production so the SDK uses real AWS regional endpoints.
    /// </summary>
    public string? ServiceUrl { get; set; }

    /// <summary>Long-poll seconds on ReceiveMessage (0-20). 20 = cheapest.</summary>
    public int ReceiveWaitSeconds { get; set; } = 20;

    /// <summary>Max messages per receive batch (1-10).</summary>
    public int MaxMessagesPerReceive { get; set; } = 10;
}
