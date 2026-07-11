using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;
using System;
using System.Threading.Tasks;

namespace WSChat.Application.Services;

public class EmailService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<bool> SendEmailAsync(string toEmail, string subject, string body, bool isHtml = false)
    {
        try
        {
            var smtpSettings = _configuration.GetSection("Smtp");
            var host = smtpSettings["Host"];
            var port = int.Parse(smtpSettings["Port"] ?? "587");
            var username = smtpSettings["Username"];
            var password = smtpSettings["Password"];
            var fromEmail = smtpSettings["FromEmail"];
            var fromName = smtpSettings["FromName"] ?? "WSChat System";

            _logger.LogInformation($"Host: {host}");
            _logger.LogInformation($"Port: {port}");
            _logger.LogInformation($"Username: {username}");
            _logger.LogInformation($"FromEmail: {fromEmail}");
            _logger.LogInformation($"FromName: {fromName}");

            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(username))
            {
                _logger.LogWarning("SMTP configuration is missing. Using mock email service.");
                return await SendMockEmailAsync(toEmail, subject, body);
            }

            if (string.IsNullOrEmpty(password))
            {
                _logger.LogError("SMTP password is empty!");
                return false;
            }

            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromEmail));
            message.To.Add(new MailboxAddress("", toEmail));
            message.Subject = subject;

            var bodyBuilder = new BodyBuilder();
            if (isHtml)
            {
                bodyBuilder.HtmlBody = body;
            }
            else
            {
                bodyBuilder.TextBody = body;
            }

            message.Body = bodyBuilder.ToMessageBody();

            _logger.LogInformation($"Connecting to SMTP server: {host}:{port}");

            using var client = new SmtpClient();

            client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            client.CheckCertificateRevocation = false;

            await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
            _logger.LogInformation("Connected to SMTP server");

            await client.AuthenticateAsync(username, password);
            _logger.LogInformation("Authenticated successfully");

            await client.SendAsync(message);
            _logger.LogInformation($"Email sent successfully to: {toEmail}");

            await client.DisconnectAsync(true);
            _logger.LogInformation("Disconnected from SMTP server");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to send email to: {toEmail}");

            return await SendMockEmailAsync(toEmail, subject, body);
        }
    }

    private async Task<bool> SendMockEmailAsync(string toEmail, string subject, string body)
    {
        try
        {
            _logger.LogInformation($"MOCK EMAIL - To: {toEmail}");
            _logger.LogInformation($"MOCK EMAIL - Subject: {subject}");
            _logger.LogInformation($"MOCK EMAIL - Body: {body}");
            _logger.LogInformation("--- In production, this would be sent via SMTP ---");

            await Task.Delay(500);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SendPasswordResetEmailAsync(string toEmail, string username, string resetToken)
    {
        var resetLink = $"{_configuration["Application:BaseUrl"]}/reset-password?token={resetToken}";

        var subject = "WSChat - Password Reset Request";
        var body = $@"
        Hello {username},

        You have requested a password reset for your WSChat account.

        To reset your password, please click the link below (valid for 1 hour):
        {resetLink}

        If you did not request this password reset, please ignore this email.

        Best regards,
        WSChat Team";

        return await SendEmailAsync(toEmail, subject, body);
    }

    public async Task<bool> SendWelcomeEmailAsync(string toEmail, string username)
    {
        var subject = "Welcome to WSChat!";
        var body = $@"
        Welcome {username}!

        Thank you for registering with WSChat. 

        Your account has been successfully created and is ready to use.

        Features available:
        - Real-time chat with other users
        - Voice messages (UDP)
        - Weather information (/weather <city>)
        - Latest news (/news [category])
        - Jokes and quotes (/joke, /quote)
        - Multiple chat rooms
        - Admin moderation tools

        If you have any questions, please contact support.

        Happy chatting!

        Best regards,
        WSChat Team";

        return await SendEmailAsync(toEmail, subject, body);
    }

    public async Task<bool> SendAdminNotificationAsync(string adminEmail, string eventType, string details)
    {
        var subject = $"WSChat Admin Alert: {eventType}";
        var body = $@"
        Admin Notification

        Event: {eventType}
        Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
        Details: {details}

        This is an automated notification from WSChat system.";

        return await SendEmailAsync(adminEmail, subject, body);
    }

    public async Task<bool> SendStatisticsReportAsync(string toEmail, string username, string reportHtml)
    {
        var subject = $"WSChat - Your Chat Statistics Report";
        var body = $@"
        Hello {username},

        Here is your requested chat statistics report.

        {reportHtml}

        You can always generate new reports from the chat interface.

        Best regards,
        WSChat Team";

        return await SendEmailAsync(toEmail, subject, body, isHtml: true);
    }
}
