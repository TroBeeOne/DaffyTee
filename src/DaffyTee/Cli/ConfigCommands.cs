using DaffyTee.Security;
using DaffyTee.Sinks;

namespace DaffyTee.Cli;

/// <summary>
/// `daffytee config ...` - settings that are secrets, kept in the encrypted store beside the users
/// rather than on a command line. Today that is the RabbitMQ URI, which carries a password.
/// </summary>
internal sealed class ConfigCommands(UserStore store, TextWriter output, Prompter prompter)
{
    public const string RabbitUriKey = "rabbit.uri";

    public static string Usage => """
          daffytee config rabbit [URI]     store the RabbitMQ URI, encrypted (prompts for it, and for a
                                           password the URI leaves out); used by --sink rabbit
          daffytee config rabbit --show    show it, password hidden
          daffytee config rabbit --clear   forget it
          daffytee config rabbit --test    connect and declare the exchange, to prove the URI works
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] != "rabbit")
        {
            output.WriteLine(Usage);
            return 1;
        }

        string? argument = args.Count > 1 ? args[1] : null;
        switch (argument)
        {
            case "--show":
                output.WriteLine(store.GetSetting(RabbitUriKey) is { } stored
                    ? $"  {new RabbitSettings(stored).SafeDescription}"
                    : "  no RabbitMQ URI stored");
                return 0;

            case "--clear":
                store.SetSetting(RabbitUriKey, null);
                output.WriteLine("  RabbitMQ URI removed");
                return 0;

            case "--test":
            {
                if (store.GetSetting(RabbitUriKey) is not { } saved)
                {
                    output.WriteLine("  no RabbitMQ URI stored - daffytee config rabbit URI");
                    return 1;
                }
                return await TestAsync(saved, cancellationToken) ? 0 : 1;
            }

            default:
            {
                string uri = argument ?? prompter.Ask("RabbitMQ URI (amqp://user@host:5672/vhost)");
                uri = CompleteUri(uri);
                if (!await TestAsync(uri, cancellationToken))
                {
                    output.WriteLine("  NOT saved.");
                    return 1;
                }

                store.SetSetting(RabbitUriKey, uri);
                output.WriteLine($"  saved: {new RabbitSettings(uri).SafeDescription}");
                return 0;
            }
        }
    }

    /// <summary>Asks for the password when the URI names a user but not a password.</summary>
    private string CompleteUri(string text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("amqp" or "amqps"))
            throw new ArgumentException($"'{text}' is not an amqp:// or amqps:// URI");

        if (uri.UserInfo.Length == 0 || uri.UserInfo.Contains(':')) return text;

        string password = prompter.AskSecret($"password for RabbitMQ user {Uri.UnescapeDataString(uri.UserInfo)}");
        var builder = new UriBuilder(uri) { Password = password };
        return builder.Uri.AbsoluteUri;
    }

    private async Task<bool> TestAsync(string uri, CancellationToken cancellationToken)
    {
        var settings = new RabbitSettings(uri);
        output.WriteLine($"  connecting to {settings.SafeDescription} ...");
        await using var sink = new RabbitMqSink(settings);
        try
        {
            await sink.PublishAsync([], cancellationToken);
            output.WriteLine("  ok: connected and the exchange is declared");
            return true;
        }
        catch (SinkUnavailableException ex)
        {
            output.WriteLine($"  FAILED: {ex.Message}");
            return false;
        }
    }
}
