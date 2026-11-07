using System.Text;
using ChainLoot.Backend.Hubs;
using Microsoft.AspNetCore.SignalR;
using MQTTnet;
using MQTTnet.Client;

namespace ChainLoot.Backend.Services;

public sealed class MqttBridge(IHubContext<HistoryHub> hub, IConfiguration configuration, ILogger<MqttBridge> logger) : BackgroundService
{
    private const string Topic = "chainloot/history";
    private readonly IMqttClient _client = new MqttFactory().CreateMqttClient();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _client.ApplicationMessageReceivedAsync += async e =>
        {
            var contract = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
            await hub.Clients.All.SendAsync("HistoryUpdated", contract, stoppingToken);
        };
        var host = configuration["Mqtt:Host"] ?? "localhost";
        var port = configuration.GetValue("Mqtt:Port", 1883);
        var options = new MqttClientOptionsBuilder().WithTcpServer(host, port).WithCleanSession().Build();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await _client.ConnectAsync(options, stoppingToken);
                    await _client.SubscribeAsync(Topic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce, stoppingToken);
                    logger.LogInformation("Połączono z MQTT.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("MQTT: {Error}", ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }

    public async Task PublishAsync(string contract, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected) return;
        try
        {
            var message = new MqttApplicationMessageBuilder().WithTopic(Topic).WithPayload(contract)
                .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce).Build();
            await _client.PublishAsync(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("Nie udało się wysłać powiadomienia MQTT: {Error}", ex.Message); }
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }
}
