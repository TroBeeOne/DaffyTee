using System.Text;
using System.Text.Json;
using RabbitMQ.Client;

namespace TeeProbe;

/// <summary>
/// TeeProbe --consume URI --queue NAME [--purge] [--show N]
///
/// Drains a RabbitMQ queue that DaffyTee publishes into and checks every message is a well-formed
/// event: parseable JSON, a CloudEvents 1.0 envelope, a routing key that matches its type, and no
/// id seen twice (DaffyTee delivers at least once, so duplicates are legal - but worth counting).
/// Prints how many of each type arrived, and any gap markers, so a script can assert on them.
/// </summary>
internal static class Consumer
{
    public static async Task<int> RunAsync(string uri, string queue, bool purgeOnly, int show)
    {
        var factory = new ConnectionFactory { Uri = new Uri(uri), ClientProvidedName = "teeprobe-consumer" };
        await using var connection = await factory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();

        if (purgeOnly)
        {
            uint purged = await channel.QueuePurgeAsync(queue);
            Console.WriteLine($"purged {purged} message(s) from {queue}");
            return 0;
        }

        var counts = new SortedDictionary<string, int>();
        var ids = new HashSet<string>();
        int messages = 0, duplicates = 0, invalid = 0;
        var gaps = new List<string>();
        var samples = new List<string>();

        while (await channel.BasicGetAsync(queue, autoAck: true) is { } delivery)
        {
            messages++;
            string body = Encoding.UTF8.GetString(delivery.Body.Span);
            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                string type = root.GetProperty("type").GetString()!;
                string id = root.GetProperty("id").GetString()!;

                bool envelopeOk = root.GetProperty("specversion").GetString() == "1.0"
                                  && root.GetProperty("source").GetString()!.StartsWith("daffytee://")
                                  && root.GetProperty("data").GetProperty("v").GetInt32() == 1
                                  && delivery.RoutingKey == type
                                  && delivery.BasicProperties.MessageId == id
                                  && delivery.BasicProperties.ContentType == "application/cloudevents+json";
                if (!envelopeOk)
                {
                    invalid++;
                    Console.WriteLine($"INVALID envelope: {body[..Math.Min(body.Length, 300)]}");
                }

                if (!ids.Add(id)) duplicates++;
                counts[type] = counts.GetValueOrDefault(type) + 1;

                if (type == "proxy.gap")
                {
                    var data = root.GetProperty("data");
                    gaps.Add($"dropped={data.GetProperty("dropped")} reasons={data.GetProperty("reasons")}");
                }

                if (samples.Count < show && type.StartsWith("request.")) samples.Add(body);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                invalid++;
                Console.WriteLine($"INVALID json: {ex.Message}");
            }
        }

        foreach (var (type, count) in counts) Console.WriteLine($"{type,-22} {count,6}");
        foreach (string gap in gaps) Console.WriteLine($"GAP {gap}");
        foreach (string sample in samples) Console.WriteLine(sample);
        Console.WriteLine($"messages={messages} invalid={invalid} duplicates={duplicates} gaps={gaps.Count}");
        return invalid == 0 ? 0 : 1;
    }
}
