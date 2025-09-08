using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading;
using System.Threading.Tasks;

// Esta classe vai rodar em segundo plano (background) na nossa Web API
public class DiscordBotService : IHostedService
{
    private readonly ILogger<DiscordBotService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _serviceProvider;
    private InteractionService? _interactionService;
    public DiscordSocketClient? Client { get; private set; }

    // O construtor recebe o logger e a configuração (appsettings.json) automaticamente
    // graças à injeção de dependência do ASP.NET Core.
    public DiscordBotService(ILogger<DiscordBotService> logger, IConfiguration configuration, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
    }

    // Este método é chamado quando a aplicação Web API inicia
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.GuildMembers
        };
        Client = new DiscordSocketClient(config);

        // Configurar o sistema de interações
        _interactionService = new InteractionService(Client.Rest);
        await _interactionService.AddModulesAsync(typeof(DiscordBotService).Assembly, _serviceProvider);

        Client.Log += LogAsync;
        Client.Ready += OnReadyAsync;
        Client.InteractionCreated += OnInteractionCreatedAsync;

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

    private async Task OnReadyAsync()
    {
        _logger.LogInformation($"Bot conectado como {Client.CurrentUser.Username}");
        
        // Registrar comandos slash no servidor
        var guildIdStr = _configuration["DiscordGuildId"];
        if (!string.IsNullOrEmpty(guildIdStr) && ulong.TryParse(guildIdStr, out var guildId))
        {
            await _interactionService.RegisterCommandsToGuildAsync(guildId);
        }
        _logger.LogInformation("Comandos slash registrados no servidor.");
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        try
        {
            var context = new SocketInteractionContext(Client, interaction);
            var result = await _interactionService.ExecuteCommandAsync(context, _serviceProvider);
            
            if (!result.IsSuccess)
            {
                _logger.LogError($"Erro ao executar comando: {result.ErrorReason}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar interação");
        }
    }

    private Task LogAsync(LogMessage log)
    {
        _logger.LogInformation(log.ToString());
        return Task.CompletedTask;
    }
}
