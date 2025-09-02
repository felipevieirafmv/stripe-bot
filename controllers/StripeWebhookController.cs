using System;
using System.IO;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

[Route("api/stripe-webhook")]
[ApiController]
public class StripeWebhookController : ControllerBase
{
    private readonly ILogger<StripeWebhookController> _logger;
    private readonly IConfiguration _configuration;
    private readonly DiscordBotService _botService;
    private readonly string? _webhookSecret;

    public StripeWebhookController(ILogger<StripeWebhookController> logger, IConfiguration configuration, DiscordBotService botService)
    {
        _logger = logger;
        _configuration = configuration;
        _botService = botService;
        _webhookSecret = _configuration["Stripe:WebhookSecret"] ?? throw new InvalidOperationException("Stripe:WebhookSecret não configurado");
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        try
        {
            var stripeHeader = Request.Headers["Stripe-Signature"];
            var stripeEvent = EventUtility.ConstructEvent(json, stripeHeader, _webhookSecret);

            _logger.LogInformation($"Evento do Stripe recebido: {stripeEvent.Type}");

            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                    var session = stripeEvent.Data.Object as Session;
                    if (session != null)
                    {
                        await HandleCheckoutSessionCompleted(session);
                    }
                    break;
                // Futuramente, você pode adicionar outros casos aqui
                // case Events.CustomerSubscriptionDeleted:
                //     var subscription = stripeEvent.Data.Object as Stripe.Subscription;
                //     // Lógica para remover o cargo
                //     break;
            }

            return Ok();
        }
        catch (StripeException e)
        {
            _logger.LogError(e, "Erro ao processar webhook do Stripe.");
            return BadRequest();
        }
    }

    private async Task HandleCheckoutSessionCompleted(Session session)
    {
        session.Metadata.TryGetValue("discord_user_id", out var discordUserIdStr);
        if (!ulong.TryParse(discordUserIdStr, out var discordUserId))
        {
            _logger.LogError($"Não foi possível encontrar ou converter o discord_user_id nos metadados da sessão {session.Id}");
            return;
        }

        var sessionLineItemService = new SessionLineItemService();
        var lineItems = await sessionLineItemService.ListAsync(session.Id);
        var priceId = lineItems.Data[0].Price.Id;

        var roleIdStr = _configuration[$"RoleMapping:{priceId}"];
        if (!ulong.TryParse(roleIdStr, out var roleId))
        {
            _logger.LogError($"Não foi encontrado um mapeamento de cargo para o Price ID {priceId}");
            return;
        }

        var guildId = 1407393107011436674ul;
        var guild = _botService.Client.GetGuild(guildId);
        if (guild == null)
        {
            _logger.LogError($"Bot não encontrou o servidor com ID {guildId}");
            return;
        }

        var user = guild.GetUser(discordUserId);
        var role = guild.GetRole(roleId);

        if (user != null && role != null)
        {
            await user.AddRoleAsync(role);
            _logger.LogInformation($"Cargo '{role.Name}' adicionado ao usuário '{user.Username}'");
        }
        else
        {
            _logger.LogError($"Usuário (ID: {discordUserId}) ou Cargo (ID: {roleId}) não encontrado.");
        }
    }

    [HttpGet("create-payment-link")]
    public async Task<IActionResult> CreatePaymentLink([FromQuery] string discordId)
    {
        if (string.IsNullOrEmpty(discordId))
        {
            return BadRequest(new { error = "O ID do Discord é obrigatório." });
        }

        var priceId = _configuration.GetSection("RoleMapping").GetChildren().FirstOrDefault()?.Key;

        if (string.IsNullOrEmpty(priceId))
        {
            _logger.LogError("Nenhum Price ID encontrado no RoleMapping do appsettings.json");
            return StatusCode(500, new { error = "Erro de configuração do servidor." });
        }

        var options = new SessionCreateOptions
        {
            PaymentMethodTypes = new List<string> { "card" },
            LineItems = new List<SessionLineItemOptions>
            {
                new SessionLineItemOptions
                {
                    Price = priceId,
                    Quantity = 1,
                },
            },
            Mode = "subscription",
            SuccessUrl = "https://discord.com/channels/@me",
            CancelUrl = "https://discord.com/channels/@me",
            
            Metadata = new Dictionary<string, string>
            {
                { "discord_user_id", discordId }
            }
        };

        try
        {
            var service = new SessionService();
            Session session = await service.CreateAsync(options);
            return Ok(new { url = session.Url });
        }
        catch (StripeException e)
        {
            _logger.LogError(e, "Erro ao criar sessão de checkout do Stripe.");
            return StatusCode(500, new { error = "Não foi possível criar o link de pagamento." });
        }
    }
}

