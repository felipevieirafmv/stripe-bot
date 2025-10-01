using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using System.Linq;
using System.Threading.Tasks;

[Group("planos", "Comandos para visualizar planos disponíveis")]
public class PlanosCommandModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PlanosCommandModule> _logger;

    public PlanosCommandModule(IConfiguration configuration, ILogger<PlanosCommandModule> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    [SlashCommand("listar", "Lista todos os planos disponíveis com informações detalhadas")]
    public async Task ListarPlanos()
    {
        await DeferAsync(ephemeral: true);

        try
        {
            // Buscar todos os produtos ativos
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

            // Enviar mensagem inicial
            var embedInicial = new EmbedBuilder()
                .WithTitle("📋 Planos Disponíveis")
                .WithDescription($"**{products.Data.Count}** planos ativos encontrados. Enviando lista completa...")
                .WithColor(Color.Blue)
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await FollowupAsync(embed: embedInicial, ephemeral: true);

            // Processar todos os produtos em lotes de 5
            var todosProdutos = products.Data.ToList();
            var lotes = todosProdutos.Select((produto, index) => new { produto, index })
                                   .GroupBy(x => x.index / 5)
                                   .Select(g => g.Select(x => x.produto).ToList())
                                   .ToList();

            for (int i = 0; i < lotes.Count; i++)
            {
                var lote = lotes[i];
                var embed = new EmbedBuilder()
                    .WithTitle($"📋 Planos Disponíveis (Parte {i + 1}/{lotes.Count})")
                    .WithColor(Color.Blue)
                    .WithTimestamp(DateTimeOffset.Now);

                var planosLista = new System.Text.StringBuilder();

                foreach (var product in lote)
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

                    // Verificar se existe no PlanMapping
                    var planMappingKey = GetPlanMappingKey(product.DefaultPriceId);
                    var comandoCompra = !string.IsNullOrEmpty(planMappingKey) 
                        ? $"`/comprar {planMappingKey}`" 
                        : "❌ Não disponível para compra";

                    planosLista.AppendLine($"**{productName}**");
                    planosLista.AppendLine($"💰 {priceText}");
                    
                    if (!string.IsNullOrEmpty(product.Description))
                    {
                        var description = product.Description.Length > 50 
                            ? product.Description.Substring(0, 50) + "..." 
                            : product.Description;
                        planosLista.AppendLine($"📝 {description}");
                    }

                    // Adicionar metadados se existirem (limitado)
                    if (product.Metadata != null && product.Metadata.Any())
                    {
                        foreach (var metadata in product.Metadata.Take(2))
                        {
                            planosLista.AppendLine($"• {metadata.Key}: {metadata.Value}");
                        }
                    }

                    planosLista.AppendLine($"🛒 {comandoCompra}");
                    planosLista.AppendLine("");
                }

                // Garantir que não exceda o limite de 1024 caracteres
                var conteudoFinal = planosLista.ToString();
                if (conteudoFinal.Length > 1000)
                {
                    conteudoFinal = conteudoFinal.Substring(0, 1000) + "...";
                }

                embed.AddField("🛍️ Planos Disponíveis", conteudoFinal, false);
                embed.WithFooter($"Total: {products.Data.Count} planos ativos");

                // Enviar como follow-up
                await FollowupAsync(embed: embed.Build(), ephemeral: true);

                // Pequena pausa entre mensagens para evitar rate limit
                if (i < lotes.Count - 1)
                {
                    await Task.Delay(500);
                }
            }

            _logger.LogInformation($"Comando /planos executado por {Context.User.Username} (ID: {Context.User.Id}) - {products.Data.Count} planos listados");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao listar planos");
            await FollowupAsync("❌ Erro interno ao buscar planos. Tente novamente mais tarde.", ephemeral: true);
        }
    }

    private string GetPlanMappingKey(string priceId)
    {
        if (string.IsNullOrEmpty(priceId))
            return string.Empty;

        var planMappingSection = _configuration.GetSection("PlanMapping");
        return planMappingSection.GetChildren()
            .FirstOrDefault(x => x.Value == priceId)?.Key ?? string.Empty;
    }
}
