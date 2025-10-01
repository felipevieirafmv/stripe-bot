using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

public class ComprarCommandModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComprarCommandModule> _logger;

    public ComprarCommandModule(IConfiguration configuration, ILogger<ComprarCommandModule> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [SlashCommand("comprar", "Comprar um plano específico")]
    public async Task ComprarPlanoEspecifico(string produto_id)
    {
        await DeferAsync(ephemeral: true);

        try
        {
            // Verificar se é uma chave mapeada no PlanMapping
            var priceId = _configuration.GetSection("PlanMapping")[produto_id];
            
            Stripe.Product product;
            Stripe.Price price;
            var priceService = new Stripe.PriceService();
            
            if (!string.IsNullOrEmpty(priceId))
            {
                // É uma chave mapeada, buscar o preço e depois o produto
                _logger.LogInformation($"Usando mapeamento: {produto_id} -> {priceId}");
                price = await priceService.GetAsync(priceId);
                
                if (price == null)
                {
                    await FollowupAsync("❌ Preço mapeado não encontrado.", ephemeral: true);
                    return;
                }
                
                // Buscar o produto a partir do preço
                var productService = new Stripe.ProductService();
                product = await productService.GetAsync(price.ProductId);
            }
            else
            {
                // Tentar como ID direto do produto
                var productService = new Stripe.ProductService();
                try
                {
                    product = await productService.GetAsync(produto_id);
                    
                    // Buscar preço padrão
                    if (string.IsNullOrEmpty(product.DefaultPriceId))
                    {
                        await FollowupAsync("❌ Este produto não possui preço configurado.", ephemeral: true);
                        return;
                    }
                    
                    price = await priceService.GetAsync(product.DefaultPriceId);
                }
                catch (StripeException)
                {
                    await FollowupAsync("❌ Produto não encontrado. Use uma chave válida do PlanMapping ou ID do produto.", ephemeral: true);
                    return;
                }
            }

            if (product == null || !product.Active)
            {
                await FollowupAsync("❌ Produto não encontrado ou indisponível.", ephemeral: true);
                return;
            }

            // Criar sessão de checkout
            var options = new SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<SessionLineItemOptions>
                {
                    new SessionLineItemOptions
                    {
                        Price = price.Id,
                        Quantity = 1,
                    },
                },
                Mode = "subscription",
                SuccessUrl = "https://discord.com/channels/@me",
                CancelUrl = "https://discord.com/channels/@me",
                Metadata = new Dictionary<string, string>
                {
                    { "discord_user_id", Context.User.Id.ToString() }
                }
            };

            var sessionService = new SessionService();
            var session = await sessionService.CreateAsync(options);

            // Criar embed simples e direto
            var unitAmount = price.UnitAmount ?? 0;
            var currency = price.Currency?.ToUpper() ?? "USD";
            var amountFormatted = unitAmount > 0 ? $"R$ {unitAmount / 100.0:F2}" : "Gratuito";
            
            var interval = price.Recurring?.Interval ?? "único";
            var intervalCount = price.Recurring?.IntervalCount ?? 1;
            var intervalText = intervalCount > 1 ? $"a cada {intervalCount} {interval}s" : $"por {interval}";

            // Determinar emoji baseado no preço
            var priceValue = unitAmount;
            var planEmoji = priceValue == 0 ? "🆓" :
                           priceValue < 2000 ? "⭐" :
                           priceValue < 5000 ? "💎" :
                           "👑";

            var embed = new EmbedBuilder()
                .WithTitle($"{planEmoji} {product.Name}")
                .WithDescription($"**Preço:** {amountFormatted} {currency} / {intervalText}\n\n" +
                               $"**Descrição:** {(!string.IsNullOrEmpty(product.Description) ? product.Description : "Plano premium com recursos exclusivos!")}\n\n" +
                               $"**Link de pagamento:**\n{session.Url}")
                .WithColor(Color.Green)
                .WithFooter("Pagamento processado pelo Stripe - 100% seguro!")
                .WithTimestamp(DateTimeOffset.Now);

            // Adicionar imagem se existir
            if (product.Images != null && product.Images.Any())
            {
                embed.WithThumbnailUrl(product.Images.First());
            }

            // Enviar no privado do usuário
            try
            {
                var dmChannel = await Context.User.CreateDMChannelAsync();
                await dmChannel.SendMessageAsync(embed: embed.Build());
                
                // Confirmar que enviou no privado
                await FollowupAsync("✅ Link de pagamento enviado no seu privado! Verifique suas mensagens diretas.", ephemeral: true);
                
                _logger.LogInformation($"Link de compra enviado no DM para {Context.User.Username} (ID: {Context.User.Id}) - Produto: {product.Name}");
            }
            catch (Exception dmEx)
            {
                _logger.LogWarning(dmEx, $"Não foi possível enviar DM para {Context.User.Username}. Enviando resposta ephemeral.");
                
                // Se não conseguir enviar DM, enviar resposta ephemeral
                await FollowupAsync(embed: embed.Build(), ephemeral: true);
                await FollowupAsync("⚠️ Não consegui enviar mensagem no privado. Certifique-se de que suas DMs estão abertas.", ephemeral: true);
            }
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Erro ao criar sessão de checkout do Stripe");
            await FollowupAsync("❌ Erro ao processar pagamento. Tente novamente mais tarde.", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar compra do plano");
            await FollowupAsync("❌ Erro interno. Tente novamente mais tarde.", ephemeral: true);
        }
    }
}

