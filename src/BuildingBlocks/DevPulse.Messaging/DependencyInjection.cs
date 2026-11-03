using DevPulse.Contracts.Events.Common;
using DevPulse.Contracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DevPulse.Messaging;

public static class MessagingExtensions
{
    /// <summary>Connexion RabbitMQ et options (section <c>RabbitMq</c>). Le nom sert de « producer » et de nom de connexion.</summary>
    public static MessagingBuilder AddDevPulseMessaging(this IServiceCollection services, string serviceName)
    {
        services.AddOptions<RabbitMqOptions>().BindConfiguration(RabbitMqOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new ServiceName(serviceName));
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<OutboxSignal>();
        return new MessagingBuilder(services);
    }
}

public sealed class MessagingBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services;

    /// <summary>Événements écrits dans la base <typeparamref name="TDb"/> puis publiés par un distributeur en arrière-plan.</summary>
    public MessagingBuilder AddOutbox<TDb>() where TDb : DbContext
    {
        Services.AddScoped<IOutbox, EfOutbox<TDb>>();
        Services.AddHostedService<OutboxDispatcher<TDb>>();
        return this;
    }

    /// <summary>Consommateur transactionnel : traitement et trace « déjà traité » dans la même transaction de <typeparamref name="TDb"/>.</summary>
    public MessagingBuilder AddConsumer<TDb>(string queue, Action<ConsumerBuilder> configure) where TDb : DbContext =>
        AddConsumer(new ConsumerRegistration(queue, typeof(TDb)), configure);

    /// <summary>Consommateur sans base : un doublon reste possible, le traitement doit le tolérer.</summary>
    public MessagingBuilder AddConsumer(string queue, Action<ConsumerBuilder> configure)
    {
        Services.TryAddScoped<BufferedOutbox>();
        Services.TryAddScoped<IOutbox>(provider => provider.GetRequiredService<BufferedOutbox>());
        return AddConsumer(new ConsumerRegistration(queue, null), configure);
    }

    private MessagingBuilder AddConsumer(ConsumerRegistration registration, Action<ConsumerBuilder> configure)
    {
        if (!EventTopology.Bindings.ContainsKey(registration.Queue))
            throw new InvalidOperationException($"File inconnue de la topologie : {registration.Queue}");

        configure(new ConsumerBuilder(Services, registration));
        Services.AddSingleton<IHostedService>(provider => ActivatorUtilities.CreateInstance<QueueConsumer>(provider, registration));
        return this;
    }
}

public sealed class ConsumerBuilder(IServiceCollection services, ConsumerRegistration registration)
{
    public ConsumerBuilder Handle<TEvent, THandler>()
        where TEvent : IntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        var routingKey = EventSerializer.RoutingKeyOf(typeof(TEvent));

        // Un traitement déclaré pour un événement que la file ne reçoit pas ne s'exécuterait jamais : on refuse de démarrer.
        if (!EventTopology.Bindings[registration.Queue].Any(binding => Topology.Matches(binding, routingKey)))
            throw new InvalidOperationException($"{registration.Queue} n'est pas liée à {routingKey}.");

        services.TryAddScoped<THandler>();
        registration.Routes[routingKey] = new EventRoute(typeof(TEvent),
            (provider, e, ct) => provider.GetRequiredService<THandler>().HandleAsync((TEvent)e, ct));
        return this;
    }
}
