using Discord;
using Discord.Interactions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;
using System.Collections.Generic;
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

    [SlashCommand("plano", "Comprar um plano de assinatura")]
    public async Task ComprarPlano(
        [Summary("plano", "Nome do plano (premium, vip, pro)")] string nomePlano)
    {
        await DeferAsync(ephemeral: true);

        try
        {
            // Verificar se o plano existe
            var priceId = _configuration.GetValue<string>($"PlanMapping:{nomePlano.ToLower()}");
            if (string.IsNullOrEmpty(priceId))
            {
                await FollowupAsync("❌ Plano não encontrado! Planos disponíveis: premium, vip, pro", ephemeral: true);
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
                        Price = priceId,
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

            var service = new SessionService();
            var session = await service.CreateAsync(options);

            // Enviar link no privado
            var embed = new EmbedBuilder()
                .WithTitle($"🛒 Compra do Plano {nomePlano.ToUpper()}")
                .WithDescription($"Clique no link abaixo para finalizar sua compra:")
                .AddField("Link de Pagamento", $"[Pagar Agora]({session.Url})")
                .AddField("Plano", nomePlano.ToUpper(), true)
                .AddField("Status", "Aguardando pagamento", true)
                .WithColor(Color.Green)
                .WithFooter("Após o pagamento, você receberá o cargo automaticamente!")
                .WithTimestamp(DateTimeOffset.Now)
                .Build();

            await Context.User.SendMessageAsync(embed: embed);
            await FollowupAsync($"✅ Link de pagamento enviado no seu privado! Verifique suas mensagens diretas.", ephemeral: true);

            _logger.LogInformation($"Link de compra enviado para {Context.User.Username} (ID: {Context.User.Id}) - Plano: {nomePlano}");
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Erro ao criar sessão de checkout do Stripe");
            await FollowupAsync("❌ Erro interno. Tente novamente mais tarde.", ephemeral: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Erro ao processar comando de compra");
            await FollowupAsync("❌ Erro interno. Tente novamente mais tarde.", ephemeral: true);
        }
    }
}
