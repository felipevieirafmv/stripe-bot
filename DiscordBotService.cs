using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

// Esta classe vai rodar em segundo plano (background) na nossa Web API
public class DiscordBotService : IHostedService
{
    private readonly ILogger<DiscordBotService> _logger;
    private readonly IConfiguration _configuration;
    public DiscordSocketClient Client { get; private set; }

    // O construtor recebe o logger e a configuração (appsettings.json) automaticamente
    // graças à injeção de dependência do ASP.NET Core.
    public DiscordBotService(ILogger<DiscordBotService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    // Este método é chamado quando a aplicação Web API inicia
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers
        };
        Client = new DiscordSocketClient(config);

        Client.Log += LogAsync;

        // Pega o token do nosso arquivo appsettings.json
        var token = _configuration["DiscordToken"];

        await Client.LoginAsync(TokenType.Bot, token);
        await Client.StartAsync();

        _logger.LogInformation("Serviço do Bot do Discord iniciado.");
    }

    // Este método é chamado quando a aplicação Web API para (ex: CTRL+C)
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Client.StopAsync();
        _logger.LogInformation("Serviço do Bot do Discord parado.");
    }

    private Task LogAsync(LogMessage log)
    {
        // Usa o sistema de logging do ASP.NET Core para exibir os logs do Discord
        _logger.LogInformation(log.ToString());
        return Task.CompletedTask;
    }
}