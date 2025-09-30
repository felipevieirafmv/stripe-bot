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
using System.Text;
using System.Threading.Tasks;

[Group("comprar", "Comandos para comprar planos")]
public class ComprarCommandModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<ComprarCommandModule> _logger;

    public ComprarCommandModule(IConfiguration configuration, ILogger<ComprarCommandModule> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [SlashCommand("planos", "Ver todos os planos disponíveis para compra")]
    public async Task VerPlanos()
    {
        await DeferAsync(ephemeral: true);

        try
        {
            // Buscar todos os produtos ativos do Stripe
            var productService = new Stripe.ProductService();
            var products = await productService.ListAsync(new Stripe.ProductListOptions
            {
                Active = true,
                Limit = 100
            });

            if (!products.Data.Any())
            {
                await FollowupAsync("❌ Nenhum plano disponível no momento.", ephemeral: true);
                return;
            }

            // Criar links diretos para cada produto
            var planosText = new StringBuilder();
            planosText.AppendLine("**Escolha um dos planos abaixo para ver mais detalhes e comprar:**\n");

            foreach (var product in products.Data.Take(10)) // Mostrar até 10 produtos
            {
                // Buscar preço
                var priceText = "Preço não definido";
                if (!string.IsNullOrEmpty(product.DefaultPriceId))
                {
                    try
                    {
                        var priceService = new Stripe.PriceService();
                        var price = await priceService.GetAsync(product.DefaultPriceId);
                        if (price != null)
                        {
                            var unitAmount = price.UnitAmount ?? 0;
                            var currency = price.Currency?.ToUpper() ?? "BRL";
                            var amountFormatted = unitAmount > 0 ? $"R$ {unitAmount / 100.0:F2}" : "Gratuito";
                            
                            var interval = price.Recurring?.Interval ?? "único";
                            var intervalCount = price.Recurring?.IntervalCount ?? 1;
                            var intervalText = intervalCount > 1 ? $"a cada {intervalCount} {interval}s" : $"por {interval}";
                            
                            priceText = $"{amountFormatted} {currency} / {intervalText}";
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Erro ao buscar preço do produto {product.Id}: {ex.Message}");
                    }
                }

                // Determinar emoji baseado no preço
                var priceValue = GetProductPrice(product);
                var emoji = priceValue == 0 ? "🆓" :
                           priceValue < 2000 ? "⭐" :
                           priceValue < 5000 ? "💎" :
                           "👑";

                var productName = product.Name ?? "Sem nome";
                var description = !string.IsNullOrEmpty(product.Description) ? product.Description : "Sem descrição";

                // Mostrar produto com ID para compra
                planosText.AppendLine($"**{emoji} {productName}**");
                planosText.AppendLine($"🆔 **ID:** `{product.Id}`");
                planosText.AppendLine($"💡 **Para comprar:** Use `/comprar {product.Id}`");
                planosText.AppendLine($"💰 **Preço:** {priceText}");
                
                // Adicionar metadados se existirem
                if (product.Metadata != null && product.Metadata.Any())
                {
                    foreach (var metadata in product.Metadata)
                    {
                        planosText.AppendLine($"{metadata.Key}: {metadata.Value}");
                    }
                }
                
                planosText.AppendLine($"📝 **Descrição:** {description}");
                planosText.AppendLine();
            }

            // Criar embed com lista de planos
            var embed = new EmbedBuilder()
                .WithTitle("🛍️ Planos Disponíveis")
                .WithDescription(planosText.ToString())
                .WithColor(Color.Blue)
                .WithTimestamp(DateTimeOffset.Now)
                .WithFooter($"Total: {products.Data.Count} planos disponíveis");

            // Adicionar imagem se algum produto tiver
            var firstProductWithImage = products.Data.FirstOrDefault(p => p.Images != null && p.Images.Any());
            if (firstProductWithImage != null)
            {
                embed.WithThumbnailUrl(firstProductWithImage.Images.First());
            }

            // Adicionar apenas um botão de ajuda
            var components = new ComponentBuilder()
                .WithButton(
                    label: "❓ Ajuda",
                    style: ButtonStyle.Link,
                    url: "https://discord.gg/seu-servidor"
                );

            await FollowupAsync(embed: embed.Build(), components: components.Build(), ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao buscar planos disponíveis");
            await FollowupAsync("❌ Erro interno. Tente novamente mais tarde.", ephemeral: true);
        }
    }


    private long GetProductPrice(Stripe.Product product)
    {
        if (string.IsNullOrEmpty(product.DefaultPriceId))
            return 0;

        try
        {
            var priceService = new Stripe.PriceService();
            var price = priceService.GetAsync(product.DefaultPriceId).Result;
            return price?.UnitAmount ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    [SlashCommand("comprar", "Comprar um plano específico")]
    public async Task ComprarPlanoEspecifico(string produto_id)
    {
        await DeferAsync(ephemeral: true);

        try
        {
            // Buscar informações do produto
            var productService = new Stripe.ProductService();
            var product = await productService.GetAsync(produto_id);

            if (product == null || !product.Active)
            {
                await FollowupAsync("❌ Produto não encontrado ou indisponível.", ephemeral: true);
                return;
            }

            // Buscar preço padrão
            if (string.IsNullOrEmpty(product.DefaultPriceId))
            {
                await FollowupAsync("❌ Este produto não possui preço configurado.", ephemeral: true);
                return;
            }

            var priceService = new Stripe.PriceService();
            var price = await priceService.GetAsync(product.DefaultPriceId);

            if (price == null)
            {
                await FollowupAsync("❌ Preço não encontrado para este produto.", ephemeral: true);
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
                               $"**Clique no botão abaixo para pagar de forma segura!**")
                .WithColor(Color.Green)
                .WithFooter("Pagamento processado pelo Stripe - 100% seguro!")
                .WithTimestamp(DateTimeOffset.Now);

            // Adicionar imagem se existir
            if (product.Images != null && product.Images.Any())
            {
                embed.WithThumbnailUrl(product.Images.First());
            }

            // Criar botão simples
            var components = new ComponentBuilder()
                .WithButton(
                    label: "💳 Pagar Agora",
                    style: ButtonStyle.Success,
                    url: session.Url
                )
                .WithButton(
                    label: "🔄 Ver Outros Planos",
                    style: ButtonStyle.Primary,
                    customId: "voltar_planos"
                );

            await FollowupAsync(embed: embed.Build(), components: components.Build(), ephemeral: true);

            _logger.LogInformation($"Link de compra enviado para {Context.User.Username} (ID: {Context.User.Id}) - Produto: {product.Name}");
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

    [ComponentInteraction("voltar_planos")]
    public async Task VoltarPlanos()
    {
        await DeferAsync(ephemeral: true);
        await VerPlanos();
    }
}

