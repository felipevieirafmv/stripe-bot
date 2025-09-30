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

                case "product.created":
                    var product = stripeEvent.Data.Object as Stripe.Product;
                    if (product != null)
                    {
                        await HandleProductCreated(product);
                    }
                    break;

                case "product.deleted":
                    var deletedProduct = stripeEvent.Data.Object as Stripe.Product;
                    if (deletedProduct != null)
                    {
                        await HandleProductDeleted(deletedProduct);
                    }
                    break;

                case "product.updated":
                    var updatedProduct = stripeEvent.Data.Object as Stripe.Product;
                    if (updatedProduct != null)
                    {
                        await HandleProductUpdated(updatedProduct);
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

    private async Task HandleProductCreated(Stripe.Product product)
    {
        _logger.LogInformation($"Novo produto criado no Stripe: {product.Name} (ID: {product.Id})");
        
        try
        {
            // Criar estrutura no Discord (cargo, canal de texto e voz)
            await CreateDiscordStructureForProduct(product);
            
            // Enviar imagem do produto criado para o chat específico
            await SendProductImageToDiscord(product);
            
            // Enviar lista de todos os produtos para outro chat
            await SendAllProductsListToDiscord();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao processar criação do produto {product.Id}: {ex.Message}");
        }
    }

    private async Task HandleProductDeleted(Stripe.Product product)
    {
        _logger.LogInformation($"Produto deletado no Stripe: {product.Name} (ID: {product.Id})");
        
        try
        {
            // Buscar todos os planos ativos
            var activePlans = await GetActivePlans();
            
            // Enviar mensagem no chat Discord
            await SendActivePlansToDiscord(activePlans, product, "deleted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao processar exclusão do produto {product.Id}: {ex.Message}");
        }
    }

    private async Task HandleProductUpdated(Stripe.Product product)
    {
        _logger.LogInformation($"Produto atualizado no Stripe: {product.Name} (ID: {product.Id}) - Ativo: {product.Active}");
        
        try
        {
            // Verificar se o produto foi arquivado (active = false)
            if (!product.Active)
            {
                _logger.LogInformation($"Produto arquivado detectado: {product.Name} (ID: {product.Id})");
                
                // Buscar todos os planos ativos
                var activePlans = await GetActivePlans();
                
                // Enviar mensagem no chat Discord
                await SendActivePlansToDiscord(activePlans, product, "archived");
            }
            else
            {
                _logger.LogInformation($"Produto reativado: {product.Name} (ID: {product.Id})");
                
                // Opcional: também podemos notificar quando um produto é reativado
                var activePlans = await GetActivePlans();
                await SendActivePlansToDiscord(activePlans, product, "reactivated");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao processar atualização do produto {product.Id}: {ex.Message}");
        }
    }

    private async Task<List<Stripe.Price>> GetActivePlans()
    {
        var priceService = new Stripe.PriceService();
        var options = new Stripe.PriceListOptions
        {
            Active = true,
            Limit = 100 // Ajuste conforme necessário
        };
        
        var prices = await priceService.ListAsync(options);
        return prices.Data.ToList();
    }

    private async Task SendActivePlansToDiscord(List<Stripe.Price> activePlans, Stripe.Product product, string eventType)
    {
        try
        {
            var channelIdStr = "1407393107011436677";
            if (!ulong.TryParse(channelIdStr, out var channelId))
            {
                _logger.LogError("ID do canal Discord inválido");
                return;
            }

            var guildIdStr = _configuration["DiscordGuildId"];
            if (string.IsNullOrEmpty(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId))
            {
                _logger.LogError("DiscordGuildId não configurado ou inválido");
                return;
            }

            // Verificar se o bot está conectado
            if (_botService.Client?.ConnectionState != ConnectionState.Connected)
            {
                _logger.LogWarning("Bot do Discord não está conectado. Tentando reconectar...");
                return;
            }

            var guild = _botService.Client.GetGuild(guildId);
            if (guild == null)
            {
                _logger.LogError($"Servidor Discord não encontrado com ID: {guildId}");
                return;
            }

            var channel = guild.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                _logger.LogError($"Canal Discord não encontrado com ID: {channelId}");
                return;
            }

            // Configurar embed baseado no tipo de evento
            string title;
            Color color;
            
            switch (eventType)
            {
                case "created":
                    title = "🆕 Novo Produto Criado no Stripe!";
                    color = Color.Green;
                    break;
                case "deleted":
                    title = "🗑️ Produto Deletado no Stripe!";
                    color = Color.Red;
                    break;
                case "archived":
                    title = "📦 Produto Arquivado no Stripe!";
                    color = Color.Orange;
                    break;
                case "reactivated":
                    title = "♻️ Produto Reativado no Stripe!";
                    color = Color.Blue;
                    break;
                default:
                    title = "📝 Produto Atualizado no Stripe!";
                    color = Color.Gold;
                    break;
            }
            
            var embed = new EmbedBuilder()
                .WithTitle(title)
                .WithDescription($"**Produto:** {product.Name}")
                .WithColor(color)
                .WithTimestamp(DateTimeOffset.Now);

            // Preparar conteúdo da descrição com preço e metadados
            var descriptionContent = new System.Text.StringBuilder();
            
            // Buscar informações completas do produto para imagem e descrição
            var productService = new Stripe.ProductService();
            Stripe.Product fullProduct = null;
            
            try
            {
                fullProduct = await productService.GetAsync(product.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Não foi possível buscar informações completas do produto {product.Id}: {ex.Message}");
                // Usar produto do evento se não conseguir buscar
                fullProduct = product;
            }
            
            if (fullProduct != null)
            {
                // Adicionar imagem se existir
                if (fullProduct.Images != null && fullProduct.Images.Any())
                {
                    embed.WithImageUrl(fullProduct.Images.First());
                }
                
                // Adicionar descrição se existir
                if (!string.IsNullOrEmpty(fullProduct.Description))
                {
                    descriptionContent.AppendLine(fullProduct.Description);
                }
                
                // Buscar preço padrão do produto
                if (!string.IsNullOrEmpty(fullProduct.DefaultPriceId))
                {
                    try
                    {
                        var priceService = new Stripe.PriceService();
                        var defaultPrice = await priceService.GetAsync(fullProduct.DefaultPriceId);
                        if (defaultPrice != null)
                        {
                            var unitAmount = defaultPrice.UnitAmount ?? 0;
                            var currency = defaultPrice.Currency?.ToUpper() ?? "USD";
                            var amountFormatted = unitAmount > 0 ? $"R$ {unitAmount / 100.0:F2}" : "Gratuito";
                            
                            var interval = defaultPrice.Recurring?.Interval ?? "único";
                            var intervalCount = defaultPrice.Recurring?.IntervalCount ?? 1;
                            var intervalText = intervalCount > 1 ? $"a cada {intervalCount} {interval}s" : $"por {interval}";
                            
                            descriptionContent.AppendLine($"Preco: {amountFormatted} {currency} / {intervalText}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Não foi possível buscar preço padrão do produto {product.Id}: {ex.Message}");
                    }
                }
                
                // Adicionar metadados na mesma seção
                if (fullProduct.Metadata != null && fullProduct.Metadata.Any())
                {
                    foreach (var metadata in fullProduct.Metadata)
                    {
                        descriptionContent.AppendLine($"{metadata.Key}: {metadata.Value}");
                    }
                }
            }
            
            // Adicionar campo de descrição com todo o conteúdo
            if (descriptionContent.Length > 0)
            {
                embed.AddField("📝 Descrição:", descriptionContent.ToString(), false);
            }

            // Metadados agora são incluídos na seção de descrição

            if (activePlans.Any())
            {
                var plansDescription = new System.Text.StringBuilder();
                plansDescription.AppendLine("📋 **Planos Ativos Atualmente:**\n");

                foreach (var plan in activePlans.Take(10)) // Limitar a 10 planos para não sobrecarregar
                {
                    var unitAmount = plan.UnitAmount ?? 0;
                    var currency = plan.Currency?.ToUpper() ?? "USD";
                    var amountFormatted = unitAmount > 0 ? $"R$ {unitAmount / 100.0:F2}" : "Gratuito";
                    
                    var interval = plan.Recurring?.Interval ?? "único";
                    var intervalCount = plan.Recurring?.IntervalCount ?? 1;
                    
                    var intervalText = intervalCount > 1 ? $"a cada {intervalCount} {interval}s" : $"por {interval}";
                    
                    // Buscar nome do PlanMapping se existir
                    var planMappingName = GetPlanMappingName(plan.Id);
                    var displayName = !string.IsNullOrEmpty(planMappingName) ? planMappingName : (plan.Nickname ?? "Sem nome");
                    
                    plansDescription.AppendLine($"• **{displayName}**");
                    plansDescription.AppendLine($"  💰 {amountFormatted} {currency} / {intervalText}");
                    plansDescription.AppendLine($"  🆔 `{plan.Id}`\n");
                }

                if (activePlans.Count > 10)
                {
                    plansDescription.AppendLine($"... e mais {activePlans.Count - 10} planos ativos.");
                }

                embed.AddField("📊 Planos Ativos", plansDescription.ToString(), false);
            }
            else
            {
                embed.AddField("📊 Planos Ativos", "Nenhum plano ativo encontrado.", false);
            }

            await channel.SendMessageAsync(embed: embed.Build());
            _logger.LogInformation($"Mensagem enviada com sucesso no canal {channelId} sobre o produto {product.Id} ({eventType})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao enviar mensagem no Discord: {ex.Message}");
        }
    }

    private string GetPlanMappingName(string priceId)
    {
        var planMappingSection = _configuration.GetSection("PlanMapping");
        return planMappingSection.GetChildren()
            .FirstOrDefault(x => x.Value == priceId)?.Key ?? string.Empty;
    }

    private async Task CreateDiscordStructureForProduct(Stripe.Product product)
    {
        try
        {
            var guildIdStr = _configuration["DiscordGuildId"];
            if (string.IsNullOrEmpty(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId))
            {
                _logger.LogError("DiscordGuildId não configurado ou inválido");
                return;
            }

            // Verificar se o bot está conectado
            if (_botService.Client?.ConnectionState != ConnectionState.Connected)
            {
                _logger.LogWarning("Bot do Discord não está conectado. Tentando reconectar...");
                return;
            }

            var guild = _botService.Client.GetGuild(guildId);
            if (guild == null)
            {
                _logger.LogError($"Servidor Discord não encontrado com ID: {guildId}");
                return;
            }

            // Limpar nome do produto para usar como base
            var cleanProductName = CleanProductName(product.Name);
            var categoryName = $"{cleanProductName} - Sistema Automatizado";

            // 1. Criar categoria
            var category = await guild.CreateCategoryChannelAsync(categoryName);
            _logger.LogInformation($"Categoria criada: {categoryName}");

            // 2. Criar cargo
            var roleName = cleanProductName.Replace(" ", "_").ToLower();
            var role = await guild.CreateRoleAsync(roleName, color: Color.Blue, isMentionable: false);
            _logger.LogInformation($"Cargo criado: {roleName}");

            // 3. Criar canal de texto
            var textChannelName = $"{roleName}-anotacoes";
            var textChannel = await guild.CreateTextChannelAsync(textChannelName, props =>
            {
                props.CategoryId = category.Id;
                props.Topic = $"Canal de anotações para {product.Name}";
            });
            _logger.LogInformation($"Canal de texto criado: {textChannelName}");

            // 4. Criar canal de voz
            var voiceChannelName = cleanProductName;
            var voiceChannel = await guild.CreateVoiceChannelAsync(voiceChannelName, props =>
            {
                props.CategoryId = category.Id;
                props.UserLimit = 10; // Limite de 10 usuários
            });
            _logger.LogInformation($"Canal de voz criado: {voiceChannelName}");

            // 5. Configurar permissões - tornar canais privados
            await ConfigureChannelPermissions(guild, category, textChannel, voiceChannel, role);

            _logger.LogInformation($"Estrutura Discord criada com sucesso para o produto: {product.Name}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao criar estrutura Discord para o produto {product.Id}: {ex.Message}");
        }
    }

    private string CleanProductName(string productName)
    {
        // Remover caracteres especiais e normalizar
        var cleanName = productName
            .Replace("@", "")
            .Replace("#", "")
            .Replace("$", "")
            .Replace("%", "")
            .Replace("^", "")
            .Replace("&", "")
            .Replace("*", "")
            .Replace("(", "")
            .Replace(")", "")
            .Replace("-", " ")
            .Replace("_", " ")
            .Trim();

        // Capitalizar primeira letra de cada palavra
        return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(cleanName.ToLower());
    }

    private async Task ConfigureChannelPermissions(IGuild guild, ICategoryChannel category, ITextChannel textChannel, IVoiceChannel voiceChannel, IRole role)
    {
        try
        {
            // Configurar permissões da categoria
            await category.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(viewChannel: PermValue.Deny));
            await category.AddPermissionOverwriteAsync(role, new OverwritePermissions(viewChannel: PermValue.Allow));

            // Configurar permissões do canal de texto
            await textChannel.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(
                viewChannel: PermValue.Deny,
                sendMessages: PermValue.Deny,
                readMessageHistory: PermValue.Deny
            ));
            await textChannel.AddPermissionOverwriteAsync(role, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                sendMessages: PermValue.Allow,
                readMessageHistory: PermValue.Allow,
                attachFiles: PermValue.Allow,
                embedLinks: PermValue.Allow
            ));

            // Configurar permissões do canal de voz
            await voiceChannel.AddPermissionOverwriteAsync(guild.EveryoneRole, new OverwritePermissions(
                viewChannel: PermValue.Deny,
                connect: PermValue.Deny,
                speak: PermValue.Deny
            ));
            await voiceChannel.AddPermissionOverwriteAsync(role, new OverwritePermissions(
                viewChannel: PermValue.Allow,
                connect: PermValue.Allow,
                speak: PermValue.Allow
            ));

            _logger.LogInformation("Permissões configuradas com sucesso para os canais privados");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao configurar permissões dos canais: {ex.Message}");
        }
    }

    private async Task SendProductImageToDiscord(Stripe.Product product)
    {
        try
        {
            var channelIdStr = _configuration["DiscordChannels:atualizacoes_planos"]; // Chat para imagem do produto
            if (!ulong.TryParse(channelIdStr, out var channelId))
            {
                _logger.LogError("ID do canal Discord inválido");
                return;
            }

            var guildIdStr = _configuration["DiscordGuildId"];
            if (string.IsNullOrEmpty(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId))
            {
                _logger.LogError("DiscordGuildId não configurado ou inválido");
                return;
            }

            // Verificar se o bot está conectado
            if (_botService.Client?.ConnectionState != ConnectionState.Connected)
            {
                _logger.LogWarning("Bot do Discord não está conectado. Tentando reconectar...");
                return;
            }

            var guild = _botService.Client.GetGuild(guildId);
            if (guild == null)
            {
                _logger.LogError($"Servidor Discord não encontrado com ID: {guildId}");
                return;
            }

            var channel = guild.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                _logger.LogError($"Canal Discord não encontrado com ID: {channelId}");
                return;
            }

            // Buscar informações completas do produto
            var productService = new Stripe.ProductService();
            Stripe.Product fullProduct = null;
            
            try
            {
                fullProduct = await productService.GetAsync(product.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Não foi possível buscar informações completas do produto {product.Id}: {ex.Message}");
                fullProduct = product;
            }

            // Criar embed com informações do produto
            var embed = new EmbedBuilder()
                .WithTitle("🆕 Novo Produto Criado!")
                .WithDescription($"**{product.Name}**")
                .WithColor(Color.Green)
                .WithTimestamp(DateTimeOffset.Now);

            // Adicionar imagem se existir
            if (fullProduct?.Images != null && fullProduct.Images.Any())
            {
                embed.WithImageUrl(fullProduct.Images.First());
            }

            // Preparar informações do produto
            var productInfo = new System.Text.StringBuilder();
            
            if (!string.IsNullOrEmpty(fullProduct?.Description))
            {
                productInfo.AppendLine(fullProduct.Description);
            }
            
            // Buscar preço padrão
            if (!string.IsNullOrEmpty(fullProduct?.DefaultPriceId))
            {
                try
                {
                    var priceService = new Stripe.PriceService();
                    var defaultPrice = await priceService.GetAsync(fullProduct.DefaultPriceId);
                    if (defaultPrice != null)
                    {
                        var unitAmount = defaultPrice.UnitAmount ?? 0;
                        var currency = defaultPrice.Currency?.ToUpper() ?? "USD";
                        var amountFormatted = unitAmount > 0 ? $"R$ {unitAmount / 100.0:F2}" : "Gratuito";
                        
                        var interval = defaultPrice.Recurring?.Interval ?? "único";
                        var intervalCount = defaultPrice.Recurring?.IntervalCount ?? 1;
                        var intervalText = intervalCount > 1 ? $"a cada {intervalCount} {interval}s" : $"por {interval}";
                        
                        productInfo.AppendLine($"**Preço:** {amountFormatted} {currency} / {intervalText}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Não foi possível buscar preço do produto {product.Id}: {ex.Message}");
                }
            }
            
            // Adicionar metadados
            if (fullProduct?.Metadata != null && fullProduct.Metadata.Any())
            {
                foreach (var metadata in fullProduct.Metadata)
                {
                    productInfo.AppendLine($"**{metadata.Key}:** {metadata.Value}");
                }
            }

            if (productInfo.Length > 0)
            {
                embed.AddField("📋 Informações:", productInfo.ToString(), false);
            }

            await channel.SendMessageAsync(embed: embed.Build());
            _logger.LogInformation($"Imagem do produto {product.Id} enviada com sucesso no canal {channelId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao enviar imagem do produto no Discord: {ex.Message}");
        }
    }

    private async Task SendAllProductsListToDiscord()
    {
        try
        {
            var channelIdStr = _configuration["DiscordChannels:planos_ativos"]; // Chat para lista de produtos
            if (!ulong.TryParse(channelIdStr, out var channelId))
            {
                _logger.LogError("ID do canal Discord inválido");
                return;
            }

            var guildIdStr = _configuration["DiscordGuildId"];
            if (string.IsNullOrEmpty(guildIdStr) || !ulong.TryParse(guildIdStr, out var guildId))
            {
                _logger.LogError("DiscordGuildId não configurado ou inválido");
                return;
            }

            // Verificar se o bot está conectado
            if (_botService.Client?.ConnectionState != ConnectionState.Connected)
            {
                _logger.LogWarning("Bot do Discord não está conectado. Tentando reconectar...");
                return;
            }

            var guild = _botService.Client.GetGuild(guildId);
            if (guild == null)
            {
                _logger.LogError($"Servidor Discord não encontrado com ID: {guildId}");
                return;
            }

            var channel = guild.GetChannel(channelId) as IMessageChannel;
            if (channel == null)
            {
                _logger.LogError($"Canal Discord não encontrado com ID: {channelId}");
                return;
            }

            // Buscar todos os produtos ativos
            var productService = new Stripe.ProductService();
            var products = await productService.ListAsync(new Stripe.ProductListOptions
            {
                Active = true,
                Limit = 100
            });

            var embed = new EmbedBuilder()
                .WithTitle("📋 Lista de Todos os Produtos")
                .WithColor(Color.Blue)
                .WithTimestamp(DateTimeOffset.Now);

            if (products.Data.Any())
            {
                var productsList = new System.Text.StringBuilder();
                
                foreach (var product in products.Data.Take(20)) // Limitar a 20 produtos
                {
                    var productName = product.Name ?? "Sem nome";
                    
                    // Buscar preço padrão
                    string priceText = "Preço não definido";
                    if (!string.IsNullOrEmpty(product.DefaultPriceId))
                    {
                        try
                        {
                            var priceService = new Stripe.PriceService();
                            var defaultPrice = await priceService.GetAsync(product.DefaultPriceId);
                            if (defaultPrice != null)
                            {
                                var unitAmount = defaultPrice.UnitAmount ?? 0;
                                var currency = defaultPrice.Currency?.ToUpper() ?? "USD";
                                var amountFormatted = unitAmount > 0 ? $"R$ {unitAmount / 100.0:F2}" : "Gratuito";
                                
                                var interval = defaultPrice.Recurring?.Interval ?? "único";
                                var intervalCount = defaultPrice.Recurring?.IntervalCount ?? 1;
                                var intervalText = intervalCount > 1 ? $"a cada {intervalCount} {interval}s" : $"por {interval}";
                                
                                priceText = $"{amountFormatted} {currency} / {intervalText}";
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning($"Não foi possível buscar preço do produto {product.Id}: {ex.Message}");
                        }
                    }
                    
                    productsList.AppendLine($"**{productName}** - {priceText}");
                }

                if (products.Data.Count > 20)
                {
                    productsList.AppendLine($"\n... e mais {products.Data.Count - 20} produtos");
                }

                embed.AddField("🛍️ Produtos Disponíveis:", productsList.ToString(), false);
                embed.WithFooter($"Total: {products.Data.Count} produtos ativos");
            }
            else
            {
                embed.AddField("🛍️ Produtos Disponíveis:", "Nenhum produto ativo encontrado.", false);
            }

            await channel.SendMessageAsync(embed: embed.Build());
            _logger.LogInformation($"Lista de produtos enviada com sucesso no canal {channelId}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Erro ao enviar lista de produtos no Discord: {ex.Message}");
        }
    }

    // [HttpGet("test-product-created")]
    // public async Task<IActionResult> TestProductCreated()
    // {
    //     try
    //     {
    //         // Criar um produto fictício para teste com metadados
    //         var testProduct = new Stripe.Product
    //         {
    //             Id = "test_product_" + DateTimeOffset.Now.ToUnixTimeSeconds(),
    //             Name = "Produto de Teste - " + DateTime.Now.ToString("HH:mm:ss"),
    //             Description = "Este é um produto de teste criado para verificar o funcionamento do webhook com metadados e preço.",
    //             Url = "https://example.com/produto-teste",
    //             DefaultPriceId = "price_test_123", // Simular um preço padrão
    //             Metadata = new Dictionary<string, string>
    //             {
    //                 { "Horario", "19h30" },
    //                 { "Vagas", "5" },
    //                 { "Duracao", "4 horas" }
    //             }
    //         };

    //         _logger.LogInformation("Executando teste do webhook product.created...");
    //         await HandleProductCreated(testProduct);

    //         return Ok(new { message = "Teste executado com sucesso!" });
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError(ex, "Erro ao executar teste do webhook product.created");
    //         return StatusCode(500, new { error = ex.Message });
    //     }
    // }

    [HttpGet("test-plans-only")]
    public async Task<IActionResult> TestPlansOnly()
    {
        try
        {
            var activePlans = await GetActivePlans();
            
            var plansInfo = activePlans.Select(plan => new
            {
                id = plan.Id,
                nickname = plan.Nickname ?? "Sem nome",
                planMappingName = GetPlanMappingName(plan.Id),
                displayName = !string.IsNullOrEmpty(GetPlanMappingName(plan.Id)) ? GetPlanMappingName(plan.Id) : (plan.Nickname ?? "Sem nome"),
                amount = plan.UnitAmount ?? 0,
                currency = plan.Currency,
                interval = plan.Recurring?.Interval ?? "único",
                intervalCount = plan.Recurring?.IntervalCount ?? 1
            }).ToList();

            return Ok(new { 
                message = "Planos ativos encontrados!",
                totalPlans = activePlans.Count,
                plans = plansInfo
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar planos ativos");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    // [HttpGet("test-product-deleted")]
    // public async Task<IActionResult> TestProductDeleted()
    // {
    //     try
    //     {
    //         // Criar um produto fictício para teste
    //         var testProduct = new Stripe.Product
    //         {
    //             Id = "test_deleted_product_" + DateTimeOffset.Now.ToUnixTimeSeconds(),
    //             Name = "Produto Deletado - " + DateTime.Now.ToString("HH:mm:ss")
    //         };

    //         _logger.LogInformation("Executando teste do webhook product.deleted...");
    //         await HandleProductDeleted(testProduct);

    //         return Ok(new { message = "Teste de exclusão executado com sucesso!" });
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError(ex, "Erro ao executar teste do webhook product.deleted");
    //         return StatusCode(500, new { error = ex.Message });
    //     }
    // }

    // [HttpGet("test-product-archived")]
    // public async Task<IActionResult> TestProductArchived()
    // {
    //     try
    //     {
    //         // Criar um produto fictício arquivado para teste
    //         var testProduct = new Stripe.Product
    //         {
    //             Id = "test_archived_product_" + DateTimeOffset.Now.ToUnixTimeSeconds(),
    //             Name = "Produto Arquivado - " + DateTime.Now.ToString("HH:mm:ss"),
    //             Active = false // Simular produto arquivado
    //         };

    //         _logger.LogInformation("Executando teste do webhook product.updated (archived)...");
    //         await HandleProductUpdated(testProduct);

    //         return Ok(new { message = "Teste de arquivamento executado com sucesso!" });
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError(ex, "Erro ao executar teste do webhook product.updated (archived)");
    //         return StatusCode(500, new { error = ex.Message });
    //     }
    // }

    // [HttpGet("test-product-reactivated")]
    // public async Task<IActionResult> TestProductReactivated()
    // {
    //     try
    //     {
    //         // Criar um produto fictício reativado para teste
    //         var testProduct = new Stripe.Product
    //         {
    //             Id = "test_reactivated_product_" + DateTimeOffset.Now.ToUnixTimeSeconds(),
    //             Name = "Produto Reativado - " + DateTime.Now.ToString("HH:mm:ss"),
    //             Active = true // Simular produto reativado
    //         };

    //         _logger.LogInformation("Executando teste do webhook product.updated (reactivated)...");
    //         await HandleProductUpdated(testProduct);

    //         return Ok(new { message = "Teste de reativação executado com sucesso!" });
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError(ex, "Erro ao executar teste do webhook product.updated (reactivated)");
    //         return StatusCode(500, new { error = ex.Message });
    //     }
    // }
    
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