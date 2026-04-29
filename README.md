# Email Platform — Email Worker

Background service that polls SQS for email jobs, sends via SES, and updates status in Storage.
Uses EmailPlatform.Shared from GitHub Packages.
