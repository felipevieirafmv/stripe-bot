using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
    private readonly SubscriptionDbContext _dbContext;
    private readonly string? _webhookSecret;

    public StripeWebhookController(
        ILogger<StripeWebhookController> logger, 
        IConfiguration configuration, 
        DiscordBotService botService, 
        SubscriptionDbContext dbContext)
    {
        _logger = logger;
        _configuration = configuration;
        _botService = botService;
        _dbContext = dbContext;
        _webhookSecret = _configuration["Stripe:WebhookSecret"];
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        if (string.IsNullOrEmpty(_webhookSecret))
        {
            _logger.LogError("Webhook secret não configurado");
            return BadRequest();
        }

        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        try
        {
            var stripeEvent = EventUtility.ConstructEvent(json, Request.Headers["Stripe-Signature"], _webhookSecret);
            _logger.LogInformation($"Evento do Stripe recebido: {stripeEvent.Type}");

            switch (stripeEvent.Type)
            {
                case "checkout.session.completed":
                    var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
                    if (session != null)
                    {
                        await HandleCheckoutSessionCompleted(session);
                    }
                    break;

                case "customer.subscription.deleted":
                    var subscription = stripeEvent.Data.Object as Stripe.Subscription;
                    if (subscription != null)
                    {
                        await HandleSubscriptionDeleted(subscription);
                    }
                    break;
            }

            return Ok();
        }
        catch (StripeException e)
        {
            _logger.LogError(e, "Erro ao processar webhook do Stripe.");
            return BadRequest();
        }
    }

    private async Task HandleCheckoutSessionCompleted(Stripe.Checkout.Session session)
    {
        _logger.LogInformation($"Processando checkout session: {session.Id}");
        
        session.Metadata.TryGetValue("discord_user_id", out var discordUserIdStr);
        _logger.LogInformation($"Discord User ID extraído: {discordUserIdStr}");
        
        if (!ulong.TryParse(discordUserIdStr, out var discordUserId)) 
        { 
            _logger.LogWarning($"Não foi possível converter Discord User ID: {discordUserIdStr}");
            return; 
        }

        var sessionLineItemService = new SessionLineItemService();
        var lineItems = await sessionLineItemService.ListAsync(session.Id);
        var priceId = lineItems.Data[0].Price.Id;
        _logger.LogInformation($"Price ID encontrado: {priceId}");

        var roleIdStr = _configuration.GetValue<string>($"RoleMapping:{priceId}");
        _logger.LogInformation($"Role ID configurado: {roleIdStr}");
        
        if (string.IsNullOrEmpty(roleIdStr) || !ulong.TryParse(roleIdStr, out var roleId)) 
        { 
            _logger.LogWarning($"Role ID não encontrado ou inválido para price: {priceId}");
            return; 
        }
        
        // ... (lógica para adicionar o cargo no Discord, como antes) ...
        var guildIdStr = _configuration["DiscordGuildId"];
        if (string.IsNullOrEmpty(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId)) 
        {
            _logger.LogError("DiscordGuildId não configurado ou inválido");
            return;
        }
        
        _logger.LogInformation($"Guild ID: {guildId}, Discord User ID: {discordUserId}, Role ID: {roleId}");
        
        var guild = _botService.Client.GetGuild(guildId);
        if (guild == null)
        {
            _logger.LogError($"❌ Servidor Discord não encontrado com ID: {guildId}. Verifique se o bot está no servidor correto.");
            return;
        }
        
        _logger.LogInformation($"✅ Servidor encontrado: {guild.Name} (ID: {guild.Id})");
        _logger.LogInformation($"📊 Total de usuários no servidor: {guild.MemberCount}");
        
        // Verificar permissões do bot
        var botUser = guild.CurrentUser;
        var botPermissions = botUser.GetPermissions(guild.DefaultChannel);
        _logger.LogInformation($"🔐 Permissões do bot: ViewChannel={botPermissions.ViewChannel}, ManageRoles={botPermissions.ManageRoles}");
        
        if (!botPermissions.ManageRoles)
        {
            _logger.LogError($"❌ Bot não tem permissão 'Manage Roles' no servidor!");
            return;
        }
        
        // Tentar encontrar o usuário de várias formas
        var user = guild.GetUser(discordUserId);
        
        if (user == null)
        {
            _logger.LogWarning($"⚠️ Usuário não encontrado no cache. Tentando buscar no servidor...");
            
            // Tentar buscar o usuário diretamente no servidor
            try
            {
                // SocketGuild não tem GetUserAsync, vamos tentar outras abordagens
                _logger.LogInformation($"Tentando buscar usuário no cache atualizado...");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Erro ao buscar usuário: {ex.Message}");
            }
        }
        
        if (user == null)
        {
            _logger.LogWarning($"⚠️ Usuário ainda não encontrado. Tentando buscar todos os membros...");
            
            // Buscar todos os membros do servidor
            try
            {
                await guild.DownloadUsersAsync();
                user = guild.GetUser(discordUserId);
                _logger.LogInformation($"✅ Usuário encontrado após DownloadUsersAsync: {user?.Username}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Erro ao fazer DownloadUsersAsync: {ex.Message}");
            }
        }
        
        if (user == null)
        {
            _logger.LogError($"❌ Usuário Discord ID {discordUserId} não foi encontrado no servidor {guild.Name} (ID: {guildId}).");
            _logger.LogInformation($"💡 Possíveis causas:");
            _logger.LogInformation($"   - Bot não tem permissão 'View Server Members'");
            _logger.LogInformation($"   - Usuário saiu do servidor após a compra");
            _logger.LogInformation($"   - ID do usuário está incorreto");
            _logger.LogInformation($"   - Bot precisa ser reiniciado para atualizar cache");
            return;
        }
        
        _logger.LogInformation($"✅ Usuário encontrado: {user.Username} (ID: {user.Id})");
        
        var role = guild.GetRole(roleId);
        if (role == null)
        {
            _logger.LogError($"Cargo não encontrado no servidor com ID: {roleId}");
            return;
        }

        _logger.LogInformation($"Tentando adicionar cargo '{role.Name}' ao usuário '{user.Username}'");
        
        // Verificar permissões do bot para gerenciar o cargo
        var botRole = botUser.Roles.OrderByDescending(r => r.Position).FirstOrDefault();
        _logger.LogInformation($"Bot role mais alta: {botRole?.Name} (posição: {botRole?.Position})");
        _logger.LogInformation($"Cargo alvo: {role.Name} (posição: {role.Position})");
        
        if (botRole != null && role.Position >= botRole.Position)
        {
            _logger.LogError($"❌ Bot não tem permissão para gerenciar o cargo '{role.Name}' - cargo está na mesma posição ou acima do bot");
            return;
        }
        
        try
        {
            await user.AddRoleAsync(role);
            _logger.LogInformation($"✅ Cargo '{role.Name}' adicionado com sucesso ao usuário '{user.Username}'");
            
            // NOVO: Salva a associação no banco de dados
            try
            {
                var newSubscription = new UserSubscription
                {
                    StripeSubscriptionId = session.SubscriptionId,
                    DiscordUserId = discordUserId,
                    StripeCustomerId = session.CustomerId
                };
                _dbContext.UserSubscriptions.Add(newSubscription);
                await _dbContext.SaveChangesAsync();
                _logger.LogInformation($"Assinatura {session.SubscriptionId} salva no banco de dados.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Erro ao salvar assinatura {session.SubscriptionId} no banco de dados.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao adicionar cargo '{role.Name}' ao usuário '{user.Username}': {ex.Message}");
        }
    }

    private async Task HandleSubscriptionDeleted(Stripe.Subscription subscription)
    {
        var userSubscription = await _dbContext.UserSubscriptions
            .FirstOrDefaultAsync(s => s.StripeSubscriptionId == subscription.Id);

        if (userSubscription == null)
        {
            _logger.LogWarning($"Recebido evento de cancelamento para a assinatura {subscription.Id}, mas ela não foi encontrada no banco de dados.");
            return;
        }

        var priceId = subscription.Items.Data[0].Price.Id;
        var roleIdStr = _configuration.GetValue<string>($"RoleMapping:{priceId}");
        if (string.IsNullOrEmpty(roleIdStr) || !ulong.TryParse(roleIdStr, out var roleId)) { 
            _logger.LogWarning($"Role ID não encontrado para o price {priceId}");
            return; 
        }

        var guildIdStr = _configuration["DiscordGuildId"];
        if (string.IsNullOrEmpty(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId)) 
        {
            _logger.LogError("DiscordGuildId não configurado ou inválido");
            return;
        }
        var guild = _botService.Client.GetGuild(guildId);
        var user = guild?.GetUser(userSubscription.DiscordUserId);
        var role = guild?.GetRole(roleId);

        if (user != null && role != null)
        {
            await user.RemoveRoleAsync(role);
            _logger.LogInformation($"Cargo '{role.Name}' removido do usuário '{user.Username}' devido ao fim da assinatura.");
        }
        else
        {
            _logger.LogWarning($"Não foi possível remover o cargo do usuário {userSubscription.DiscordUserId} (cargo ou usuário não encontrado).");
        }
        
        try
        {
            _dbContext.UserSubscriptions.Remove(userSubscription);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation($"Assinatura {subscription.Id} removida do banco de dados.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao remover assinatura {subscription.Id} do banco de dados.");
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