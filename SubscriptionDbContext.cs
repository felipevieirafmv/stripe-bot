using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

public class UserSubscription
{
    [Key]
    public string StripeSubscriptionId { get; set; } = string.Empty;

    public ulong DiscordUserId { get; set; }
    
    public string StripeCustomerId { get; set; } = string.Empty;
}

public class SubscriptionDbContext : DbContext
{
    public DbSet<UserSubscription> UserSubscriptions { get; set; }

    public SubscriptionDbContext(DbContextOptions<SubscriptionDbContext> options)
        : base(options)
    {
    }
}
