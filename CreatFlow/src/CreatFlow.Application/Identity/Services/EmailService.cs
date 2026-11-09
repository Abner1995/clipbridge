using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace CreatFlow.Application.Identity.Services;

/// <summary>
/// Email service using SMTP configs from system_configs table.
/// Uses MailKit (not deprecated System.Net.Mail.SmtpClient).
/// </summary>
public class EmailService
{
    private readonly ISystemConfigRepository _configRepo;

    public EmailService(ISystemConfigRepository configRepo)
    {
        _configRepo = configRepo;
    }

    /// <summary>
    /// Sends a test email to verify SMTP configuration.
    /// Reads host/port/username/password/ssl from system_configs.
    /// </summary>
    public async Task<(bool Success, string Error)> SendTestAsync(string toEmail)
    {
        // 1. Load and validate config
        var cfg = await LoadConfigAsync();

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(cfg.Host)) missing.Add("SMTP 服务器");
        if (cfg.Port <= 0) missing.Add("端口");
        if (string.IsNullOrWhiteSpace(cfg.Username)) missing.Add("发件邮箱");
        if (string.IsNullOrWhiteSpace(cfg.Password)) missing.Add("邮箱密码/授权码");
        if (missing.Count > 0)
            return (false, $"请先填写：{string.Join("、", missing)}");

        try
        {
            // 2. Build message
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress("CreatFlow", cfg.Username));
            message.To.Add(MailboxAddress.Parse(toEmail));
            message.Subject = "CreatFlow — 邮件发送测试";
            message.Body = new TextPart("plain")
            {
                Text = $"这是一封测试邮件。\n\n" +
                       $"如果你收到了这封邮件，说明 CreatFlow 的邮箱配置正确。\n\n" +
                       $"发送时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                       $"发件服务器：{cfg.Host}:{cfg.Port}"
            };

            // 3. Send
            using var client = new SmtpClient();

            // Outlook 个人账号用应用密码 → 必须跳过 XOAUTH2，否则认证失败
            client.AuthenticationMechanisms.Remove("XOAUTH2");

            await client.ConnectAsync(cfg.Host, cfg.Port,
                cfg.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
            await client.AuthenticateAsync(cfg.Username, cfg.Password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            return (true, "");
        }
        catch (AuthenticationException ex)
        {
            return (false, $"认证失败: {ex.Message}");
        }
        catch (SmtpCommandException ex) when (ex.ErrorCode == SmtpErrorCode.RecipientNotAccepted)
        {
            return (false, $"收件邮箱 {toEmail} 被拒绝");
        }
        catch (Exception ex)
        {
            return (false, $"发送失败: {ex.Message}");
        }
    }

    private async Task<EmailConfig> LoadConfigAsync()
    {
        var all = await _configRepo.GetAllAsync();
        var dict = all.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

        // Normalize host: strip protocol prefix, trim whitespace
        var rawHost = dict.GetValueOrDefault("email.host", "")?.Trim() ?? "";
        var host = rawHost
            .Replace("https://", "")
            .Replace("http://", "")
            .TrimEnd('/');

        return new EmailConfig(
            Host: host,
            Port: int.TryParse(dict.GetValueOrDefault("email.port", ""), out var p) ? p : 587,
            Username: (dict.GetValueOrDefault("email.username", "") ?? "").Trim(),
            Password: (dict.GetValueOrDefault("email.password", "") ?? "").Trim(),
            UseSsl: dict.GetValueOrDefault("email.use_ssl", "true")?.Trim() == "true"
        );
    }

    private record EmailConfig(string Host, int Port, string Username, string Password, bool UseSsl);
}
