using System.Reflection;

namespace Nodus.Api.Common.Resolvers;

public static class SelectionResolverSetup
{
    /// <summary>
    /// Registers every implementation of <typeparamref name="TInterface"/> found in the assemblies (scoped, as the
    /// interface and as itself) and a resolver that selects one by key.
    /// </summary>
    public static IServiceCollection AddSelectionResolver<TInterface, TKey, TInput>(
        this IServiceCollection services,
        DictionarySetter<TInterface, TKey> dictionarySetter,
        KeySelector<TInterface, TKey, TInput> keySelector,
        FallbackKeySelector<TInterface, TKey, TInput> fallbackKeySelector,
        params Assembly[] assemblies)
        where TInterface : class
        where TKey : notnull =>
        services
            .Scan(scan => scan
                .FromAssemblies(assemblies)
                .AddClasses(classes => classes.AssignableTo<TInterface>().WithoutAttribute<RegisterIgnoreAttribute>())
                .As<TInterface>()
                .AsSelf()
                .WithScopedLifetime())
            .AddSingleton(dictionarySetter)
            .AddSingleton(keySelector)
            .AddSingleton(fallbackKeySelector)
            .AddSingleton<ISelectionDictionary<TInterface, TKey>, SelectionDictionary<TInterface, TKey>>()
            .AddScoped<ISelectionResolver<TInput, TInterface>, SelectionResolver<TInterface, TKey, TInput>>();

    public static IServiceCollection AddSelectionResolver<TInterface, TKey, TInput>(
        this IServiceCollection services,
        DictionarySetter<TInterface, TKey> dictionarySetter,
        KeySelector<TInterface, TKey, TInput> keySelector,
        params Assembly[] assemblies)
        where TInterface : class
        where TKey : notnull =>
        services.AddSelectionResolver(dictionarySetter, keySelector, (_, _) => false, assemblies);
}
