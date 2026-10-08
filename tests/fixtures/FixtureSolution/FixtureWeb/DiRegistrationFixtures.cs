using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fixture.Web.Di;

// v0.14.0 fixture for get_di_registrations (docs/EXPANSION-SPECS.md §8): one registration per
// recognized shape, plus one that cannot be read statically. Never executed — slnmap only reads it.

public interface IClock
{
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.UtcNow;
}

public interface IStore<T>
{
}

public sealed class MemoryStore<T> : IStore<T>
{
}

public sealed class Settings
{
}

public sealed class Worker : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}

public static class DiRegistrations
{
    public static IServiceCollection AddFixtureServices(this IServiceCollection services, Type dynamicType)
    {
        services.AddScoped<IClock, SystemClock>();                                  // generic pair
        services.AddSingleton<Settings>();                                          // generic self
        services.AddTransient<IClock>(sp => new SystemClock());                     // factory, new X()
        services.AddTransient<Settings>(sp => sp.GetRequiredService<Settings>());   // factory, opaque
        services.AddSingleton<IClock>(new SystemClock());                           // instance
        services.AddScoped(typeof(IClock), typeof(SystemClock));                    // typeof pair
        services.AddScoped(typeof(IStore<>), typeof(MemoryStore<>));                // open generic
        services.AddKeyedSingleton<IClock, SystemClock>("utc");                     // keyed
        services.TryAddTransient<IClock, SystemClock>();                            // TryAdd
        services.AddHostedService<Worker>();                                        // hosted service
        services.AddScoped(dynamicType);                                            // unrecognized
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IClock, SystemClock>()); // descriptor: unrecognized, not dropped
        services.AddSingleton<IClock>(CreateClock);                                 // factory as a method group
        ServiceCollectionServiceExtensions.AddScoped<Settings>(services);           // static-call form
        return services;
    }

    private static IClock CreateClock(IServiceProvider provider) => new SystemClock();
}
